namespace DistributedJobQueue.Api.Models.Responses;

public record JobResponse
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public int Attempts { get; init; }
    public int MaxAttempts { get; init; }
    public DateTimeOffset ScheduledAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public JobResultInfo? Result { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record JobResultInfo
{
    public bool Success { get; init; }
    public object? Output { get; init; }
    public string? ErrorMessage { get; init; }
}

public record JobCancelledResponse
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CancelledAt { get; init; }
}

public record QueueStatsResponse
{
    public QueueDepth QueueDepth { get; init; } = new();
    public WorkerStats Workers { get; init; } = new();
    public PerformanceStats Performance { get; init; } = new();
    public DateTimeOffset SnapshotAt { get; init; }
}

public record QueueDepth
{
    public PriorityDepth Pending { get; init; } = new();
    public int Running { get; init; }
    public int CompletedToday { get; init; }
    public int FailedToday { get; init; }
    public int Dead { get; init; }
}

public record PriorityDepth { public int High { get; init; } public int Normal { get; init; } public int Low { get; init; } }
public record WorkerStats { public int Total { get; init; } public int Active { get; init; } public int Idle { get; init; } public int Stale { get; init; } }
public record PerformanceStats { public double AvgProcessingTimeMs { get; init; } public double AvgQueueDelayMs { get; init; } public double JobsPerMinute { get; init; } }

public record ApiResponse<T> { public T Data { get; init; } = default!; public ApiMeta Meta { get; init; } = new(); }
public record PagedApiResponse<T> { public IEnumerable<T> Data { get; init; } = []; public PaginationMeta Pagination { get; init; } = new(); public ApiMeta Meta { get; init; } = new(); }
public record ApiMeta { public string RequestId { get; init; } = Guid.NewGuid().ToString(); public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow; }
public record PaginationMeta { public string? Cursor { get; init; } public bool HasMore { get; init; } public int Limit { get; init; } }
public static class ApiResponse { public static ApiResponse<T> Success<T>(T data) => new() { Data = data }; }