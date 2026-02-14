-- 020_add_full_text_search.sql: Add PostgreSQL full-text search support for hybrid search
-- Uses a trigger to maintain a tsvector column with weighted fields (title=A, tags=B, text=C).
-- Cannot use GENERATED ALWAYS AS because to_tsvector('english', ...) is STABLE, not IMMUTABLE.

ALTER TABLE memories ADD COLUMN IF NOT EXISTS search_vector tsvector;

-- Function to compute weighted tsvector from title, tags, and text
CREATE OR REPLACE FUNCTION memories_search_vector_update() RETURNS trigger AS $$
BEGIN
    NEW.search_vector :=
        setweight(to_tsvector('english', coalesce(NEW.title, '')), 'A') ||
        setweight(to_tsvector('english', replace(coalesce(array_to_string(NEW.tags, ' '), ''), '-', ' ')), 'B') ||
        setweight(to_tsvector('english', coalesce(NEW.text, '')), 'C');
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger to auto-update search_vector on INSERT or UPDATE
CREATE TRIGGER memories_search_vector_trigger
    BEFORE INSERT OR UPDATE ON memories
    FOR EACH ROW
    EXECUTE FUNCTION memories_search_vector_update();

-- GIN index for fast full-text search
CREATE INDEX IF NOT EXISTS idx_memories_search_vector
    ON memories USING gin (search_vector);

-- Backfill search_vector for all existing rows
UPDATE memories SET search_vector =
    setweight(to_tsvector('english', coalesce(title, '')), 'A') ||
    setweight(to_tsvector('english', replace(coalesce(array_to_string(tags, ' '), ''), '-', ' ')), 'B') ||
    setweight(to_tsvector('english', coalesce(text, '')), 'C');
