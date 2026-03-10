namespace DistributedJobQueue.Api.Models.Requests;

using System.ComponentModel.DataAnnotations;

public record SubmitJobRequest
{
    [Required][StringLength(64)] public string Type { get; init; } = string.Empty;
    [Required] public object Payload { get; init; } = new();
    public string Priority { get; init; } = "NORMAL"; // HIGH | NORMAL | LOW
    public DateTimeOffset? ScheduledAt { get; init; }
    [Range(1, 10)] public int MaxAttempts { get; init; } = 5;
}

public record ListJobsRequest
{
    public string? Status { get; init; } // PENDING | RUNNING | COMPLETED | FAILED | DEAD
    public string? Type { get; init; }
    [Range(1, 100)] public int Limit { get; init; } = 20;
    public string? Cursor { get; init; }
}

public record WorkerHeartbeatRequest
{
    [Required] public string WorkerId { get; init; } = string.Empty;
    public string? JobId { get; init; }
    [Required] public string Status { get; init; } = string.Empty; // idle | busy
}