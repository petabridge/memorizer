# ADR: Memory Similarity Discovery Feature

**Date:** 2025-11-28
**Status:** Accepted
**Context:** Pre-workspace/project taxonomy feature for evaluating embedding quality

## Decision

Implement a "Similar Memories" feature in the memory detail view that:
1. Uses pgvector similarity search to surface related memories on-demand
2. Allows users to optionally persist confirmed similarities as bidirectional `similar-to` relationships with scores
3. Provides configurable threshold (default 70%, adjustable via UI slider)

## Context

- Production Memorizer has ~1,900 memories with 384-dimension embeddings
- This feature serves as a stepping stone before implementing workspace/project taxonomy
- Helps validate embedding model quality after changes (post-1.9.0-beta1)
- Relationships in Memorizer are currently one-directional; similar-to relationships need to be bidirectional for semantic correctness

## Rationale

### Why On-Demand vs Pre-Computed Similarity?

We chose "Light Persistence" (on-demand queries with optional relationship creation) over:

- **Full pre-computation**: Would require background jobs to maintain a large similarity matrix, complexity not justified for evaluation use case
- **Pure ephemeral**: No persistence means users can't build a knowledge graph from discovered similarities

### Why Bidirectional Relationships?

Similarity is symmetric by definition (A similar to B implies B similar to A). Creating relationships in both directions:
- Ensures consistency regardless of which memory is viewed
- Shows "Similar Memories" correctly from either side
- Stores the same score in both relationship records

### Why Configurable Threshold?

- Default threshold (70%) balances precision vs recall for typical content
- Users can adjust per-session to explore different similarity levels
- Configuration in appsettings.json allows deployment-specific tuning

## Technical Approach

### Schema Change

Added `score` column to `memory_relationships` table:
```sql
ALTER TABLE memory_relationships
ADD COLUMN score DOUBLE PRECISION DEFAULT NULL;
```

### Similarity Query

Uses pgvector cosine distance operator:
```sql
SELECT m.id, m.title, m.type,
       1 - (m.embedding <=> @sourceEmbedding) AS similarity
FROM memories m
WHERE m.id != @sourceId
  AND 1 - (m.embedding <=> @sourceEmbedding) >= @minSimilarity
ORDER BY m.embedding <=> @sourceEmbedding
LIMIT @limit;
```

### API Endpoints

- `GET /api/memory/{id}/similar?threshold=0.7&limit=10` - Query similar memories
- `POST /api/memory/{id}/similar` - Create bidirectional relationships
- `GET /api/memory/similarity/settings` - Get configured thresholds for UI

## Consequences

### Benefits

- Users can evaluate embedding quality by exploring similar memories
- Knowledge graph grows through user-confirmed relationships
- Low implementation complexity (no background jobs, actors)
- Configurable for different deployment needs

### Trade-offs

- Similarity is computed on-demand (minor latency on view)
- No automatic relationship creation (requires user action)
- Score stored redundantly in bidirectional relationships

### Deferred Decisions

The following were explicitly deferred for post-workspace/project implementation:
- DBSCAN cluster discovery
- LLM-generated cluster names
- Background pre-computation
- MCP tool exposure for similarity queries
- Automatic relationship creation

## Files Changed

| File | Change Type |
|------|-------------|
| `src/Memorizer/appsettings.json` | MODIFY |
| `src/Memorizer/Settings/SimilaritySettings.cs` | NEW |
| `src/Memorizer/migrations/010_add_relationship_score.sql` | NEW |
| `src/Memorizer/Models/MemoryRelationship.cs` | MODIFY |
| `src/Memorizer/Models/SimilarMemory.cs` | NEW |
| `src/Memorizer/Services/Memory.cs` | MODIFY |
| `src/Memorizer/Controllers/MemoryController.cs` | MODIFY |
| `src/Memorizer/Views/Home/View.cshtml` | MODIFY |
| `src/Memorizer/wwwroot/css/site.css` | MODIFY |

## Related

- [Memorizer Evolution Master Plan](memorizer://memory/0dc98f5b-7f2c-44b9-940c-a16e97763de8)
- [Implementation Plan](memorizer://memory/e3fd98d0-c2a3-4bf0-b5c4-b534a8cb8b47)
