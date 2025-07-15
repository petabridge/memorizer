CREATE TABLE IF NOT EXISTS memory_relationships (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    from_memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    to_memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
); 