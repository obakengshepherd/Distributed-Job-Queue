using System.Text.Json;
using Dapper;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using DistributedJobQueue.Api.Models.Requests;
using DistributedJobQueue.Api.Models.Responses;
using DistributedJobQueue.Application.Interfaces;
using DistributedJobQueue.Domain.Entities;

namespace DistributedJobQueue.Infrastructure.Persistence;

// ════════════════════════════════════════════════════════════════════════════
// JOB REPOSITORY
// ════════════════════════════════════════════════════════════════════════════

public class JobRepository
{
    private readonly string _connectionString;

    public JobRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task<JobRecord?> FindByIdAsync(string jobId)
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT j.*, r.success, r.output, r.error_message, r.completed_at AS result_completed_at
            FROM jobs j
            LEFT JOIN job_results r ON r.job_id = j.id
            WHERE j.id = @JobId
            """;
        return await conn.QuerySingleOrDefaultAsync<JobRecord>(sql, new { JobId = jobId });
    }

    public async Task InsertJobAsync(JobRecord job, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO jobs (id, type, payload, status, priority, attempts, max_attempts, scheduled_at, created_at)
            VALUES (@Id, @Type, @Payload::jsonb, @Status::job_status, @Priority::job_priority,
                    @Attempts, @MaxAttempts, @ScheduledAt, @CreatedAt)
            """;
        await conn.ExecuteAsync(sql, job);
    }

    public async Task<bool> CancelJobAsync(string jobId)
    {
        using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync("""
            UPDATE jobs SET status = 'cancelled'::job_status
            WHERE id = @JobId AND status = 'pending'::job_status
            """, new { JobId = jobId });
        return rows > 0;
    }

    public async Task<IEnumerable<JobRecord>> ListJobsAsync(string? status, string? type, int limit, string? cursor)
    {
        using var conn = CreateConnection();
        var conditions = new List<string>();
        if (status is not null) conditions.Add("status = @Status::job_status");
        if (type is not null)   conditions.Add("type = @Type");
        if (cursor is not null) conditions.Add("id < @Cursor");
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        var sql = $"SELECT * FROM jobs {where} ORDER BY created_at DESC LIMIT @Limit";
        return await conn.QueryAsync<JobRecord>(sql, new { Status = status, Type = type, Cursor = cursor, Limit = limit });
    }

    // ── Worker claiming (SELECT FOR UPDATE SKIP LOCKED) ───────────────────────

    /// <summary>
    /// Atomically claims one available job using SELECT FOR UPDATE SKIP LOCKED.
    /// This is the distributed coordination mechanism — any number of workers
    /// can call this simultaneously without conflicts or duplicate claims.
    /// Returns null if no jobs are available.
    /// </summary>
    public async Task<JobRecord?> ClaimNextJobAsync(string workerId, NpgsqlConnection conn)
    {
        const string sql = """
            SELECT id, type, payload, attempts, max_attempts
            FROM jobs
            WHERE status = 'pending'::job_status
              AND scheduled_at <= NOW()
            ORDER BY priority ASC, created_at ASC
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """;

        var job = await conn.QuerySingleOrDefaultAsync<JobRecord>(sql);
        if (job is null) return null;

        await conn.ExecuteAsync("""
            UPDATE jobs
            SET status     = 'running'::job_status,
                worker_id  = @WorkerId,
                started_at = NOW(),
                attempts   = attempts + 1
            WHERE id = @JobId
            """, new { WorkerId = workerId, JobId = job.Id });

        return job;
    }

    public async Task CompleteJobAsync(string jobId, object? output, NpgsqlConnection conn)
    {
        await conn.ExecuteAsync("""
            UPDATE jobs
            SET status = 'completed'::job_status, completed_at = NOW()
            WHERE id = @JobId
            """, new { JobId = jobId });

        await conn.ExecuteAsync("""
            INSERT INTO job_results (id, job_id, success, output, completed_at)
            VALUES (@Id, @JobId, true, @Output::jsonb, NOW())
            """, new
        {
            Id     = $"res_{Guid.NewGuid():N}",
            JobId  = jobId,
            Output = output is not null ? JsonSerializer.Serialize(output) : null
        });
    }

    public async Task FailJobAsync(string jobId, string errorMessage, int attempts, int maxAttempts, NpgsqlConnection conn)
    {
        if (attempts < maxAttempts)
        {
            // Exponential backoff: 2^attempts seconds
            var backoffSeconds = (int)Math.Pow(2, attempts);
            await conn.ExecuteAsync("""
                UPDATE jobs
                SET status       = 'pending'::job_status,
                    worker_id    = NULL,
                    scheduled_at = NOW() + INTERVAL '1 second' * @Backoff
                WHERE id = @JobId
                """, new { JobId = jobId, Backoff = backoffSeconds });
        }
        else
        {
            await conn.ExecuteAsync("""
                UPDATE jobs SET status = 'dead'::job_status WHERE id = @JobId
                """, new { JobId = jobId });

            await conn.ExecuteAsync("""
                INSERT INTO dead_letter_jobs (id, original_job_id, job_type, failure_reason, attempts, moved_at)
                SELECT @Id, id, type, @FailureReason, attempts, NOW()
                FROM jobs WHERE id = @JobId
                """, new
            {
                Id            = $"dl_{Guid.NewGuid():N}",
                JobId         = jobId,
                FailureReason = errorMessage
            });

            await conn.ExecuteAsync("""
                INSERT INTO job_results (id, job_id, success, error_message, completed_at)
                VALUES (@Id, @JobId, false, @ErrorMessage, NOW())
                """, new
            {
                Id           = $"res_{Guid.NewGuid():N}",
                JobId        = jobId,
                ErrorMessage = errorMessage
            });
        }
    }

    public async Task UpdateWorkerHeartbeatAsync(string workerId, string? currentJobId, string status)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync("""
            UPDATE workers
            SET last_heartbeat_at = NOW(),
                current_job_id   = @CurrentJobId,
                status           = @Status::worker_status
            WHERE id = @WorkerId
            """, new { WorkerId = workerId, CurrentJobId = currentJobId, Status = status });
    }

    public async Task<string> RegisterWorkerAsync(string hostname, int processId)
    {
        using var conn = CreateConnection();
        var workerId = $"wrk_{Guid.NewGuid():N}";
        await conn.ExecuteAsync("""
            INSERT INTO workers (id, hostname, process_id, status, last_heartbeat_at, registered_at)
            VALUES (@Id, @Hostname, @ProcessId, 'idle'::worker_status, NOW(), NOW())
            """, new { Id = workerId, Hostname = hostname, ProcessId = processId });
        return workerId;
    }

    public async Task<IEnumerable<StaleWorkerRecord>> GetStaleWorkersAsync(TimeSpan threshold)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<StaleWorkerRecord>("""
            SELECT id, current_job_id
            FROM workers
            WHERE last_heartbeat_at < NOW() - @Threshold
              AND status != 'offline'::worker_status
            """, new { Threshold = threshold });
    }

    public async Task MarkWorkerOfflineAsync(string workerId)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync("""
            UPDATE workers SET status = 'offline'::worker_status WHERE id = @WorkerId
            """, new { WorkerId = workerId });
    }

    public async Task<QueueStatsData> GetQueueStatsAsync()
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleAsync<QueueStatsData>("""
            SELECT
                COUNT(*) FILTER (WHERE status = 'pending' AND priority = 'high')   AS pending_high,
                COUNT(*) FILTER (WHERE status = 'pending' AND priority = 'normal') AS pending_normal,
                COUNT(*) FILTER (WHERE status = 'pending' AND priority = 'low')    AS pending_low,
                COUNT(*) FILTER (WHERE status = 'running')    AS running,
                COUNT(*) FILTER (WHERE status = 'completed' AND completed_at >= NOW() - INTERVAL '1 day') AS completed_today,
                COUNT(*) FILTER (WHERE status = 'failed'    AND completed_at >= NOW() - INTERVAL '1 day') AS failed_today,
                COUNT(*) FILTER (WHERE status = 'dead')      AS dead_total,
                (SELECT COUNT(*) FROM workers WHERE status = 'idle')   AS workers_idle,
                (SELECT COUNT(*) FROM workers WHERE status = 'busy')   AS workers_active,
                (SELECT COUNT(*) FROM workers WHERE status = 'offline')AS workers_offline,
                (SELECT COUNT(*) FROM workers WHERE status != 'offline') AS workers_total
            FROM jobs
            """);
    }
}

public record JobRecord
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Payload { get; init; } = "{}";
    public string Status { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public int Attempts { get; init; }
    public int MaxAttempts { get; init; }
    public string? WorkerId { get; init; }
    public DateTimeOffset ScheduledAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    // From job_results join
    public bool? Success { get; init; }
    public string? Output { get; init; }
    public string? ErrorMessage { get; init; }
}

public record StaleWorkerRecord { public string Id { get; init; } = string.Empty; public string? CurrentJobId { get; init; } }
public record QueueStatsData
{
    public int PendingHigh { get; init; } public int PendingNormal { get; init; } public int PendingLow { get; init; }
    public int Running { get; init; } public int CompletedToday { get; init; } public int FailedToday { get; init; }
    public int DeadTotal { get; init; } public int WorkersIdle { get; init; } public int WorkersActive { get; init; }
    public int WorkersOffline { get; init; } public int WorkersTotal { get; init; }
}

namespace DistributedJobQueue.Application.Services;

// ════════════════════════════════════════════════════════════════════════════
// JOB SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class JobService : IJobService
{
    private readonly JobRepository _repo;
    private readonly IQueueService _queue;

    public JobService(JobRepository repo, IQueueService queue)
    {
        _repo  = repo;
        _queue = queue;
    }

    public async Task<JobResponse> SubmitJobAsync(SubmitJobRequest request, CancellationToken ct)
    {
        var jobId = $"job_{Guid.NewGuid():N}";
        var job = new JobRecord
        {
            Id          = jobId,
            Type        = request.Type,
            Payload     = JsonSerializer.Serialize(request.Payload),
            Status      = "pending",
            Priority    = (request.Priority ?? "NORMAL").ToLower(),
            Attempts    = 0,
            MaxAttempts = request.MaxAttempts,
            ScheduledAt = request.ScheduledAt ?? DateTimeOffset.UtcNow,
            CreatedAt   = DateTimeOffset.UtcNow
        };

        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await _repo.InsertJobAsync(job, conn);

        // Publish wake signal to RabbitMQ so idle workers poll immediately
        await _queue.PublishJobNotificationAsync(ct);

        return MapJob(job);
    }

    public async Task<JobResponse> GetJobAsync(string jobId, CancellationToken ct)
    {
        var job = await _repo.FindByIdAsync(jobId)
            ?? throw new JobNotFoundException(jobId);
        return MapJob(job);
    }

    public async Task<JobCancelledResponse> CancelJobAsync(string jobId, CancellationToken ct)
    {
        var cancelled = await _repo.CancelJobAsync(jobId);
        if (!cancelled) throw new JobNotCancellableException(jobId);
        return new JobCancelledResponse
        {
            Id          = jobId,
            Status      = "CANCELLED",
            CancelledAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<PagedApiResponse<JobResponse>> ListJobsAsync(ListJobsRequest query, CancellationToken ct)
    {
        var jobs = await _repo.ListJobsAsync(query.Status?.ToLower(), query.Type, query.Limit, query.Cursor);
        return new PagedApiResponse<JobResponse>
        {
            Data = jobs.Select(MapJob),
            Pagination = new PaginationMeta { Limit = query.Limit }
        };
    }

    private static JobResponse MapJob(JobRecord j) => new()
    {
        Id          = j.Id,
        Type        = j.Type,
        Status      = j.Status.ToUpper(),
        Priority    = j.Priority.ToUpper(),
        Attempts    = j.Attempts,
        MaxAttempts = j.MaxAttempts,
        ScheduledAt = j.ScheduledAt,
        StartedAt   = j.StartedAt,
        CompletedAt = j.CompletedAt,
        Result      = j.Success.HasValue ? new JobResultInfo
        {
            Success      = j.Success.Value,
            Output       = j.Output is not null ? JsonSerializer.Deserialize<object>(j.Output) : null,
            ErrorMessage = j.ErrorMessage
        } : null,
        CreatedAt = j.CreatedAt
    };
}

// ════════════════════════════════════════════════════════════════════════════
// WORKER SERVICE (heartbeat + watchdog)
// ════════════════════════════════════════════════════════════════════════════

public class WorkerService : IWorkerService
{
    private readonly JobRepository _repo;
    private readonly ILogger<WorkerService> _logger;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(60);

    public WorkerService(JobRepository repo, ILogger<WorkerService> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    public async Task RecordHeartbeatAsync(WorkerHeartbeatRequest request, CancellationToken ct)
    {
        await _repo.UpdateWorkerHeartbeatAsync(request.WorkerId, request.JobId, request.Status);
    }

    public async Task<IEnumerable<string>> DetectStaleWorkersAsync(CancellationToken ct)
    {
        var stale = await _repo.GetStaleWorkersAsync(StaleThreshold);
        return stale.Select(w => w.Id);
    }

    public async Task ReclaimOrphanedJobsAsync(IEnumerable<string> staleWorkerIds, CancellationToken ct)
    {
        var staleWorkers = (await _repo.GetStaleWorkersAsync(StaleThreshold)).ToList();

        foreach (var worker in staleWorkers)
        {
            _logger.LogWarning("Reclaiming jobs from stale worker {WorkerId}", worker.Id);
            await _repo.MarkWorkerOfflineAsync(worker.Id);

            if (worker.CurrentJobId is not null)
            {
                var job = await _repo.FindByIdAsync(worker.CurrentJobId);
                if (job is not null && job.Status == "running")
                {
                    await using var conn = _repo.CreateConnection();
                    await conn.OpenAsync(ct);
                    await _repo.FailJobAsync(
                        job.Id,
                        $"Worker {worker.Id} became stale",
                        job.Attempts,
                        job.MaxAttempts,
                        conn);
                    _logger.LogInformation("Reclaimed job {JobId} from stale worker {WorkerId}", job.Id, worker.Id);
                }
            }
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// QUEUE SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class QueueService : IQueueService
{
    private readonly JobRepository _repo;
    private readonly IConnection? _rabbitConn;
    private readonly ILogger<QueueService> _logger;
    private const string NotifyQueue = "jobs.notify";

    public QueueService(JobRepository repo, IConfiguration configuration, ILogger<QueueService> logger)
    {
        _repo   = repo;
        _logger = logger;
        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(configuration.GetConnectionString("RabbitMQ") ?? "amqp://devuser:devpass@localhost:5672/")
            };
            _rabbitConn = factory.CreateConnection();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RabbitMQ unavailable — workers will fall back to timer polling");
        }
    }

    public async Task<QueueStatsResponse> GetStatsAsync(CancellationToken ct)
    {
        var data = await _repo.GetQueueStatsAsync();
        return new QueueStatsResponse
        {
            QueueDepth = new QueueDepth
            {
                Pending        = new PriorityDepth { High = data.PendingHigh, Normal = data.PendingNormal, Low = data.PendingLow },
                Running        = data.Running,
                CompletedToday = data.CompletedToday,
                FailedToday    = data.FailedToday,
                Dead           = data.DeadTotal
            },
            Workers = new WorkerStats
            {
                Total   = data.WorkersTotal,
                Active  = data.WorkersActive,
                Idle    = data.WorkersIdle,
                Stale   = data.WorkersOffline
            },
            Performance  = new PerformanceStats(),
            SnapshotAt   = DateTimeOffset.UtcNow
        };
    }

    public async Task PublishJobNotificationAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        if (_rabbitConn is null) return;
        try
        {
            using var ch = _rabbitConn.CreateModel();
            ch.QueueDeclare(queue: NotifyQueue, durable: false, exclusive: false, autoDelete: false);
            ch.BasicPublish(exchange: "", routingKey: NotifyQueue, body: "{}"u8.ToArray());
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to publish job notification"); }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// BACKGROUND WORKER — the actual job processing loop
// Runs as a hosted service. Multiple instances = distributed workers.
// ════════════════════════════════════════════════════════════════════════════

public class JobWorkerBackgroundService : BackgroundService
{
    private readonly JobRepository _repo;
    private readonly IServiceProvider _services;
    private readonly ILogger<JobWorkerBackgroundService> _logger;
    private string? _workerId;

    public JobWorkerBackgroundService(
        JobRepository repo,
        IServiceProvider services,
        ILogger<JobWorkerBackgroundService> logger)
    {
        _repo     = repo;
        _services = services;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Register this worker instance in the database
        _workerId = await _repo.RegisterWorkerAsync(
            System.Net.Dns.GetHostName(),
            Environment.ProcessId);

        _logger.LogInformation("Job worker {WorkerId} started", _workerId);

        // Run heartbeat and job claiming in parallel
        var heartbeatTask = RunHeartbeatAsync(stoppingToken);
        var processingTask = RunProcessingLoopAsync(stoppingToken);
        await Task.WhenAny(heartbeatTask, processingTask);
    }

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _repo.UpdateWorkerHeartbeatAsync(_workerId!, null, "idle");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Heartbeat failed for worker {WorkerId}", _workerId); }
        }
    }

    private async Task RunProcessingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessOneJobAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job processing loop error for worker {WorkerId}", _workerId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ProcessOneJobAsync(CancellationToken ct)
    {
        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // SELECT FOR UPDATE SKIP LOCKED — atomic claim
        var job = await _repo.ClaimNextJobAsync(_workerId!, conn);

        if (job is null)
        {
            await tx.RollbackAsync(ct);
            // No jobs available — wait briefly before polling again
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            return;
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("Worker {WorkerId} claimed job {JobId} (type={Type})", _workerId, job.Id, job.Type);
        await _repo.UpdateWorkerHeartbeatAsync(_workerId!, job.Id, "busy");

        try
        {
            var result = await ExecuteJobAsync(job, ct);

            await using var completeConn = _repo.CreateConnection();
            await completeConn.OpenAsync(ct);
            await _repo.CompleteJobAsync(job.Id, result, completeConn);

            _logger.LogInformation("Job {JobId} completed successfully", job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} (type={Type}) failed on attempt {Attempt}",
                job.Id, job.Type, job.Attempts);

            await using var failConn = _repo.CreateConnection();
            await failConn.OpenAsync(ct);
            await _repo.FailJobAsync(job.Id, ex.Message, job.Attempts, job.MaxAttempts, failConn);
        }
        finally
        {
            await _repo.UpdateWorkerHeartbeatAsync(_workerId!, null, "idle");
        }
    }

    /// <summary>
    /// Dispatches to the appropriate handler based on job type.
    /// Add new job types here — each type should be idempotent.
    /// </summary>
    private async Task<object?> ExecuteJobAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(job.Payload) ?? new();

        return job.Type switch
        {
            "send_email"      => await HandleSendEmailAsync(payload, ct),
            "generate_report" => await HandleGenerateReportAsync(payload, ct),
            "process_webhook" => await HandleWebhookAsync(payload, ct),
            _ => throw new NotSupportedException($"Unknown job type: '{job.Type}'. Register a handler in ExecuteJobAsync.")
        };
    }

    // ── Job handlers (stubs — replace with real implementations) ─────────────

    private async Task<object?> HandleSendEmailAsync(Dictionary<string, JsonElement> payload, CancellationToken ct)
    {
        await Task.Delay(100, ct); // simulate work
        return new { MessageId = $"msg_{Guid.NewGuid():N}", Sent = true };
    }

    private async Task<object?> HandleGenerateReportAsync(Dictionary<string, JsonElement> payload, CancellationToken ct)
    {
        await Task.Delay(500, ct); // simulate longer work
        return new { ReportUrl = $"https://reports.internal/{Guid.NewGuid():N}.pdf" };
    }

    private async Task<object?> HandleWebhookAsync(Dictionary<string, JsonElement> payload, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        return new { StatusCode = 200, Delivered = true };
    }
}

// ── Job Queue exceptions ──────────────────────────────────────────────────────

public class JobNotFoundException(string id) : Exception($"Job '{id}' not found.");
public class JobNotCancellableException(string id)
    : Exception($"Job '{id}' cannot be cancelled — only PENDING jobs can be cancelled.");
