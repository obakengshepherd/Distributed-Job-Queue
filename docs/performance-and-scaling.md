# Performance — Distributed Job Queue

---

## Current Bottlenecks

### Bottleneck 1: Claim query degrades without the partial index
The core claim query (`SELECT FOR UPDATE SKIP LOCKED`) must scan only PENDING jobs.
Without the partial index `jobs_claim_idx WHERE status = 'pending'`, it would scan
millions of completed rows on every poll. The partial index makes this O(log N)
on the small pending subset, regardless of total table size.

**Critical:** Never drop or disable `jobs_claim_idx`. Monitor its size and rebuild
if bloat exceeds 30% of table size.

### Bottleneck 2: Worker heartbeat write volume
100 workers × 1 update/30s = 3.3 writes/sec to `workers` table. Trivial alone,
but combined with job claim/complete/result writes at 12/sec, total write load
is ~15–20 writes/second. Light for PostgreSQL on NVMe; heavy for shared cloud DB.

### Bottleneck 3: Completed job table bloat
After 30 days at 1M jobs/day, the `jobs` table holds 30M rows. The partial index
only covers PENDING rows, so completed rows don't degrade claims — but they do
slow table-level operations (VACUUM, backup, full scans for operator queries).

**Mitigation:** Monthly maintenance job moves completed/dead jobs older than 30
days to an archive table or cold storage.

---

## Cache Hit Rate Targets

| Key                          | Target | Notes                                  |
|-----------------------------|--------|----------------------------------------|
| `worker:{id}:alive`          | N/A    | Written on heartbeat; TTL is the signal|
| `counter:queue:{priority}`   | N/A    | Approximate counter only               |
| `job:submitted:{key}`        | ≥ 95%  | SETNX dedup; 1h TTL                    |

---

## Database Read Replica Routing

| Operation                         | Target        | Reason                              |
|----------------------------------|---------------|--------------------------------------|
| `SELECT FOR UPDATE SKIP LOCKED`  | **Primary**   | Requires primary for row-level locks |
| `GET /jobs/{id}` (status poll)   | Read replica  | Display; eventual OK                 |
| `GET /queue/stats`               | Read replica  | Monitoring dashboard; eventual OK    |
| `SELECT dead_letter_jobs`        | Read replica  | Operator investigation; not urgent   |
| All job lifecycle writes         | **Primary**   | State machine writes                 |
| Worker heartbeat updates         | **Primary**   | Liveness tracking                    |

---

## Connection Pool Sizing

| Service           | Pool Size | Rationale                                               |
|-------------------|-----------|---------------------------------------------------------|
| Workers (per)     | 2         | Each worker holds one claim connection briefly (ms)     |
| Job Submission API| 10        | Low-traffic write path                                  |
| PgBouncer mode    | Transaction| Workers release connection between claim and execute   |

**PgBouncer transaction mode is critical here.** Workers in `ExecuteAsync` may take
seconds to minutes. If the DB connection were held during execution (session mode),
100 workers would hold 100 PostgreSQL connections for the entire job duration —
exhausting the connection limit. Transaction mode releases between statements.

---

## Query Performance Targets

| Query                                          | Target p95 | Target p99 | Index                     |
|------------------------------------------------|-----------|-----------|---------------------------|
| `SELECT FOR UPDATE SKIP LOCKED` (claim)        | < 3ms     | < 8ms     | `jobs_claim_idx` (partial)|
| `UPDATE jobs SET status = 'running'`           | < 3ms     | < 5ms     | PK                        |
| `INSERT INTO job_results`                      | < 5ms     | < 10ms    | PK                        |
| `UPDATE workers SET last_heartbeat_at`         | < 2ms     | < 5ms     | PK                        |
| `GET /jobs/{id}` (status read)                 | < 2ms     | < 5ms     | PK                        |
| Stale worker detection (`WHERE heartbeat < ?`) | < 5ms     | < 10ms    | `workers_heartbeat_idx`   |

---

## Rate Limiting Configuration

| Policy           | Limit  | Window | Endpoint                    |
|------------------|--------|--------|-----------------------------|
| job-submit       | 1,000  | 1 min  | `POST /jobs`                |
| worker-heartbeat | 600    | 1 min  | `POST /workers/heartbeat`   |
| reads            | 120    | 1 min  | `GET /jobs`, `GET /queue/stats` |
| unauthenticated  | 10     | 1 min  | By IP                       |

---

# Scaling Strategy — Distributed Job Queue

---

## Horizontal Scaling Table

| Component              | Scales Horizontally? | Notes                                               |
|------------------------|---------------------|-----------------------------------------------------|
| Workers                | ✅ Yes               | Primary scaling lever; add freely                   |
| Job Submission API     | ✅ Yes               | Stateless; low traffic; 2–3 instances sufficient    |
| RabbitMQ               | ✅ Yes (Cluster)     | Notification queue is non-durable; cluster optional |
| Redis                  | ✅ Yes (Cluster)     | Heartbeat keys + counters; shard by worker_id hash  |
| PostgreSQL primary     | ❌ No (writes)       | Single primary; claim queries require primary        |
| PostgreSQL replicas    | ✅ Yes               | Status reads, operator queries                      |

**The primary scaling action for the Job Queue is always: add worker instances.**
The bottleneck is almost never the API or the database — it is the number of
workers processing jobs in parallel.

---

## Load Balancing Configuration

**Job Submission API (stateless):**
```
Algorithm:   Round-Robin
Affinity:    None
Health:      GET /health every 10s; 3 failures = remove; 2 successes = restore
```

**Workers (not HTTP load balanced — PostgreSQL distributes via SKIP LOCKED):**
```
Distribution: SELECT FOR UPDATE SKIP LOCKED (database-level distribution)
Max instances: Limited by PostgreSQL connection pool capacity
Scale trigger: Queue depth (PENDING jobs) > 1,000 and growing
Scale down:    Queue depth = 0 for 5 consecutive minutes
```

---

## Stateless Design Guarantees

1. **Workers are stateless processes.** A worker that crashes mid-job loses only
   the in-progress job, which the Watchdog reclaims within 60 seconds. No
   application-level state is lost.

2. **Job claiming is atomic at the DB layer.** `SELECT FOR UPDATE SKIP LOCKED`
   handles all concurrency — no application-layer coordination or locking needed.

3. **Heartbeat is idempotent.** A delayed heartbeat arriving after the watchdog
   has already marked a worker stale simply updates the timestamp; it does not
   conflict with any other operation.

4. **Dead letter jobs are isolated.** A poisoned job type does not affect healthy
   jobs. Dead letter isolation is at the table level, not the application level.

---

## Worker Scaling Formula

```
Required workers = ceil(target_jobs_per_day / (86400 / avg_job_duration_seconds))

Example (1M jobs/day, avg 10s per job):
  = ceil(1,000,000 / (86400 / 10))
  = ceil(1,000,000 / 8,640)
  = ceil(115.7)
  = 116 workers
```

Add 25% headroom for burst: target **145 workers** at 1M jobs/day average load.

---

## Scaling Triggers

| Metric                              | Threshold       | Action                                      |
|-------------------------------------|-----------------|---------------------------------------------|
| Queue depth (PENDING jobs)          | > 1,000 growing | Add 10 worker instances                     |
| Queue depth (PENDING jobs)          | > 10,000        | Emergency scale: add max workers            |
| Worker p95 job start delay          | > 5s            | Add workers immediately                     |
| Dead letter job creation rate       | > 10/hour       | Alert; investigate job handler              |
| PostgreSQL claim query p99          | > 15ms          | Check `jobs_claim_idx` health; REINDEX if needed |
