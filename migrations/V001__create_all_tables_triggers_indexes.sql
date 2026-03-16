-- =============================================================================
-- V001__create_job_types.sql
-- Distributed Job Queue — Custom enum types
--
-- ROLLBACK:
--   DROP TYPE IF EXISTS job_status CASCADE;
--   DROP TYPE IF EXISTS job_priority CASCADE;
--   DROP TYPE IF EXISTS worker_status CASCADE;
-- =============================================================================

-- job_status enum prevents invalid status strings at the DB level.
-- A worker bug writing status = 'done' (not a valid value) is caught
-- immediately by PostgreSQL rather than corrupting the queue state.
CREATE TYPE job_status    AS ENUM ('pending', 'running', 'completed', 'failed', 'dead', 'cancelled');
CREATE TYPE job_priority  AS ENUM ('high', 'normal', 'low');
CREATE TYPE worker_status AS ENUM ('idle', 'busy', 'offline');

-- =============================================================================
-- V002__create_jobs_table.sql
-- Distributed Job Queue — jobs table (core claiming entity)
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS jobs CASCADE;
-- =============================================================================

CREATE TABLE jobs (
    id            VARCHAR(36)   NOT NULL,
    type          VARCHAR(64)   NOT NULL,
    payload       JSONB         NOT NULL,
    status        job_status    NOT NULL DEFAULT 'pending',
    priority      job_priority  NOT NULL DEFAULT 'normal',
    attempts      SMALLINT      NOT NULL DEFAULT 0,
    max_attempts  SMALLINT      NOT NULL DEFAULT 5,
    worker_id     VARCHAR(36)   NULL,
    -- FK to workers added in V003 after workers table exists
    scheduled_at  TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    started_at    TIMESTAMPTZ   NULL,
    completed_at  TIMESTAMPTZ   NULL,
    created_at    TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT jobs_pkey PRIMARY KEY (id),

    CONSTRAINT jobs_max_attempts_range
        CHECK (max_attempts BETWEEN 1 AND 10),

    CONSTRAINT jobs_attempts_non_negative
        CHECK (attempts >= 0),

    CONSTRAINT jobs_attempts_le_max
        CHECK (attempts <= max_attempts),

    CONSTRAINT jobs_scheduled_after_created
        CHECK (scheduled_at >= created_at),

    CONSTRAINT jobs_started_after_scheduled
        CHECK (started_at IS NULL OR started_at >= scheduled_at),

    CONSTRAINT jobs_completed_after_started
        CHECK (completed_at IS NULL OR started_at IS NULL OR completed_at >= started_at),

    -- Enforce that running jobs have a worker_id set
    CONSTRAINT jobs_running_has_worker
        CHECK (status != 'running' OR worker_id IS NOT NULL)
);

COMMENT ON TABLE jobs IS
    'Central job entity. Primary coordination mechanism is '
    'SELECT id FROM jobs WHERE status=''pending'' AND scheduled_at <= NOW() '
    'ORDER BY priority ASC, created_at ASC LIMIT 1 FOR UPDATE SKIP LOCKED. '
    'SKIP LOCKED allows concurrent workers to claim different rows atomically '
    'without deadlocks or duplicate claiming.';

COMMENT ON COLUMN jobs.priority IS
    'Enum with collation: ''high'' < ''normal'' < ''low'' in sort order — '
    'ORDER BY priority ASC gives highest priority jobs first.';

COMMENT ON COLUMN jobs.worker_id IS
    'NULL when pending. Set when claimed (status=running). '
    'Cleared when completed/failed. FK added in V003.';

-- =============================================================================
-- V003__create_workers_results_deadletter.sql
-- Distributed Job Queue — workers, job_results, dead_letter_jobs tables
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS dead_letter_jobs CASCADE;
--   DROP TABLE IF EXISTS job_results CASCADE;
--   DROP TABLE IF EXISTS workers CASCADE;
--   ALTER TABLE jobs DROP CONSTRAINT IF EXISTS jobs_worker_fk;
-- =============================================================================

CREATE TABLE workers (
    id                  VARCHAR(36)    NOT NULL,
    hostname            VARCHAR(256)   NOT NULL,
    process_id          INTEGER        NOT NULL,
    status              worker_status  NOT NULL DEFAULT 'idle',
    current_job_id      VARCHAR(36)    NULL,
    last_heartbeat_at   TIMESTAMPTZ    NOT NULL DEFAULT NOW(),
    registered_at       TIMESTAMPTZ    NOT NULL DEFAULT NOW(),

    CONSTRAINT workers_pkey PRIMARY KEY (id),

    CONSTRAINT workers_current_job_fk
        FOREIGN KEY (current_job_id) REFERENCES jobs (id)
        ON DELETE SET NULL
);

COMMENT ON TABLE workers IS
    'Worker registry. Heartbeat every 30 seconds to last_heartbeat_at. '
    'Watchdog marks workers stale if last_heartbeat_at < NOW() - 60s. '
    'Stale workers'' jobs are reclaimed: status reset to pending (if retryable) '
    'or moved to dead_letter_jobs (if attempts exhausted).';

COMMENT ON COLUMN workers.last_heartbeat_at IS
    'Updated every 30 seconds. Workers stale after 60s gap. '
    'Watchdog reclaims orphaned jobs from stale workers.';

-- Add FK from jobs to workers now that workers table exists
ALTER TABLE jobs
    ADD CONSTRAINT jobs_worker_fk
        FOREIGN KEY (worker_id) REFERENCES workers (id)
        ON DELETE SET NULL;
        -- SET NULL: if a worker record is deleted (cleanup), jobs it claimed
        -- have their worker_id cleared. The watchdog would have already reclaimed
        -- these jobs before deletion, but SET NULL is a safety net.

CREATE TABLE job_results (
    id             VARCHAR(36)  NOT NULL,
    job_id         VARCHAR(36)  NOT NULL,
    success        BOOLEAN      NOT NULL,
    output         JSONB        NULL,
    error_message  TEXT         NULL,
    completed_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT job_results_pkey PRIMARY KEY (id),

    -- One result per job — enforced at DB level.
    CONSTRAINT job_results_job_unique UNIQUE (job_id),

    CONSTRAINT job_results_job_fk
        FOREIGN KEY (job_id) REFERENCES jobs (id)
        ON DELETE CASCADE,

    -- output populated on success; error_message on failure
    CONSTRAINT job_results_success_has_no_error
        CHECK (NOT success OR error_message IS NULL),

    CONSTRAINT job_results_failure_has_error
        CHECK (success OR error_message IS NOT NULL)
);

COMMENT ON TABLE job_results IS
    'Separate from jobs table to keep the claim query scanning a narrow jobs table. '
    'Wide result payloads would bloat the jobs table and slow SELECT FOR UPDATE SKIP LOCKED.';

CREATE TABLE dead_letter_jobs (
    id                VARCHAR(36)  NOT NULL,
    original_job_id   VARCHAR(36)  NOT NULL,
    job_type          VARCHAR(64)  NOT NULL,
    failure_reason    TEXT         NOT NULL,
    attempts          SMALLINT     NOT NULL,
    moved_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT dead_letter_jobs_pkey PRIMARY KEY (id),
    CONSTRAINT dead_letter_jobs_job_unique UNIQUE (original_job_id)
);

COMMENT ON TABLE dead_letter_jobs IS
    'Isolation queue for permanently failing jobs. '
    'Does not reference jobs table via FK — jobs are left in terminal ''dead'' status. '
    'Operators can inspect, fix the handler, and re-queue via the admin API.';

-- =============================================================================
-- V004__add_triggers.sql
-- Distributed Job Queue — Watchdog support trigger
-- =============================================================================

-- Automatically update workers.last_heartbeat_at tracking
-- The application always calls POST /workers/heartbeat — this is a safety net
CREATE OR REPLACE FUNCTION workers_mark_busy()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.current_job_id IS NOT NULL AND OLD.current_job_id IS NULL THEN
        NEW.status = 'busy';
    ELSIF NEW.current_job_id IS NULL AND OLD.current_job_id IS NOT NULL THEN
        NEW.status = 'idle';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workers_status_sync_trigger
    BEFORE UPDATE ON workers
    FOR EACH ROW
    EXECUTE FUNCTION workers_mark_busy();

COMMENT ON TRIGGER workers_status_sync_trigger ON workers IS
    'Auto-syncs worker status with current_job_id: '
    'non-null current_job_id → busy; null → idle.';

-- =============================================================================
-- V005__add_indexes.sql
-- Distributed Job Queue — All performance indexes
--
-- ROLLBACK (reverse order):
--   DROP INDEX IF EXISTS dead_letter_type_idx;
--   DROP INDEX IF EXISTS dead_letter_moved_at_idx;
--   DROP INDEX IF EXISTS workers_status_idx;
--   DROP INDEX IF EXISTS workers_heartbeat_idx;
--   DROP INDEX IF EXISTS jobs_running_worker_idx;
--   DROP INDEX IF EXISTS jobs_type_status_idx;
--   DROP INDEX IF EXISTS jobs_claim_idx;
-- =============================================================================

-- THE MOST CRITICAL INDEX in this system.
-- Powers the core claim query:
-- SELECT id FROM jobs WHERE status='pending' AND scheduled_at <= NOW()
-- ORDER BY priority ASC, created_at ASC LIMIT 1 FOR UPDATE SKIP LOCKED
--
-- Composite on (status, priority, scheduled_at) allows an index range scan:
-- 1. Filter to status='pending' rows (small subset of the full table)
-- 2. Within that, rows where scheduled_at <= NOW()
-- 3. Return in priority + created_at order without a sort
--
-- Without this index, every claim would require a full table scan across
-- potentially millions of completed job rows.
CREATE INDEX jobs_claim_idx
    ON jobs (status, priority ASC, scheduled_at ASC)
    WHERE status = 'pending';
-- Partial: only pending jobs. The majority of jobs are completed.

COMMENT ON INDEX jobs_claim_idx IS
    'THE core performance index. Powers SELECT FOR UPDATE SKIP LOCKED claim query. '
    'Partial (WHERE status=pending) — dramatically smaller than a full index. '
    'Completed/failed/dead jobs are not in this index.';

-- Query: Operator — list jobs by type and status
CREATE INDEX jobs_type_status_idx
    ON jobs (type, status, created_at DESC);

-- Query: Watchdog — find running jobs belonging to a stale worker
CREATE INDEX jobs_running_worker_idx
    ON jobs (worker_id, status)
    WHERE status = 'running';

COMMENT ON INDEX jobs_running_worker_idx IS
    'Watchdog query: SELECT ... FROM jobs WHERE worker_id = $stale_id AND status=running. '
    'Partial index: only running jobs (small fraction of total).';

-- Query: Queue stats — count workers by status
CREATE INDEX workers_status_idx
    ON workers (status);

-- Query: Watchdog — find stale workers
CREATE INDEX workers_heartbeat_idx
    ON workers (last_heartbeat_at ASC);

COMMENT ON INDEX workers_heartbeat_idx IS
    'Watchdog: SELECT ... WHERE last_heartbeat_at < NOW() - INTERVAL ''60 seconds''. '
    'ASC ordering — oldest heartbeats first.';

-- Query: Dead letter analysis by job type
CREATE INDEX dead_letter_type_idx
    ON dead_letter_jobs (job_type, moved_at DESC);

-- Query: Recent dead letter jobs
CREATE INDEX dead_letter_moved_at_idx
    ON dead_letter_jobs (moved_at DESC);

ANALYZE jobs;
ANALYZE workers;
ANALYZE job_results;
ANALYZE dead_letter_jobs;
