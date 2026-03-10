namespace DistributedJobQueue.Application.Interfaces;

using DistributedJobQueue.Api.Models.Requests;
using DistributedJobQueue.Api.Models.Responses;

/// <summary>
/// Manages job lifecycle: submit, status, cancel, list.
/// Job claiming is done by workers using SELECT FOR UPDATE SKIP LOCKED — not this interface.
/// </summary>
public interface IJobService
{
    Task<JobResponse> SubmitJobAsync(SubmitJobRequest request, CancellationToken ct);
    Task<JobResponse> GetJobAsync(string jobId, CancellationToken ct);
    Task<JobCancelledResponse> CancelJobAsync(string jobId, CancellationToken ct);
    Task<PagedApiResponse<JobResponse>> ListJobsAsync(ListJobsRequest query, CancellationToken ct);
}

/// <summary>Worker registration, heartbeat recording, and stale worker detection.</summary>
public interface IWorkerService
{
    Task RecordHeartbeatAsync(WorkerHeartbeatRequest request, CancellationToken ct);
    Task<IEnumerable<string>> DetectStaleWorkersAsync(CancellationToken ct); // called by watchdog
    Task ReclaimOrphanedJobsAsync(IEnumerable<string> staleWorkerIds, CancellationToken ct);
}

/// <summary>Queue depth metrics and operational stats.</summary>
public interface IQueueService
{
    Task<QueueStatsResponse> GetStatsAsync(CancellationToken ct);
    Task PublishJobNotificationAsync(CancellationToken ct); // wake-up signal to RabbitMQ
}