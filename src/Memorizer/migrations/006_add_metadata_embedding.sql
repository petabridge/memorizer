-- 006_add_metadata_embedding.sql: Add metadata-only embedding column for PoC

-- Add new column for metadata embeddings (title + tags)
ALTER TABLE memories
    ADD COLUMN IF NOT EXISTS embedding_metadata VECTOR(384);

-- Create index for metadata embedding searches
CREATE INDEX IF NOT EXISTS idx_memories_embedding_metadata_cosine
    ON memories USING ivfflat (embedding_metadata vector_cosine_ops)
    WITH (lists = 100); 