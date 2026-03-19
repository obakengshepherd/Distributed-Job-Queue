using System.Text.Json;
using Dapper;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;

namespace DistributedJobQueue.Infrastructure.Cache;

/// <summary>
/// Redis adapter for the Distributed Job Queue system.
///
/// Roles:
///   1. Worker heartbeat TTL keys  — detect stale workers without polling
///   2. Queue depth counters       — real-time monitoring without DB queries
///   3. Job deduplication cache    — prevent submitting the same job twice
///
/// Key inventory:
///   worker:{id}:alive  (STRING, TTL 45s) — set on every heartbeat
///   counter:queue:depth (STRING)          — approximate pending job count
///   job:submitted:{idempotencyKey} (STRING, TTL 1h) — submission dedup
/// </summary>
public class JobQueueCacheService
{
    private readonly IDatabase _db;
    private readonly ILogger<JobQueueCacheService> _logger;

    private static readonly TimeSpan HeartbeatTtl    = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DeduplicationTtl = TimeSpan.FromHours(1);

    public JobQueueCacheService(IConnectionMultiplexer redis, ILogger<JobQueueCacheService> logger)
    {
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    // ── Worker heartbeat keys ─────────────────────────────────────────────────

    /// <summary>
    /// Sets/renews the worker liveness key in Redis.
    /// The Watchdog can check Redis instead of querying PostgreSQL for every worker,
    /// reducing DB load at scale. PostgreSQL remains the authoritative source.
    /// </summary>
    public async Task RenewWorkerHeartbeatAsync(string workerId)
    {
        try
        {
            await _db.StringSetAsync($"worker:{workerId}:alive", "1", HeartbeatTtl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis heartbeat renew failed for worker {WorkerId} — non-fatal, DB check is fallback", workerId);
        }
    }

    public async Task<bool> IsWorkerAliveInCacheAsync(string workerId)
    {
        try { return await _db.KeyExistsAsync($"worker:{workerId}:alive"); }
        catch { return false; } // Redis unavailable → fall through to DB check
    }

    public async Task RemoveWorkerHeartbeatAsync(string workerId)
    {
        try { await _db.KeyDeleteAsync($"worker:{workerId}:alive"); }
        catch { /* non-fatal */ }
    }

    // ── Queue depth counters ──────────────────────────────────────────────────

    /// <summary>
    /// Increments the approximate queue depth counter.
    /// This counter is an approximation — it is incremented on submit and
    /// decremented on claim. It may drift from the true count (e.g. on crash),
    /// but it is useful for monitoring without a COUNT(*) query.
    /// </summary>
    public async Task IncrementQueueDepthAsync(string priority)
    {
        try
        {
            await _db.StringIncrementAsync($"counter:queue:{priority}");
        }
        catch { /* non-fatal */ }
    }

    public async Task DecrementQueueDepthAsync(string priority)
    {
        try
        {
            // Use MAX(0) to prevent negative counters from drift
            var key     = $"counter:queue:{priority}";
            var current = await _db.StringGetAsync(key);
            if (current.HasValue && long.TryParse(current.ToString(), out var count) && count > 0)
                await _db.StringDecrementAsync(key);
        }
        catch { /* non-fatal */ }
    }

    public async Task<Dictionary<string, long>> GetQueueDepthCountersAsync()
    {
        try
        {
            var keys = new[] { "counter:queue:high", "counter:queue:normal", "counter:queue:low" };
            var values = await _db.StringGetAsync(keys.Select(k => (RedisKey)k).ToArray());
            return new Dictionary<string, long>
            {
                ["high"]   = values[0].HasValue && long.TryParse(values[0].ToString(), out var h) ? h : 0,
                ["normal"] = values[1].HasValue && long.TryParse(values[1].ToString(), out var n) ? n : 0,
                ["low"]    = values[2].HasValue && long.TryParse(values[2].ToString(), out var l) ? l : 0
            };
        }
        catch { return new Dictionary<string, long> { ["high"] = 0, ["normal"] = 0, ["low"] = 0 }; }
    }

    // ── Job submission deduplication ──────────────────────────────────────────

    /// <summary>
    /// Returns true if a job with this key was already submitted within the TTL window.
    /// Prevents duplicate job submission from retry-happy producers.
    /// </summary>
    public async Task<bool> TryMarkJobSubmittedAsync(string idempotencyKey)
    {
        try
        {
            // SET NX — returns true if the key was set (first submission)
            return await _db.StringSetAsync(
                $"job:submitted:{idempotencyKey}",
                "1",
                DeduplicationTtl,
                When.NotExists);
        }
        catch
        {
            return true; // Redis unavailable — allow submission (DB unique index is fallback)
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// RABBITMQ CONSUMER — Job Notification Consumer
// Receives wake signals from the notification queue and triggers job polling
// ════════════════════════════════════════════════════════════════════════════

namespace DistributedJobQueue.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ consumer for the jobs.notify queue.
///
/// This consumer replaces the timer-based polling in the worker with an
/// event-driven approach: workers sleep until woken by a notification.
///
/// Queue: jobs.notify (non-durable — ephemeral wake signals)
/// Exchange: default (direct routing)
///
/// Why RabbitMQ and not Kafka?
/// Job notifications are pure work-queue semantics:
///   - Each notification should wake exactly one worker (competing consumers)
///   - Notifications do not need to be replayed — if a worker misses a wake
///     signal, it will poll on its fallback timer anyway
///   - RabbitMQ's basic.get + ack model is ideal for this pattern
///
/// Dead letter queue: jobs.dlx → jobs.dead
/// Messages that cannot be processed (serialisation error) go to the DLQ.
/// Since notifications carry no payload, this should never happen in practice.
/// </summary>
public class JobNotificationConsumer : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly Func<CancellationToken, Task> _onJobAvailable;
    private readonly ILogger<JobNotificationConsumer> _logger;
    private const string Queue = "jobs.notify";

    public JobNotificationConsumer(
        IConfiguration configuration,
        Func<CancellationToken, Task> onJobAvailable,
        ILogger<JobNotificationConsumer> logger)
    {
        _onJobAvailable = onJobAvailable;
        _logger         = logger;

        var factory = new ConnectionFactory
        {
            Uri = new Uri(configuration.GetConnectionString("RabbitMQ") ?? "amqp://devuser:devpass@localhost:5672/"),
            AutomaticRecoveryEnabled = true,         // auto-reconnect on connection loss
            NetworkRecoveryInterval  = TimeSpan.FromSeconds(5)
        };

        _connection = factory.CreateConnection();
        _channel    = _connection.CreateModel();

        // Non-durable queue — wake signals are ephemeral
        _channel.QueueDeclare(
            queue: Queue,
            durable: false,      // does not survive broker restart — that is fine
            exclusive: false,    // multiple workers share this queue (competing consumers)
            autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"]    = "jobs.dlx",
                ["x-dead-letter-routing-key"] = "jobs.dead"
            });

        // Prefetch 1 — each worker takes one notification at a time
        // This implements round-robin distribution to workers
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
    }

    public void StartConsuming(CancellationToken ct)
    {
        var consumer = new EventingBasicConsumer(_channel);

        consumer.Received += async (_, args) =>
        {
            if (ct.IsCancellationRequested)
            {
                // Requeue — another worker will handle it after restart
                _channel.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
                return;
            }

            try
            {
                _logger.LogDebug("Received job notification — triggering poll");
                await _onJobAvailable(ct);
                _channel.BasicAck(args.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process job notification");
                // NACK without requeue — wake signal goes to DLQ (dead signals are harmless)
                _channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
            }
        };

        _channel.BasicConsume(queue: Queue, autoAck: false, consumer: consumer);
        _logger.LogInformation("JobNotificationConsumer started — listening on queue: {Queue}", Queue);
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}

// ════════════════════════════════════════════════════════════════════════════
// RABBITMQ PRODUCER — Enhanced notification publisher with retry logic
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Enhanced RabbitMQ producer for job wake-up signals with retry and dead-letter setup.
/// Replaces the basic publisher from Phase 4 with proper exchange declarations
/// and confirms mode for reliable delivery.
/// </summary>
public class JobNotificationPublisher : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly ILogger<JobNotificationPublisher> _logger;
    private const string NotifyQueue = "jobs.notify";
    private const string DlxName    = "jobs.dlx";
    private const string DlqName    = "jobs.dead";

    public JobNotificationPublisher(IConfiguration configuration, ILogger<JobNotificationPublisher> logger)
    {
        _logger = logger;

        var factory = new ConnectionFactory
        {
            Uri = new Uri(configuration.GetConnectionString("RabbitMQ") ?? "amqp://devuser:devpass@localhost:5672/"),
            AutomaticRecoveryEnabled = true
        };

        _connection = factory.CreateConnection();
        _channel    = _connection.CreateModel();

        // Declare dead letter exchange and queue
        _channel.ExchangeDeclare(exchange: DlxName, type: ExchangeType.Direct, durable: true);
        _channel.QueueDeclare(queue: DlqName, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(queue: DlqName, exchange: DlxName, routingKey: DlqName);

        // Declare the notify queue with DLX config
        _channel.QueueDeclare(
            queue: NotifyQueue,
            durable: false,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"]    = DlxName,
                ["x-dead-letter-routing-key"] = DlqName
            });

        // Enable publisher confirms for reliable delivery
        _channel.ConfirmSelect();
    }

    public void PublishNotification()
    {
        try
        {
            var props = _channel.CreateBasicProperties();
            props.Persistent = false; // non-persistent — wake signals are ephemeral

            _channel.BasicPublish(
                exchange:   string.Empty,
                routingKey: NotifyQueue,
                basicProperties: props,
                body: "{}"u8.ToArray());

            // Wait for broker confirmation (up to 2 seconds)
            var confirmed = _channel.WaitForConfirms(TimeSpan.FromSeconds(2));
            if (!confirmed)
                _logger.LogWarning("RabbitMQ notification publish not confirmed within timeout");
        }
        catch (Exception ex)
        {
            // Non-fatal — workers fall back to timer polling
            _logger.LogWarning(ex, "Failed to publish job notification to RabbitMQ — workers will use timer fallback");
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
