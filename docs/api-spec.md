# API Specification — Distributed Job Queue

---

## Overview

The Job Queue API allows producer services to submit background jobs, query their status,
and manage the queue operationally. Workers use the heartbeat endpoint to signal liveness.
The API does not handle job execution — that is entirely internal to the worker pool. This
API is consumed by any upstream service that needs to offload work asynchronously, and by
operators monitoring the queue's health.

---

## Base URL and Versioning

```
https://api.jobs.internal/api/v1
```

---

## Authentication

```
Authorization: Bearer <jwt_token>
```

Role-based access:
- `producer` — may submit, cancel, and query jobs
- `worker` — may only call `POST /workers/heartbeat`
- `operator` — full access including queue stats and dead letter management

---

## Common Response Envelope

### Success
```json
{
  "data": { ... },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

### Error
```json
{
  "error": {
    "code": "JOB_NOT_CANCELLABLE",
    "message": "Only jobs in PENDING status can be cancelled.",
    "details": [{ "field": "status", "issue": "Current status is RUNNING" }]
  },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

---

## Rate Limiting

| Endpoint                  | Limit              | Scope        |
|--------------------------|--------------------|--------------|
| `POST /jobs`              | 1000 / minute      | Per service  |
| `POST /workers/heartbeat` | 600 / minute       | Per worker   |
| All read endpoints        | 120 / minute       | Per user     |

---

## Endpoints

---

### POST /jobs

**Description:** Submits a new background job for async processing. Returns the job ID
immediately. Execution begins as soon as a worker is available and `scheduled_at` is reached.

**Request Body:**

| Field          | Type    | Required | Validation                           | Example                    |
|----------------|---------|----------|--------------------------------------|----------------------------|
| `type`         | string  | Yes      | Registered job type, max 64 chars    | `"send_email"`             |
| `payload`      | object  | Yes      | Valid JSON, max 64KB                 | `{"to":"user@example.com"}`|
| `priority`     | string  | No       | `HIGH`, `NORMAL`, `LOW` (default NORMAL) | `"HIGH"`              |
| `scheduled_at` | string  | No       | ISO8601, defaults to now             | `"2024-01-15T11:00:00Z"`   |
| `max_attempts` | integer | No       | 1–10, default 5                      | `3`                        |

**Example Request:**
```json
{
  "type": "send_email",
  "payload": {
    "to": "user@example.com",
    "subject": "Your receipt",
    "template": "receipt_v2"
  },
  "priority": "NORMAL",
  "max_attempts": 3
}
```

**Response — 201 Created:**
```json
{
  "data": {
    "id": "job_01j9z3k4m5n6p7q8",
    "type": "send_email",
    "status": "PENDING",
    "priority": "NORMAL",
    "attempts": 0,
    "max_attempts": 3,
    "scheduled_at": "2024-01-15T10:30:00Z",
    "created_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                              |
|------|----------------------------------------|
| 201  | Job queued                             |
| 400  | Invalid payload, unknown job type      |
| 401  | Unauthorized                           |
| 422  | Payload exceeds 64KB limit             |
| 429  | Rate limit exceeded                    |

---

### GET /jobs/{id}

**Description:** Returns the current status, attempt count, and result (if completed or
failed) for a specific job.

**Path Parameters:** `id` — Job ID

**Response — 200 OK:**
```json
{
  "data": {
    "id": "job_01j9z3k4m5n6p7q8",
    "type": "send_email",
    "status": "COMPLETED",
    "priority": "NORMAL",
    "attempts": 1,
    "max_attempts": 3,
    "scheduled_at": "2024-01-15T10:30:00Z",
    "started_at": "2024-01-15T10:30:02Z",
    "completed_at": "2024-01-15T10:30:05Z",
    "result": {
      "success": true,
      "output": { "message_id": "msg_abc123" }
    },
    "created_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition       |
|------|-----------------|
| 200  | Success         |
| 401  | Unauthorized    |
| 404  | Job not found   |

---

### DELETE /jobs/{id}

**Description:** Cancels a job that is currently in PENDING status. Running, completed,
failed, and dead jobs cannot be cancelled.

**Path Parameters:** `id` — Job ID

**Response — 200 OK:**
```json
{
  "data": {
    "id": "job_01j9z3k4m5n6p7q8",
    "status": "CANCELLED",
    "cancelled_at": "2024-01-15T10:31:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                    |
|------|----------------------------------------------|
| 200  | Job cancelled                                |
| 401  | Unauthorized                                 |
| 404  | Job not found                                |
| 422  | Job is not in PENDING status                 |

---

### GET /jobs

**Description:** Returns a paginated list of jobs filtered by status and/or type.

**Query Parameters:**

| Parameter | Type    | Default  | Description                                      |
|-----------|---------|----------|--------------------------------------------------|
| `status`  | string  | —        | `PENDING`, `RUNNING`, `COMPLETED`, `FAILED`, `DEAD` |
| `type`    | string  | —        | Filter by job type                               |
| `limit`   | integer | `20`     | Page size, max 100                               |
| `cursor`  | string  | —        | Pagination cursor                                |

**Response — 200 OK:**
```json
{
  "data": [
    {
      "id": "job_01j9z3k4m5n6p7q8",
      "type": "send_email",
      "status": "PENDING",
      "priority": "NORMAL",
      "attempts": 0,
      "scheduled_at": "2024-01-15T10:30:00Z",
      "created_at": "2024-01-15T10:30:00Z"
    }
  ],
  "pagination": { "cursor": "eyJpZCI6ImR9", "has_more": false, "limit": 20 },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                     |
|------|-------------------------------|
| 200  | Success                       |
| 400  | Invalid status value          |
| 401  | Unauthorized                  |

---

### POST /workers/heartbeat

**Description:** Worker sends a heartbeat to signal it is alive and processing. Must be
called every 30 seconds. Workers that miss two consecutive heartbeats (60s gap) are marked
stale and their in-progress jobs are reclaimed.

**Request Body:**

| Field       | Type   | Required | Validation                    | Example                      |
|-------------|--------|----------|-------------------------------|------------------------------|
| `worker_id` | string | Yes      | UUID, previously registered   | `"wrk_01j9z3k4m5n6p7q8"`    |
| `job_id`    | string | No       | UUID of currently claimed job | `"job_01j9z3k4m5n6p7q8"`    |
| `status`    | string | Yes      | `idle` or `busy`              | `"busy"`                     |

**Response — 204 No Content**

**Status Codes:**

| Code | Condition                           |
|------|-------------------------------------|
| 204  | Heartbeat recorded                  |
| 400  | Missing worker_id or invalid status |
| 401  | Unauthorized                        |
| 403  | Token does not have `worker` role   |
| 404  | Worker ID not registered            |

---

### GET /queue/stats

**Description:** Returns current queue depth by status and priority, active worker count,
average job processing time, and dead letter job count.

**Response — 200 OK:**
```json
{
  "data": {
    "queue_depth": {
      "PENDING": { "HIGH": 12, "NORMAL": 143, "LOW": 7 },
      "RUNNING": 48,
      "COMPLETED_TODAY": 84321,
      "FAILED_TODAY": 23,
      "DEAD": 5
    },
    "workers": {
      "total": 100,
      "active": 48,
      "idle": 52,
      "stale": 0
    },
    "performance": {
      "avg_processing_time_ms": 4823,
      "avg_queue_delay_ms": 312,
      "jobs_per_minute": 586
    },
    "snapshot_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                        |
|------|----------------------------------|
| 200  | Success                          |
| 401  | Unauthorized                     |
| 403  | Requires `operator` role         |
