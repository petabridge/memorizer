-- Add score column to memory_relationships for similarity scoring
-- This enables storing similarity scores when creating 'similar-to' relationships

ALTER TABLE memory_relationships
ADD COLUMN IF NOT EXISTS score DOUBLE PRECISION DEFAULT NULL;

-- Index for efficient querying of scored relationships
-- Partial index only covers rows with scores (similarity relationships)
CREATE INDEX IF NOT EXISTS idx_memory_relationships_score
ON memory_relationships(score)
WHERE score IS NOT NULL;

-- Add comment for documentation
COMMENT ON COLUMN memory_relationships.score IS 'Similarity score (0.0 to 1.0) for similar-to relationships. NULL for other relationship types.';
