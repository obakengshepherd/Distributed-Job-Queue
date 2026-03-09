# Architecture — Distributed Job Queue

---

## Overview

The Distributed Job Queue decouples work submission from work execution. Producers submit
jobs via a REST API; workers claim and process them asynchronously. The architectural
backbone is PostgreSQL, which serves as both the job store and the coordination mechanism —
the `SELECT FOR UPDATE SKIP LOCKED` pattern provides atomic, collision-free job claiming
without the need for a separate message broker on the hot path. RabbitMQ is used for worker
notification (wake-up signals) to avoid continuous polling, but the job state and ownership
live entirely in PostgreSQL. This makes the system remarkably simple to operate: the job
queue is queryable, auditable, and recoverable using standard SQL tooling.

---

## Architecture Diagram

```
┌──────────────────────────────────────────────────────────────┐
│              Producer Services (Any upstream app)            │
└──────────────────────────────┬───────────────────────────────┘
                               │ HTTPS
┌──────────────────────────────▼───────────────────────────────┐
│                      Job Submission API                      │
│              (POST /jobs, GET /jobs/{id}, DELETE)            │
└──────────────────────────────┬───────────────────────────────┘
                               │
               ┌───────────────┴───────────────┐
               │                               │
┌──────────────▼───────────┐   ┌───────────────▼────────────┐
│       PostgreSQL          │   │   RabbitMQ: jobs.notify    │
│   (jobs · workers ·       │   │   (wake signal only —      │
│    job_results ·          │   │    no job payload)         │
│    dead_letter_jobs)      │   └───────────────┬────────────┘
└──────────────┬────────────┘                   │
               │                                │
               │   SELECT FOR UPDATE SKIP LOCKED│
               └────────────────┬───────────────┘
                                │
               ┌────────────────▼────────────────┐
               │         Worker Pool              │
               │   (W1 · W2 · W3 · ... · W100)   │
               │   Each worker:                  │
               │   - Registers in workers table  │
               │   - Claims jobs atomically      │
               │   - Heartbeats every 30s        │
               │   - Writes result on complete   │
               └────────────────┬────────────────┘
                                │
               ┌────────────────▼────────────────┐
               │        Worker Watchdog           │
               │  (detects stale workers,         │
               │   reclaims orphaned jobs)        │
               └─────────────────────────────────┘
```

---

## Layer-by-Layer Description

### Job Submission API

The Job Submission API is the external interface for producers. It accepts job creation
requests with a payload, job type, optional priority (default: NORMAL), and optional
`scheduled_at` time (for delayed execution). It returns a job ID immediately — job execution
is never synchronous from the producer's perspective.

The API also provides status polling: `GET /jobs/{id}` returns the current status, attempts
count, and result if completed. This allows producers to implement their own completion
polling without any coupling to the worker layer.

### PostgreSQL — Job Store and Coordination Mechanism

PostgreSQL is the single source of truth for all job state. This is an intentional design
choice: it simplifies operations significantly compared to a dual-store architecture where
job metadata is in a database and messages are in a broker.

The core coordination mechanism is `SELECT FOR UPDATE SKIP LOCKED`. When a worker claims
a job, it executes:

```
SELECT id FROM jobs
WHERE status = 'PENDING'
AND scheduled_at <= NOW()
ORDER BY priority DESC, created_at ASC
LIMIT 1
FOR UPDATE SKIP LOCKED
```

The `SKIP LOCKED` clause atomically skips any row that is already locked by another
transaction. This means any number of workers can safely poll for jobs simultaneously
without conflicts, duplicates, or deadlocks. The lock is held for the duration of the
status update to RUNNING, after which the job row is unlocked and the worker holds only
the job ID it claimed.

### RabbitMQ — Notification Only

RabbitMQ carries a single type of message: a wake signal (`{}` — empty payload) published
whenever a new job is created. Workers subscribe to this notification queue and awaken to
poll for available jobs when a signal arrives. Without this, workers would poll on a timer
(e.g., every second), introducing up to 1 second of unnecessary delay and constant database
load when the queue is empty.

RabbitMQ is not in the critical path for job execution — it is purely a latency optimisation.
If RabbitMQ is unavailable, workers fall back to timer-based polling and continue processing
jobs with slightly higher average latency.

### Worker Pool

Workers are independent processes (or instances of the same process) that compete for jobs.
Each worker:

1. **Registers** itself in the `workers` table on startup with its hostname, process ID,
   and a heartbeat timestamp.

2. **Claims** a job using `SELECT FOR UPDATE SKIP LOCKED`. On success, it updates the job's
   `status` to RUNNING, `started_at` to now, and increments `attempts`.

3. **Executes** the job by dispatching to the appropriate handler based on `job.type`.

4. **Heartbeats** every 30 seconds: updates its row in `workers` with `last_heartbeat_at = now()`.
   This is the mechanism by which crashed workers are detected.

5. **Completes** the job by updating `status` to COMPLETED and writing the result to
   `job_results`.

6. **Fails** gracefully: on exception, if `attempts < max_attempts`, updates `status` back
   to PENDING with a `scheduled_at` of `now() + backoff(attempts)` for retry. If
   `attempts >= max_attempts`, moves the job to `dead_letter_jobs` and sets `status` to DEAD.

### Worker Watchdog

The Worker Watchdog is a lightweight scheduled process (runs every 30 seconds) that detects
stale workers. A worker is considered stale if `last_heartbeat_at < now() - 60 seconds`.
For each stale worker, the watchdog:

1. Updates the worker's `status` to OFFLINE.
2. Queries for any jobs with `status = RUNNING` and `worker_id = stale_worker_id`.
3. For each such job: if `attempts < max_attempts`, resets `status` to PENDING for retry.
   Otherwise, moves to dead letter.

The watchdog itself is idempotent — running it multiple times produces the same result.

### Dead Letter Queue

Jobs that have exhausted their retry allowance are moved to the `dead_letter_jobs` table
with the original job ID, failure reason, and timestamp. The Job Submission API exposes
endpoints for listing and re-queuing dead letter jobs. Dead letter jobs do not block the
main queue — they are entirely isolated.

---

## Component Responsibilities Summary

| Component             | Responsibility                                              | Communicates Via         |
|-----------------------|-------------------------------------------------------------|--------------------------|
| Job Submission API    | Accept job submissions, return status, expose management    | HTTP                     |
| PostgreSQL            | Job store, coordination via SKIP LOCKED, result persistence | TCP + row-level locking  |
| RabbitMQ              | Worker wake-up signal on new job creation                   | AMQP                     |
| Worker Pool           | Claim, execute, heartbeat, complete/fail jobs               | PostgreSQL + RabbitMQ    |
| Worker Watchdog       | Detect stale workers, reclaim orphaned running jobs         | PostgreSQL (scheduled)   |
| Dead Letter Queue     | Isolate permanently failing jobs for manual inspection      | PostgreSQL table         |
