-- 009_add_embedding_dimension_tracking.sql: Track embedding configuration and migration history

-- Track embedding configuration (model + dimensions)
-- Only one active config at a time - represents current expected state
CREATE TABLE IF NOT EXISTS embedding_config (
    id SERIAL PRIMARY KEY,
    model_name TEXT NOT NULL,
    dimensions INT NOT NULL,
    detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_active BOOLEAN NOT NULL DEFAULT true
);

-- Ensure only one active config at a time
CREATE UNIQUE INDEX IF NOT EXISTS idx_embedding_config_active
    ON embedding_config(is_active) WHERE is_active = true;

-- Migration history for audit trail
-- Tracks every dimension migration run, including partial/failed ones for resumability
CREATE TABLE IF NOT EXISTS embedding_dimension_migrations (
    migration_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,

    -- What changed
    old_model TEXT NOT NULL,
    old_dimensions INT NOT NULL,
    new_model TEXT NOT NULL,
    new_dimensions INT NOT NULL,

    -- Status tracking
    status TEXT NOT NULL DEFAULT 'running',  -- 'running', 'schema_changed', 'regenerating', 'completed', 'failed'
    error_message TEXT,

    -- Progress tracking for resumability
    total_memories INT NOT NULL DEFAULT 0,
    memories_processed INT NOT NULL DEFAULT 0,
    memories_successful INT NOT NULL DEFAULT 0,
    memories_failed INT NOT NULL DEFAULT 0,
    last_processed_memory_id UUID,
    failed_memory_ids UUID[] NOT NULL DEFAULT '{}',

    -- Metadata
    requested_by TEXT
);

-- Index for finding in-progress or recent migrations
CREATE INDEX IF NOT EXISTS idx_dimension_migrations_status
    ON embedding_dimension_migrations(status, started_at DESC);

-- Bootstrap: Insert current config based on existing hardcoded defaults
-- This initializes the tracking for existing databases
INSERT INTO embedding_config (model_name, dimensions, is_active)
SELECT 'all-minilm:33m-l12-v2-fp16', 384, true
WHERE NOT EXISTS (SELECT 1 FROM embedding_config WHERE is_active = true);
