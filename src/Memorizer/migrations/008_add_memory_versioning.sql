-- Migration 008: Add memory versioning support
-- Adds version tracking, event log, and version snapshots for full audit trail

-- Add version tracking to memories table
ALTER TABLE memories
    ADD COLUMN IF NOT EXISTS current_version INT NOT NULL DEFAULT 1;

-- Event log table (audit trail - what changed)
CREATE TABLE IF NOT EXISTS memory_events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    version_number INT NOT NULL,              -- Which version this event created
    event_type TEXT NOT NULL,                 -- See event types below
    event_data JSONB NOT NULL,                -- Details of what changed
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    changed_by TEXT,                          -- Who made the change

    CONSTRAINT fk_memory_events_memory FOREIGN KEY (memory_id)
        REFERENCES memories(id) ON DELETE CASCADE
);

-- Event types:
-- 'memory_created'       - Initial creation
-- 'content_updated'      - Text/content changed (full replace)
-- 'content_appended'     - Text appended to existing content
-- 'section_updated'      - Specific section replaced
-- 'metadata_updated'     - Title, type, confidence, tags changed
-- 'relationship_added'   - New relationship created
-- 'relationship_removed' - Relationship deleted
-- 'memory_reverted'      - Reverted to previous version

-- Version snapshots table (full state for fast revert)
CREATE TABLE IF NOT EXISTS memory_versions (
    version_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    version_number INT NOT NULL,

    -- Full snapshot of memory content (NO embeddings)
    type TEXT NOT NULL,
    content JSONB NOT NULL,
    text TEXT NOT NULL,
    source TEXT NOT NULL,
    tags TEXT[] NOT NULL DEFAULT '{}',
    confidence DOUBLE PRECISION NOT NULL,
    title TEXT,

    -- Snapshot of relationships at this version
    relationship_ids UUID[] NOT NULL DEFAULT '{}',

    -- Version metadata
    created_at TIMESTAMPTZ NOT NULL,          -- Original memory creation time
    versioned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT unique_memory_version UNIQUE (memory_id, version_number),
    CONSTRAINT fk_memory_versions_memory FOREIGN KEY (memory_id)
        REFERENCES memories(id) ON DELETE CASCADE
);

-- Add version tracking to relationships
ALTER TABLE memory_relationships
    ADD COLUMN IF NOT EXISTS created_in_version INT,
    ADD COLUMN IF NOT EXISTS deleted_in_version INT;  -- NULL = still active

-- Indexes for efficient queries
CREATE INDEX IF NOT EXISTS idx_memory_events_memory_id ON memory_events(memory_id, version_number DESC);
CREATE INDEX IF NOT EXISTS idx_memory_events_timestamp ON memory_events(timestamp);
CREATE INDEX IF NOT EXISTS idx_memory_events_type ON memory_events(event_type);
CREATE INDEX IF NOT EXISTS idx_memory_versions_memory_id ON memory_versions(memory_id, version_number DESC);
CREATE INDEX IF NOT EXISTS idx_memory_relationships_version ON memory_relationships(from_memory_id, created_in_version);
CREATE INDEX IF NOT EXISTS idx_memory_relationships_deleted ON memory_relationships(deleted_in_version) WHERE deleted_in_version IS NULL;

-- Migration for existing data: Create version 1 snapshot for all existing memories
INSERT INTO memory_versions (
    memory_id, version_number, type, content, text, source,
    tags, confidence, title, created_at, versioned_at,
    relationship_ids
)
SELECT
    m.id,
    1,
    m.type,
    m.content,
    m.text,
    m.source,
    COALESCE(m.tags, '{}'),
    m.confidence,
    m.title,
    m.created_at,
    m.created_at,
    COALESCE(
        ARRAY(
            SELECT mr.id
            FROM memory_relationships mr
            WHERE mr.from_memory_id = m.id OR mr.to_memory_id = m.id
        ),
        '{}'
    )
FROM memories m
WHERE NOT EXISTS (
    SELECT 1 FROM memory_versions mv
    WHERE mv.memory_id = m.id AND mv.version_number = 1
);

-- Create initial events for existing memories
INSERT INTO memory_events (
    memory_id, version_number, event_type, event_data, timestamp, changed_by
)
SELECT
    m.id,
    1,
    'memory_created',
    jsonb_build_object(
        'title', m.title,
        'type', m.type,
        'source', m.source,
        'migration', true
    ),
    m.created_at,
    'migration'
FROM memories m
WHERE NOT EXISTS (
    SELECT 1 FROM memory_events me
    WHERE me.memory_id = m.id AND me.version_number = 1
);

-- Update relationships with version tracking
UPDATE memory_relationships
SET created_in_version = 1
WHERE created_in_version IS NULL;
