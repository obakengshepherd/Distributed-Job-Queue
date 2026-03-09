# Problem Statement — Distributed Job Queue

---

## Section 1 — The Problem

Not all work can or should happen in the context of an HTTP request. Sending an email,
processing an uploaded video, generating a report, or running a data pipeline are all
tasks that are too slow, too resource-intensive, or too failure-prone to execute
synchronously. A distributed job queue decouples the submission of work from its execution:
a producer enqueues a job and immediately receives acknowledgement; one of many workers
picks it up and processes it in the background. The system must ensure that no submitted
job is silently lost, that every job is eventually processed, and that a single badly
behaved job cannot prevent other jobs from running.

---

## Section 2 — Why It Is Hard

- **At-least-once delivery vs exactly-once processing**: Message brokers can guarantee
  that a job is delivered at least once, but guaranteeing it is processed exactly once
  requires the worker to acknowledge success only after durably recording the result, and
  requires the job handler itself to be idempotent. Getting this wrong means jobs are
  either silently dropped or processed multiple times — both are bugs.

- **Atomic job claiming**: When multiple workers are polling for available jobs, they must
  not claim the same job simultaneously. This requires an atomic claim operation —
  typically a database-level lock (`SELECT FOR UPDATE SKIP LOCKED`) or a broker-level
  acknowledgement model — rather than a read-then-update race condition.

- **Retry and failure isolation**: A job that repeatedly fails (a "poison pill") must not
  be retried indefinitely and must not block other jobs in the queue. The system needs a
  retry limit, exponential backoff between retries, and a dead letter queue to isolate
  permanently failing jobs for manual inspection.

- **Worker failure mid-execution**: A worker can crash after claiming a job but before
  completing it. The system must detect this via heartbeat timeout and reclaim the job for
  another worker — without requiring any global coordinator that itself becomes a single
  point of failure.

- **Queue depth and backpressure**: At 1 million jobs per day with 100 workers, average
  throughput is about 12 jobs per second. But job submission is bursty. When producers
  submit jobs faster than workers can process them, queue depth grows. The system must
  surface this as a metric and provide backpressure or worker scaling signals before the
  queue becomes unboundedly large.

---

## Section 3 — Scope of This Implementation.

**In scope:**

- Job submission API with payload, type, priority, and optional scheduled execution time
- Atomic job claiming using PostgreSQL `SELECT FOR UPDATE SKIP LOCKED`
- Worker registration and heartbeat with stale worker detection
- Retry logic with configurable max attempts and exponential backoff
- Dead letter queue for jobs exceeding max retry attempts
- Job status tracking: PENDING, RUNNING, COMPLETED, FAILED, DEAD
- Job result storage (success output or failure reason)
- Queue depth and worker utilisation metrics API
- Job cancellation (for pending jobs not yet claimed)
- Priority queue support (higher priority jobs claimed before lower priority)

**Out of scope:**

- Cron-style recurring job scheduling
- Job dependency graphs (job B starts only after job A completes)
- Distributed tracing across job chains
- Real-time worker dashboard UI
- Multi-tenant queue isolation
- Rate limiting per job type or producer

---

## Section 4 — Success Criteria

The system is working correctly when:

1. Every job submitted via `POST /jobs` is either eventually processed to completion or
   moved to the dead letter queue after exhausting its retry allowance — no job is
   silently dropped.

2. No job is claimed by more than one worker simultaneously, regardless of how many workers
   are polling concurrently.

3. If a worker crashes mid-execution, its claimed job is reclaimed by another worker within
   one heartbeat timeout window (≤60 seconds) and processing resumes from the beginning.

4. A job that consistently fails does not block other jobs — it is retried with backoff,
   moved to the dead letter queue after reaching max attempts, and has no impact on the
   processing of healthy jobs.

5. Job processing delay (time from submission to start of execution) does not exceed
   5 seconds under normal load (1M jobs/day, 100 workers), measured at p95.

6. The job status API accurately reflects the current state of any job within 1 second of
   that state changing — there is no window where a completed job appears as running.
