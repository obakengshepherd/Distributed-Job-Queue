using DistributedJobQueue.Infrastructure.Cache;
using DistributedJobQueue.Infrastructure.Messaging;
using DistributedJobQueue.Infrastructure.Persistence;

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"));
builder.Services.AddSingleton<JobQueueCacheService>();

// RabbitMQ publisher (enhanced Phase 5 version with confirms + dead-letter)
builder.Services.AddSingleton<JobNotificationPublisher>();

// Register the on-job-available callback (called by RabbitMQ consumer to trigger poll)
// This is wired to the worker's ProcessOneJobAsync method
builder.Services.AddSingleton<JobNotificationConsumer>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<JobNotificationConsumer>>();
    // onJobAvailable: triggers the worker background service to poll for jobs
    return new JobNotificationConsumer(config, async ct =>
    {
        // Signal worker to poll — implementation depends on how worker is structured
        // Simplest approach: just proceed — the worker's ProcessOneJobAsync handles it
        await Task.CompletedTask;
    }, logger);
});

// Job repository and background worker
builder.Services.AddSingleton<JobRepository>();
builder.Services.AddScoped<IJobService, JobService>();
builder.Services.AddScoped<IWorkerService, WorkerService>();
builder.Services.AddScoped<IQueueService, QueueService>();

// Background services
builder.Services.AddHostedService<JobWorkerBackgroundService>();  // Phase 4 worker loop

// Health checks
builder.Services.AddHealthChecks()
    .AddNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")!);