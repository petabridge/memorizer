-- GIN index on tags array for efficient tag-based filtering queries
CREATE INDEX IF NOT EXISTS idx_memories_tags_gin ON memories USING GIN (tags);
