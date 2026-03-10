# Scaling Strategy — Distributed Job Queue

---

## Current Single-Node Bottlenecks

- **PostgreSQL polling load**: Each worker polls for jobs by executing a `SELECT FOR UPDATE
  SKIP LOCKED` query. At 100 workers polling every second (in the absence of RabbitMQ wake
  signals), that is 100 database queries per second for job claiming alone. With RabbitMQ
  wake signals, workers sleep when the queue is empty and only wake on new job notifications —
  dramatically reducing idle polling load.

- **Job insert → claim latency**: A job submitted via the API must be committed to PostgreSQL
  before a worker can claim it. If the database is under heavy write load (high job completion
  result writes + new job inserts + heartbeat updates), claim latency can creep up.

- **Worker heartbeat volume**: 100 workers heartbeating every 30 seconds = ~3 heartbeat
  writes per second. Low individually, but combined with job state update writes, it
  contributes to total PostgreSQL write load.

- **Dead letter job accumulation**: If a flawed job type is deployed, many jobs may fail
  repeatedly and accumulate in the dead letter table. The dead letter table does not affect
  main queue performance (it is a separate table), but it signals an operational problem
  that should trigger an alert and investigation.

---

## Horizontal Scaling Plan

### Workers

Workers are the primary scaling lever. Add worker instances to increase throughput.
The `SELECT FOR UPDATE SKIP LOCKED` mechanism scales perfectly with additional workers:
each additional worker adds capacity without any configuration change.

Hard limit: the number of effective workers is bounded by the number of job rows in the
PENDING state divided by the time to process a single job. Adding workers beyond this
point provides no benefit for throughput (no jobs to claim) but adds polling load.

Target: scale workers based on queue depth metric. When `jobs WHERE status=PENDING` count
exceeds 1,000 and is growing, trigger auto-scaling of worker instances.

### Job Submission API

The API is stateless. Scale horizontally with round-robin load balancing. API throughput
is rarely the bottleneck — job submission is a simple database insert.

### PostgreSQL

**Phase 1 — Connection pooling**: With 100 workers each holding a database connection
during job execution, connection count is a concern at scale. Add PgBouncer in front of
PostgreSQL. Workers connect to PgBouncer in transaction-mode pooling; they hold a real
PostgreSQL connection only during the `SELECT FOR UPDATE SKIP LOCKED` and the subsequent
status update — a few milliseconds, not the entire job execution duration.

**Phase 2 — Read replicas**: Route job status queries (`GET /jobs/{id}`) and analytics
queries to a read replica. All writes (job creation, status updates, heartbeats) target
the primary.

**Phase 3 — Partition `jobs` table**: As the `jobs` table grows, partition by `created_at`
month. Completed and dead jobs from old partitions can be archived. The active partition
(PENDING + RUNNING jobs) remains small and is the only partition touched during normal
operation.

**Phase 4 — Separate result storage**: The `job_results` table can be large (results may
be multi-kilobyte JSON payloads). If result storage becomes a concern, consider moving
results to an object store (e.g., S3-compatible) and storing only a reference in
`job_results`.

### RabbitMQ

A single RabbitMQ instance handles the `jobs.notify` queue with ease. The messages are
empty notifications (no payload) and are consumed immediately — the queue should never
grow beyond a few hundred messages. No scaling is needed at the defined scale.

---

## Queue Depth and Worker Scaling Triggers

| Metric                              | Threshold  | Action                              |
|-------------------------------------|------------|-------------------------------------|
| `jobs WHERE status=PENDING` count   | > 1,000    | Alert; consider scaling workers     |
| `jobs WHERE status=PENDING` count   | > 10,000   | Scale workers immediately           |
| Worker heartbeat gap                | > 60s      | Watchdog reclaims job; alert        |
| Dead letter job count (24h window)  | > 100      | Alert; investigate job handler      |
| Average job processing time         | > 2x SLA   | Alert; investigate job handler perf |

---

## Throughput Targets

| Metric                         | Target                            |
|--------------------------------|-----------------------------------|
| Jobs processed per day         | 1,000,000                         |
| Average jobs per second        | ~12                               |
| Peak jobs per second           | ~100 (burst scenario)             |
| Job claim latency (p95)        | ≤ 100ms from submission           |
| Job start delay (p95)          | ≤ 5s from submission at normal load |
| Worker count (normal)          | 100                               |
| Worker count (max, scaled)     | Up to partition/connection limit  |

At 1M jobs/day with 100 workers, and assuming an average job takes 10 seconds to process,
each worker handles ~86,400 seconds ÷ 10 seconds = ~8,640 jobs per day. 100 workers
handle ~864,000 jobs per day. For 1M jobs/day, slightly more than 100 workers are needed,
or average job processing time must be under 9 seconds.

This capacity calculation should be reviewed whenever new job types are introduced with
significantly different processing time characteristics.
