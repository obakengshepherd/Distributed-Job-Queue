# Cache Strategy & Event Schema — Distributed Job Queue

---

## Cache Strategy

### Pattern 1: Worker Heartbeat TTL Keys

Redis stores a lightweight liveness signal for each worker, separate from the
authoritative heartbeat timestamp in PostgreSQL.

```
Worker heartbeat cycle (every 30s):
  1. UPDATE workers SET last_heartbeat_at = NOW()   ← PostgreSQL (authoritative)
  2. SET worker:{id}:alive 1 EX 45                  ← Redis (fast liveness check)

Watchdog check (every 30s):
  1. For each registered worker:
     - QUICK CHECK: EXISTS worker:{id}:alive        ← Redis O(1) check
     - AUTHORITATIVE CHECK: last_heartbeat_at < now()-60s  ← PostgreSQL
  2. Workers failing both checks → mark offline, reclaim jobs
```

The Redis check reduces Watchdog database load: only workers that look stale in
Redis (TTL expired) require a PostgreSQL query to confirm.

### Pattern 2: Queue Depth Counters (Approximate)

```
On job submit:  INCR counter:queue:{priority}
On job claim:   DECR counter:queue:{priority}  (guarded: max(0, current-1))

GET /queue/stats: reads counters from Redis (O(1)) instead of COUNT(*) from DB
```

The counters may drift from the true count (e.g. if a worker crashes while
claiming without decrementing). They reset correctly on the next maintenance
cycle when all stale records are cleaned up. For monitoring dashboards, this
approximation is acceptable. For production decisions (scaling), query the DB.

### Pattern 3: Submission Deduplication (SETNX)

```
POST /jobs {idempotency_key: key}
  1. SET job:submitted:{key} 1 NX EX 3600   ← SET if Not eXists, TTL 1h
  2. Returns false (key existed) → return 409 Conflict
  3. Returns true (key set)      → proceed with job creation
```

---

## Key Inventory

| Key Pattern                     | Type   | TTL   | Purpose                              |
|---------------------------------|--------|-------|--------------------------------------|
| `worker:{id}:alive`             | String | 45s   | Fast liveness check for Watchdog     |
| `counter:queue:{priority}`      | String | None  | Approximate queue depth monitoring   |
| `job:submitted:{key}`           | String | 1h    | Submission deduplication (SETNX)     |

---

## Event Schema

### RabbitMQ Queue: `jobs.notify` (non-durable)

- **Producer:** JobService (on every `POST /jobs`)
- **Consumer:** JobNotificationConsumer (one per worker instance)
- **Exchange type:** Default (direct, no exchange)
- **Prefetch:** 1 per consumer — each notification wakes exactly one worker

**Message body:** `{}` (empty JSON — the payload is irrelevant, only the arrival matters)

**Dead letter:** `jobs.dlx` exchange → `jobs.dead` queue
(Dead letters are harmless — a failed wake signal means a worker polls on its
fallback timer instead. The DLQ exists only for observability.)

### Why the Notify Queue Uses Non-Durable Messages

Wake signals are ephemeral. If the broker restarts, workers fall back to timer-based
polling automatically. Persisting wake signals would waste broker disk I/O for no
correctness benefit.

### Consumer Competing Model (Round-Robin)

With `prefetchCount: 1`, RabbitMQ delivers each message to only one consumer at a
time. When 100 workers are connected and 100 jobs are submitted simultaneously, each
worker receives exactly one notification and one job. This is the correct round-robin
distribution model for a work queue.

```
100 jobs submitted → 100 notifications → each of 100 workers receives 1 notification
                                        → each worker polls PostgreSQL
                                        → SELECT FOR UPDATE SKIP LOCKED claims one job
                                        → 100 jobs processed in parallel, no conflicts
```
