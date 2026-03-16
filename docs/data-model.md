# Data Model — Distributed Job Queue

---

## Database Technology Choices

### PostgreSQL (Job store and coordination mechanism)
PostgreSQL serves a dual role here that is architecturally significant: it is both the
job store and the distributed coordination mechanism. The `SELECT ... FOR UPDATE SKIP LOCKED`
pattern provides atomic, collision-free job claiming without an external lock manager or
a separate message broker on the claiming hot path. Any number of workers can claim jobs
simultaneously — SKIP LOCKED means a row being claimed by one worker is invisibly skipped
by all others, preventing duplicate processing at the database level.

This is deliberately simpler than a dual-store architecture (separate DB + broker). All
job state is in one place, queryable with standard SQL, and recoverable through standard
PostgreSQL tooling.

### RabbitMQ (Worker notification only)
RabbitMQ carries a single message type: a lightweight wake signal (`{}` — empty body)
published when a new job is inserted. Workers subscribe and awaken to poll PostgreSQL
when signalled, rather than polling on a fixed timer. This reduces idle polling load
and improves average job start latency. If RabbitMQ is unavailable, workers fall back
to timer-based polling — job processing continues with slightly higher latency.

---

## Entity Relationship Overview

A **Job** is the central entity. It holds the payload, tracks its own lifecycle status,
and records how many times it has been attempted.

A **Worker** registers itself on startup and heartbeats every 30 seconds. The **Worker
Watchdog** (a scheduled background process) detects stale workers by comparing
`last_heartbeat_at` to the current time and reclaims any jobs they were processing.

A **JobResult** stores the success output or failure reason after a job completes. It is
separate from the jobs table to keep the jobs table narrow — wide rows slow down the
`SELECT FOR UPDATE SKIP LOCKED` claim operation.

A **DeadLetterJob** records jobs that have exhausted their retry allowance, isolating
them from the main queue without blocking healthy job processing.

---

## Table Definitions

### `jobs`

| Column          | Type          | Constraints                              | Description                                         |
|-----------------|---------------|------------------------------------------|-----------------------------------------------------|
| `id`            | `VARCHAR(36)` | PRIMARY KEY                              | Prefixed UUID: `job_<uuid>`                         |
| `type`          | `VARCHAR(64)` | NOT NULL                                 | Job handler identifier (e.g. `send_email`)          |
| `payload`       | `JSONB`       | NOT NULL                                 | Arbitrary job data, max 64KB at application layer   |
| `status`        | `job_status`  | NOT NULL, DEFAULT 'pending'              | Enum: `pending`, `running`, `completed`, `failed`, `dead`, `cancelled` |
| `priority`      | `job_priority`| NOT NULL, DEFAULT 'normal'               | Enum: `high`, `normal`, `low`                       |
| `attempts`      | `SMALLINT`    | NOT NULL, DEFAULT 0                      | Number of times this job has been attempted         |
| `max_attempts`  | `SMALLINT`    | NOT NULL, DEFAULT 5, CHECK (max_attempts BETWEEN 1 AND 10) | Maximum retry limit |
| `worker_id`     | `VARCHAR(36)` | NULL, FK → workers                       | Currently claimed by — null when not running        |
| `scheduled_at`  | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()                  | Earliest time this job may be claimed               |
| `started_at`    | `TIMESTAMPTZ` | NULL                                     | When the current attempt began                      |
| `completed_at`  | `TIMESTAMPTZ` | NULL                                     | When the job reached a terminal status              |
| `created_at`    | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()                  | Immutable                                           |

**Why `JSONB` for payload?** Job payloads vary in structure by job type. JSONB allows
querying payload fields in PostgreSQL directly (e.g. filter jobs by `payload->>'to'` for
email jobs) without deserialising in the application layer. GIN indexes on JSONB fields
can accelerate these queries if needed.

**Why a database-level `job_status` enum?** The status must progress through a valid
sequence. An application bug that writes `status = 'finished'` (a misspelling) would be
silently stored in a VARCHAR column but rejected immediately by a PostgreSQL enum type.

### `job_results`

| Column          | Type          | Constraints             | Description                                    |
|-----------------|---------------|-------------------------|------------------------------------------------|
| `id`            | `VARCHAR(36)` | PRIMARY KEY             | Prefixed UUID                                  |
| `job_id`        | `VARCHAR(36)` | NOT NULL, UNIQUE, FK → jobs | One result per job                         |
| `success`       | `BOOLEAN`     | NOT NULL                | TRUE = completed, FALSE = permanently failed   |
| `output`        | `JSONB`       | NULL                    | Return value on success                        |
| `error_message` | `TEXT`        | NULL                    | Final error on permanent failure               |
| `completed_at`  | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW() | When this result was written                   |

**Separate table for results:** Keeping results separate from the `jobs` table ensures
the `SELECT FOR UPDATE SKIP LOCKED` claim query scans only the narrow `jobs` table.
Wide rows (with large JSONB output payloads) would bloat the main table and slow claims.

### `workers`

| Column              | Type           | Constraints               | Description                                   |
|---------------------|----------------|---------------------------|-----------------------------------------------|
| `id`                | `VARCHAR(36)`  | PRIMARY KEY               | Prefixed UUID: `wrk_<uuid>`                   |
| `hostname`          | `VARCHAR(256)` | NOT NULL                  | Server hostname for operational visibility    |
| `process_id`        | `INTEGER`      | NOT NULL                  | OS process ID for diagnostics                 |
| `status`            | `worker_status`| NOT NULL, DEFAULT 'idle'  | Enum: `idle`, `busy`, `offline`               |
| `current_job_id`    | `VARCHAR(36)`  | NULL, FK → jobs           | Job this worker is processing                 |
| `last_heartbeat_at` | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()   | Updated every 30 seconds                      |
| `registered_at`     | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()   | When this worker instance started             |

### `dead_letter_jobs`

| Column            | Type          | Constraints             | Description                               |
|-------------------|---------------|-------------------------|-------------------------------------------|
| `id`              | `VARCHAR(36)` | PRIMARY KEY             | Prefixed UUID                             |
| `original_job_id` | `VARCHAR(36)` | NOT NULL, UNIQUE        | Reference to the failed job               |
| `job_type`        | `VARCHAR(64)` | NOT NULL                | Denormalised for analyst filtering        |
| `failure_reason`  | `TEXT`        | NOT NULL                | Final error message from the last attempt |
| `attempts`        | `SMALLINT`    | NOT NULL                | How many times it was attempted           |
| `moved_at`        | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW() | When it was moved to dead letter          |

---

## Index Strategy

| Index Name                          | Table              | Columns                               | Type    | Query Pattern                                       |
|-------------------------------------|--------------------|---------------------------------------|---------|-----------------------------------------------------|
| `jobs_claim_idx`                    | `jobs`             | `(status, priority, scheduled_at)`    | B-tree  | **Core claim query** — PENDING + priority order + ready time |
| `jobs_type_status_idx`              | `jobs`             | `(type, status)`                      | B-tree  | List jobs by type and status (operator queries)     |
| `jobs_worker_id_idx`                | `jobs`             | `(worker_id) WHERE status = 'running'`| Partial B-tree | Find running jobs for a specific worker (watchdog) |
| `workers_heartbeat_idx`             | `workers`          | `(last_heartbeat_at)`                 | B-tree  | Watchdog: find stale workers older than 60s        |
| `workers_status_idx`                | `workers`          | `(status)`                            | B-tree  | Queue stats: count idle/busy workers               |
| `dead_letter_moved_at_idx`          | `dead_letter_jobs` | `(moved_at DESC)`                     | B-tree  | Operator: list recent dead letter jobs             |
| `dead_letter_type_idx`              | `dead_letter_jobs` | `(job_type)`                          | B-tree  | Operator: filter dead letters by job type          |

**The claim index (`jobs_claim_idx`) is the most performance-critical index in this
system.** The claim query is:

```sql
SELECT id FROM jobs
WHERE status = 'pending'
  AND scheduled_at <= NOW()
ORDER BY priority ASC, created_at ASC
LIMIT 1
FOR UPDATE SKIP LOCKED;
```

The composite index on `(status, priority, scheduled_at)` allows PostgreSQL to find the
next claimable job via an index scan rather than a table scan — critical when the jobs
table contains millions of completed rows.

---

## Relationship Types

- **Job → JobResult**: one-to-one (a job has at most one result, written at terminal state).
- **Job → Worker**: many-to-one (a worker processes one job at a time; many jobs reference the same worker over time).
- **Job → DeadLetterJob**: one-to-one (if it reaches dead letter status).

---

## Soft Delete Strategy

Jobs are never deleted from the `jobs` table — they reach a terminal status (`completed`,
`dead`, `cancelled`) and remain queryable. This allows producers to check a job's final
result at any time without a separate archiving concern.

Completed jobs older than 30 days should be moved to an archive partition or cold storage
table by a scheduled maintenance job — this keeps the main `jobs` table small and the
claim index fast.

---

## Audit Trail

| Table              | `created_at`      | Timestamp fields             | Notes                                          |
|--------------------|-------------------|------------------------------|------------------------------------------------|
| `jobs`             | ✓                 | `scheduled_at`, `started_at`, `completed_at` | Full lifecycle timestamps |
| `job_results`      | `completed_at`    | —                            | Immutable after write                          |
| `workers`          | `registered_at`   | `last_heartbeat_at`          | `last_heartbeat_at` updated every 30s          |
| `dead_letter_jobs` | `moved_at`        | —                            | Immutable                                      |
