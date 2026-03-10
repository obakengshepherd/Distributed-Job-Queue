namespace DistributedJobQueue.Application.Services;

using DistributedJobQueue.Application.Interfaces;
using DistributedJobQueue.Api.Models.Requests;
using DistributedJobQueue.Api.Models.Responses;

public class JobService : IJobService
{
    public Task<JobResponse> SubmitJobAsync(SubmitJobRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 16");
    public Task<JobResponse> GetJobAsync(string jobId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 16");
    public Task<JobCancelledResponse> CancelJobAsync(string jobId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 16");
    public Task<PagedApiResponse<JobResponse>> ListJobsAsync(ListJobsRequest query, CancellationToken ct) => throw new NotImplementedException("Implemented Day 16");
}

public class WorkerService : IWorkerService
{
    public Task RecordHeartbeatAsync(WorkerHeartbeatRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 16");
    public Task<IEnumerable<string>> DetectStaleWorkersAsync(CancellationToken ct) => throw new NotImplementedException("Implemented Day 16");
    public Task ReclaimOrphanedJobsAsync(IEnumerable<string> staleWorkerIds, CancellationToken ct) => throw new NotImplementedException("Implemented Day 16");
}

public class QueueService : IQueueService
{
    public Task<QueueStatsResponse> GetStatsAsync(CancellationToken ct) => throw new NotImplementedException("Implemented Day 16");
    public Task PublishJobNotificationAsync(CancellationToken ct) => throw new NotImplementedException("Implemented Day 16");
}