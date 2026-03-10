using DistributedJobQueue.Application.Interfaces;
using DistributedJobQueue.Api.Models.Requests;
using DistributedJobQueue.Api.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DistributedJobQueue.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public class JobController : ControllerBase
{
    private readonly IJobService _jobService;
    private readonly IQueueService _queueService;

    public JobController(IJobService jobService, IQueueService queueService)
    {
        _jobService = jobService;
        _queueService = queueService;
    }

    // POST /api/v1/jobs
    [HttpPost("jobs")]
    public async Task<IActionResult> SubmitJob([FromBody] SubmitJobRequest request, CancellationToken ct)
    {
        var result = await _jobService.SubmitJobAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, ApiResponse.Success(result));
    }

    // GET /api/v1/jobs/{id}
    [HttpGet("jobs/{id}")]
    public async Task<IActionResult> GetJob([FromRoute] string id, CancellationToken ct)
    {
        var result = await _jobService.GetJobAsync(id, ct);
        return Ok(ApiResponse.Success(result));
    }

    // DELETE /api/v1/jobs/{id}
    [HttpDelete("jobs/{id}")]
    public async Task<IActionResult> CancelJob([FromRoute] string id, CancellationToken ct)
    {
        var result = await _jobService.CancelJobAsync(id, ct);
        return Ok(ApiResponse.Success(result));
    }

    // GET /api/v1/jobs
    [HttpGet("jobs")]
    public async Task<IActionResult> ListJobs([FromQuery] ListJobsRequest query, CancellationToken ct)
    {
        var result = await _jobService.ListJobsAsync(query, ct);
        return Ok(result);
    }

    // GET /api/v1/queue/stats
    [HttpGet("queue/stats")]
    public async Task<IActionResult> GetQueueStats(CancellationToken ct)
    {
        var result = await _queueService.GetStatsAsync(ct);
        return Ok(ApiResponse.Success(result));
    }
}

[ApiController]
[Route("api/v1/workers")]
[Authorize]
public class WorkerController : ControllerBase
{
    private readonly IWorkerService _workerService;

    public WorkerController(IWorkerService workerService)
    {
        _workerService = workerService;
    }

    // POST /api/v1/workers/heartbeat
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] WorkerHeartbeatRequest request, CancellationToken ct)
    {
        await _workerService.RecordHeartbeatAsync(request, ct);
        return NoContent();
    }
}