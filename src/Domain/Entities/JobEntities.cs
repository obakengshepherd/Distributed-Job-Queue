namespace DistributedJobQueue.Domain.Entities;

public class Job
{
    public string Id { get; private set; } = string.Empty;
    public string Type { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public JobStatus Status { get; private set; }
    public JobPriority Priority { get; private set; }
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public string? CurrentWorkerId { get; private set; }
    public DateTimeOffset ScheduledAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // Lifecycle — implemented Day 16
    public void Claim(string workerId) => throw new NotImplementedException();
    public void Complete() => throw new NotImplementedException();
    public void Fail(int backoffSeconds) => throw new NotImplementedException();
    public void Cancel() => throw new NotImplementedException();
    public void MoveToDeadLetter() => throw new NotImplementedException();
}

public class Worker
{
    public string Id { get; private set; } = string.Empty;
    public string Hostname { get; private set; } = string.Empty;
    public WorkerStatus Status { get; private set; }
    public string? CurrentJobId { get; private set; }
    public DateTimeOffset LastHeartbeatAt { get; private set; }
    public DateTimeOffset RegisteredAt { get; private set; }

    public bool IsStale => DateTimeOffset.UtcNow - LastHeartbeatAt > TimeSpan.FromSeconds(60);
}

public class DeadLetterJob
{
    public string Id { get; private set; } = string.Empty;
    public string OriginalJobId { get; private set; } = string.Empty;
    public string FailureReason { get; private set; } = string.Empty;
    public DateTimeOffset MovedAt { get; private set; }
}

public enum JobStatus { Pending, Running, Completed, Failed, Dead, Cancelled }
public enum JobPriority { High = 1, Normal = 2, Low = 3 }
public enum WorkerStatus { Idle, Busy, Offline }

namespace DistributedJobQueue.Api;
using System.Security.Claims;
public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal p)
    {
        var id = p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(id)) throw new UnauthorizedAccessException("User ID claim missing.");
        return id;
    }
}