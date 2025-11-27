# Embedding Dimension Migration

This document explains how Memorizer handles changes to embedding dimensions when you switch to a different embedding model.

## Overview

Memorizer uses [pgvector](https://github.com/pgvector/pgvector) for storing and searching vector embeddings. pgvector uses fixed-dimension `VECTOR(n)` columns, which means:

- All embeddings in a column must have the same number of dimensions
- Changing dimensions requires schema migration (ALTER TABLE)
- Existing embeddings become invalid when dimensions change

## Common Embedding Models and Dimensions

| Model | Dimensions | Notes |
|-------|------------|-------|
| `all-minilm` | 384 | Fast, lightweight, good for general use |
| `nomic-embed-text` | 768 | Higher quality, more compute required |
| `mxbai-embed-large` | 1024 | High quality, large model |
| `bge-base-en-v1.5` | 768 | BGE family, good quality |
| `bge-large-en-v1.5` | 1024 | BGE family, high quality |

## How Auto-Detection Works

Memorizer automatically detects embedding dimensions:

1. **On Startup**: Probes the configured embedding model by generating a test embedding
2. **Stores Config**: Records the detected model name and dimensions in `embedding_config` table
3. **Validates**: Compares detected dimensions against stored config and database schema
4. **Warns**: If mismatch detected, shows warning banner in UI

### Sources of Truth

1. **Detected Dimensions**: Live probe of the embedding API (most accurate)
2. **Stored Config**: Previous model/dimension recorded in `embedding_config` table
3. **Schema Dimensions**: Actual `VECTOR(n)` column size in database

## When Migration is Needed

Migration is required when:

- You change `Embeddings:Model` in configuration to a model with different output dimensions
- The detected dimensions don't match the database schema
- Example: Switching from `all-minilm` (384d) to `nomic-embed-text` (768d)

## Migration Process

The dimension migration tool performs these steps:

1. **Acquire Lock**: Uses PostgreSQL advisory lock to prevent concurrent migrations
2. **Drop Indexes**: Removes vector similarity indexes
3. **ALTER TABLE**: Changes `VECTOR(n)` column to new dimension size
   - Sets all existing embeddings to NULL (search temporarily unavailable)
4. **Recreate Indexes**: Rebuilds vector indexes for new dimensions
5. **Regenerate Embeddings**: Re-embeds all memories with new model
6. **Update Config**: Stores new model/dimension in `embedding_config`
7. **Release Lock**: Allows normal operations to resume

### What Happens to Old Embeddings?

During migration, old embeddings are set to NULL because:

- pgvector doesn't support zero-padding or truncating vectors
- Old dimension vectors are incompatible with new dimensions for similarity search
- Setting to NULL ensures searches don't return invalid results

Memories with NULL embeddings won't appear in vector similarity searches until regeneration completes.

## Using the Migration Tool

### Via UI

1. Navigate to **Tools > Dimension Migration**
2. Review the current dimension status
3. If migration is needed, click **Start Dimension Migration**
4. Monitor progress via the real-time progress bar

### Via API

```bash
# Check dimension status
curl http://localhost:5000/ui/tools/dimension-status

# Start migration
curl -X POST http://localhost:5000/ui/tools/start-dimension-migration

# Check migration status
curl http://localhost:5000/ui/tools/dimension-migration-status
```

## Configuration

Embedding configuration is in `appsettings.json`:

```json
{
  "Embeddings": {
    "ApiUrl": "http://localhost:11434/",
    "Model": "all-minilm",
    "Timeout": "00:00:10"
  }
}
```

**Note**: The `Dimensions` setting has been removed. Dimensions are now auto-detected from the model.

## Resuming Failed Migrations

If a migration is interrupted, Memorizer tracks the migration state in the `embedding_dimension_migrations` table. You can resume from the UI or via API:

```bash
curl -X POST http://localhost:5000/ui/tools/resume-dimension-migration \
  -H "Content-Type: application/json" \
  -d '{"migrationId": "your-migration-id"}'
```

## Monitoring

- **Startup Logs**: Check for "Validating embedding dimensions" messages
- **Warning Banner**: Appears in UI when mismatch detected
- **Migration Table**: Query `embedding_dimension_migrations` for history

## Troubleshooting

### "Embedding API unavailable"

- Ensure Ollama (or your embedding service) is running
- Check `Embeddings:ApiUrl` in configuration
- Verify the model is pulled: `ollama pull your-model-name`

### "Could not acquire migration lock"

- Another migration may be in progress
- Check for stuck locks: `SELECT * FROM pg_locks WHERE locktype = 'advisory'`
- If needed, terminate the blocking connection

### Search not returning results during migration

- This is expected - embeddings are being regenerated
- Wait for migration to complete
- Check progress in the Tools > Dimension Migration page

## Database Schema

The migration system uses these tables:

```sql
-- Tracks current embedding configuration
CREATE TABLE embedding_config (
    id SERIAL PRIMARY KEY,
    model_name TEXT NOT NULL,
    dimensions INT NOT NULL,
    detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_active BOOLEAN NOT NULL DEFAULT true
);

-- Tracks migration history
CREATE TABLE embedding_dimension_migrations (
    migration_id UUID PRIMARY KEY,
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,
    old_model TEXT NOT NULL,
    old_dimensions INT NOT NULL,
    new_model TEXT NOT NULL,
    new_dimensions INT NOT NULL,
    status TEXT NOT NULL,  -- 'running', 'schema_changed', 'regenerating', 'completed', 'failed'
    total_memories INT NOT NULL DEFAULT 0,
    memories_processed INT NOT NULL DEFAULT 0,
    memories_successful INT NOT NULL DEFAULT 0,
    memories_failed INT NOT NULL DEFAULT 0,
    error_message TEXT,
    requested_by TEXT
);
```
