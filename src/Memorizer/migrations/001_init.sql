-- 001_init.sql: Initial schema

-- Track applied migrations
CREATE TABLE IF NOT EXISTS schema_version (
    version INT PRIMARY KEY,
    name TEXT NOT NULL,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Required extension
CREATE EXTENSION IF NOT EXISTS vector;

-- Main table
CREATE TABLE IF NOT EXISTS memories (
    id UUID PRIMARY KEY,
    type TEXT NOT NULL,
    content JSONB NOT NULL,
    source TEXT NOT NULL,
    embedding VECTOR(384) NOT NULL,
    tags TEXT[] NOT NULL,
    confidence DOUBLE PRECISION NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
); 