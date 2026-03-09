# Failure Scenarios — Distributed Job Queue

> **Status**: Skeleton — stubs defined on Day 2. Full mitigations and implementations added on Day 27.

---

## Scenario 1 — Worker Crashes Mid-Execution

**Trigger**: A worker process crashes (OOM, unhandled exception, VM termination) while a
job is in RUNNING status. The job is never completed or failed — it remains orphaned.

**Component that fails**: Worker process.

**Impact**: Internal — the job is stuck in RUNNING state indefinitely unless a recovery
mechanism reclaims it.

**Mitigation strategy**: TBD Day 27 — involves Worker Watchdog detecting stale heartbeat,
resetting job to PENDING for retry if attempts < max_attempts, moving to dead letter if
max_attempts reached.

---

## Scenario 2 — Poison Pill Job

**Trigger**: A job is submitted with a malformed payload or triggers a consistent bug in
the handler code. It fails on every attempt.

**Component that fails**: Job handler logic / job payload.

**Impact**: Internal — job is retried max_attempts times with exponential backoff, then
moved to dead_letter_jobs. The job does not block other jobs but consumes worker time
on each retry.

**Mitigation strategy**: TBD Day 27 — involves exponential backoff between retries, dead
letter isolation after max_attempts, alerting on dead letter job creation, and tooling to
re-queue dead letter jobs after the handler bug is fixed.

---

## Scenario 3 — PostgreSQL Primary Outage

**Trigger**: The PostgreSQL primary becomes unavailable. Workers cannot claim new jobs,
complete jobs, or heartbeat.

**Component that fails**: PostgreSQL primary.

**Impact**: Internal — all job processing halts. In-progress jobs whose workers lose their
database connection will fail their current execution attempt. No new jobs can be claimed.

**Mitigation strategy**: TBD Day 27 — involves automatic PostgreSQL failover to replica,
worker reconnect logic with exponential backoff, and reconciliation of in-flight job states
after primary recovery.

---

## Scenario 4 — RabbitMQ Notification Queue Failure

**Trigger**: RabbitMQ becomes unavailable. New job notifications are not delivered to workers.

**Component that fails**: RabbitMQ broker.

**Impact**: Internal — workers fall back to timer-based polling. Jobs are still processed
but with up to a 1-second additional delay (the polling interval). No job loss occurs.

**Mitigation strategy**: TBD Day 27 — involves fallback polling loop in worker when
RabbitMQ connection fails, alerting on RabbitMQ unavailability, automatic reconnect when
broker recovers.

---

## Scenario 5 — Database Connection Pool Exhaustion

**Trigger**: All available PgBouncer connections are in use. New workers attempting to
claim jobs cannot acquire a connection.

**Component that fails**: PostgreSQL connection pool.

**Impact**: Internal — job claiming is delayed or fails. Workers queue up waiting for a
connection. System throughput degrades proportionally to the connection wait time.

**Mitigation strategy**: TBD Day 27 — involves connection pool sizing to match worker count,
circuit breaker pattern on connection acquisition, alerting on pool saturation, and
PgBouncer transaction-mode configuration to minimise connection hold time per worker.
