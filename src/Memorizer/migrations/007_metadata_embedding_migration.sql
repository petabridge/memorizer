-- Migration 007: Metadata embedding migration marker
-- This migration ensures metadata embeddings will be generated for existing memories

-- The migration system will automatically trigger background metadata embedding generation
-- for any memories that don't have metadata embeddings after this migration is applied