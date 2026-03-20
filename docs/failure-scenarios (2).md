# Failure Scenarios — Distributed Job Queue

> **Status**: Complete — Days 25–27 implementation. Replaces Phase 1 skeleton.

---

## Scenario 1 — Worker Crashes Mid-Execution (Orphaned Running Job)

**Trigger**
A worker process crashes (OOM kill, VM termination, unhandled exception outside
the job handler) while a job has `status = 'running'` and the worker owns it.
The job never reaches `completed` or `failed`.

**Affected Components**
JobRepository, `jobs` table, Worker Watchdog, `workers` table.

**User-Visible Impact**
Internal. The job is stuck in `RUNNING` state. If no recovery mechanism exists,
the job never executes. Producers polling for a result wait indefinitely.

**System Behaviour Without Mitigation**
`jobs.status = 'running'` is permanent. The `SELECT FOR UPDATE SKIP LOCKED` claim
query explicitly skips `running` rows — no other worker can claim it. The job is
permanently orphaned.

**Mitigation**

1. **Heartbeat TTL detection (Redis):** Workers update `worker:{id}:alive` in
   Redis every 30 seconds (TTL 45s). When the key expires, the Watchdog knows
   the worker is stale without querying PostgreSQL.

2. **Worker Watchdog service (30-second cycle):**
   ```csharp
   var stale = await _repo.GetStaleWorkersAsync(threshold: TimeSpan.FromSeconds(60));
   foreach (var worker in stale)
   {
       await _repo.MarkWorkerOfflineAsync(worker.Id);
       if (worker.CurrentJobId is not null)
       {
           var job = await _repo.FindByIdAsync(worker.CurrentJobId);
           if (job?.Status == "running")
               await _repo.FailJobAsync(job.Id, "Worker became stale", job.Attempts, job.MaxAttempts);
       }
   }
   ```

3. **Retry or dead-letter on reclaim:**
   - If `job.attempts < job.max_attempts`: status resets to `pending` with
     exponential backoff (`scheduled_at = NOW() + 2^attempts seconds`).
   - If `job.attempts >= job.max_attempts`: status set to `dead`, row inserted
     into `dead_letter_jobs`.

4. **Job reclaim is idempotent:** The `FailJobAsync` / `CompleteJobAsync` methods
   use optimistic concurrency — they check `status = 'running'` in the WHERE clause.
   If two watchdog instances race (unlikely but possible), only one wins.

**Detection**
- Alert: `jobs_stuck_in_running_count > 0` for > 2 minutes → watchdog may be down.
- Alert: `worker_stale_detected_total` rate → high rate indicates frequent worker crashes.
- Metric: `dead_letter_job_creation_total` — non-zero means repeated job failures.

---

## Scenario 2 — Poison Pill Job (Consistent Handler Failure)

**Trigger**
A job has a payload that consistently crashes the handler — invalid data format,
a bug introduced in a deployment, an API the handler calls that no longer exists.
The job fails on every attempt.

**Affected Components**
`JobWorkerBackgroundService`, retry policy, `dead_letter_jobs` table.

**User-Visible Impact**
Internal. The job type is blocked from successful execution. Other job types
continue processing normally — isolation is at the job level, not the worker level.

**System Behaviour Without Mitigation**
The worker retries indefinitely, consuming worker time on a job that will never
succeed. Healthy jobs in the queue may be delayed.

**Mitigation**

1. **Exponential backoff between retries:**
   ```
   Attempt 1 → immediate
   Attempt 2 → wait 2 seconds
   Attempt 3 → wait 4 seconds
   Attempt 4 → wait 8 seconds
   Attempt 5 → dead letter
   ```
   Total time consumed: 14 seconds across all retries. Worker processes other
   jobs during backoff.

2. **Dead letter isolation after `max_attempts`:**
   ```csharp
   // In FailJobAsync:
   if (attempts >= maxAttempts)
   {
       await conn.ExecuteAsync("UPDATE jobs SET status = 'dead' WHERE id = @Id", ...);
       await conn.ExecuteAsync("INSERT INTO dead_letter_jobs ...", ...);
   }
   ```
   The dead job is isolated in `dead_letter_jobs`. It does not affect the claim
   query (`WHERE status = 'pending'`). Other jobs of any type continue.

3. **Dead letter re-queue after fix:** Once the handler bug is fixed, operators
   use the admin API: `POST /admin/dead-letter/{id}/requeue`. This inserts a new
   job row with `attempts = 0` and `status = 'pending'`.

4. **Dead letter alert:** `dead_letter_jobs` is monitored. Any new row triggers
   an alert within 5 minutes.

**Detection**
- Alert: `dead_letter_job_creation_total > 0` → any dead letter creation pages on-call.
- Metric: `job_failure_rate_by_type` — spike for a specific type indicates a handler bug.
- Alert: `job_type_failure_rate{type="send_email"} > 50%` → handler broken.

---

## Scenario 3 — PostgreSQL Primary Outage (All Processing Halts)

**Trigger**
PostgreSQL primary becomes unavailable. Workers cannot claim jobs (`SELECT FOR UPDATE`
requires the primary). Workers cannot heartbeat. Claims and completions fail.

**Affected Components**
JobRepository, all workers, all in-progress jobs.

**User-Visible Impact**
Internal. No new jobs are processed. In-progress jobs whose workers lose the DB
connection have their current attempt fail. Job processing resumes when the primary
recovers or failover completes.

**Mitigation**

1. **Retry with exponential backoff on connection failure:** Workers catch
   `NpgsqlException { IsTransient: true }` on claim queries and retry with
   100ms → 200ms → 400ms → 800ms backoff before giving up for the current poll cycle.
   Workers do not crash — they keep retrying.

2. **Circuit breaker on DB connections:** After 5 consecutive failures, the circuit
   opens. Workers enter a sleep loop (5-second intervals), attempting a probe
   request every 30 seconds. On probe success, circuit closes and normal processing resumes.

3. **Graceful worker state:** In-progress jobs whose DB write fails at completion
   time remain in `RUNNING` state. The Watchdog reclaims them after primary recovery
   (they appear as stale workers' jobs). They retry normally.

4. **RabbitMQ fallback for heartbeat:** If the DB is unavailable, workers cannot
   update `last_heartbeat_at`. Redis TTL (`worker:{id}:alive`) serves as the
   liveness signal during the outage window — the Watchdog reads from Redis before
   querying PostgreSQL.

**Detection**
- Alert: PostgreSQL health check → Unhealthy.
- Metric: `job_claim_failure_rate` spike coinciding with DB outage.
- Alert: `workers_with_no_heartbeat_count > total_workers × 0.5` → DB likely down.

---

## Scenario 4 — RabbitMQ Notification Queue Failure

**Trigger**
RabbitMQ becomes unavailable. Job submission (`POST /jobs`) succeeds (job inserted
in PostgreSQL) but the wake notification cannot be published to `jobs.notify`.

**Affected Components**
`JobNotificationPublisher`, `jobs.notify` queue, worker poll timing.

**User-Visible Impact**
None functionally. Jobs are still processed. The only impact is latency: instead
of workers being woken immediately by the notification, they discover the new job
on their next timer-based poll (1-second interval).

**System Behaviour Without Mitigation**
If `PublishNotification()` throws, and the exception is not handled, `POST /jobs`
returns 500 even though the job was successfully created in PostgreSQL. The job
is orphaned without the client knowing it was accepted.

**Mitigation**

1. **Fire-and-forget notification publish:** `JobService.SubmitJobAsync` calls
   `_queue.PublishJobNotificationAsync` but does not `await` it for success:
   ```csharp
   await _repo.InsertJobAsync(job, conn);        // must succeed
   _ = _queue.PublishJobNotificationAsync(ct);   // best-effort
   return MapJob(job);                           // always returns
   ```

2. **Timer-based polling fallback in all workers:** Workers always have a 1-second
   polling timer as a fallback. RabbitMQ is a latency optimisation, not a correctness
   requirement.

3. **`PublishNotification` fails silently with warning log:** The publisher wraps
   all RabbitMQ operations in try/catch and logs at WARNING level. This never
   propagates to the API response.

**Detection**
- Alert: RabbitMQ health check → Degraded.
- Metric: `job_start_latency_p99` slight increase → timer polling is the active path.

---

## Scenario 5 — Database Connection Pool Exhaustion

**Trigger**
100 workers all attempt to claim jobs simultaneously. Each claim requires a
`BEGIN TRANSACTION` → `SELECT FOR UPDATE SKIP LOCKED` → `UPDATE` → `COMMIT` cycle.
PgBouncer's transaction-mode pool allows up to 100 server connections. Peak claiming
activity may saturate this pool.

**Affected Components**
PgBouncer connection pool, `JobWorkerBackgroundService`, claim throughput.

**Mitigation**

1. **PgBouncer transaction mode:** Connections are returned to the pool immediately
   after each statement group (BEGIN...COMMIT). Workers do not hold connections
   during job execution — only during the 3-statement claim sequence (~5ms).

2. **Pool sizing formula:**
   ```
   pool_size = (claim_duration_ms / target_poll_interval_ms) × worker_count
             = (5ms / 1000ms) × 100 workers = 0.5 → round up to 5 connections needed
   ```
   Even 100 workers require only ~5 PgBouncer connections when using transaction mode.

3. **Circuit breaker on pool exhaustion:** After 5 consecutive `PoolExhaustedException`
   within 30 seconds, the circuit opens. Workers enter the sleep loop rather than
   hammering the pool.

**Detection**
- Alert: `pgbouncer_wait_time_p99 > 50ms` → pool under pressure.
- Metric: `job_claim_wait_time_histogram` — should be near zero with correct pool sizing.
