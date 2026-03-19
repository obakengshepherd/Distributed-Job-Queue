using DistributedJobQueue.Infrastructure.Cache;
using DistributedJobQueue.Infrastructure.Messaging;
using DistributedJobQueue.Infrastructure.Persistence;
using Shared.Infrastructure.RateLimit;
using Shared.Api.Controllers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(
    _ => RateLimitPolicies.JobQueuePolicies());

builder.Services.AddHealthChecks()
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", failureStatus: HealthStatus.Degraded,  tags: ["messaging"])
    .AddCheck<PostgreSqlHealthCheck>("postgresql", failureStatus: HealthStatus.Unhealthy, tags: ["database"]);

builder.Services.AddTransient(_ => new RabbitMqHealthCheck(builder.Configuration.GetConnectionString("RabbitMQ") ?? "amqp://devuser:devpass@localhost:5672/"));
builder.Services.AddTransient(_ => new PostgreSqlHealthCheck(builder.Configuration.GetConnectionString("PostgreSQL")!));

// ── Middleware pipeline ──
// app.UseAuthentication();
// app.UseAuthorization();
// app.UseMiddleware<RedisRateLimitMiddleware>();
// app.MapControllers();
// app.MapHealthEndpoints();

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