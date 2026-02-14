# Architecture Decision Record: Hybrid Search with PostgreSQL Full-Text Search and Reciprocal Rank Fusion

**Date:** 2026-02-14
**Status:** Accepted
**Supersedes:** [Memory Search Result Ranking and Tag Handling](2025-05-23-memory-search-ranking.md) (partially — tag soft-boost behavior is preserved within the new method)

## Context

The search quality evaluation framework (issue #132) quantified a persistent problem: short keyword queries like "race condition", "phobos", and "dependency injection" return **zero results** at the default 0.7 similarity threshold.

Root cause analysis showed:

- **Cosine similarity compression**: With 1024-dimensional embeddings and 2400+ memories, cosine similarity scores compress into a narrow band (0.52-0.62 for most results). A 0.7 threshold eliminates most genuinely relevant content.
- **Keyword dilution in embeddings**: Even with metadata-only embeddings (title + tags), short keyword queries produce embedding vectors that are too diffuse to reliably surface exact matches. "race condition" as a 2-word embedding is compared against title embeddings of varying length and semantic density.

Eval results at threshold 0.7 (production dataset, 2335 memories):

| Query | SearchWithMetadataEmbedding | HybridSearch |
|-------|---------------------------|--------------|
| "race condition" | 0 results | 5 relevant results |
| "phobos" | 0 results | 5 relevant results |
| "dependency injection" | 0 results | 5 relevant results |
| "flaky test" | 1 result | 5 relevant results |
| "docker compose" | 5 results | 5 results (different ranking) |
| "blazor components" | 5 results | 5 results (comparable) |

Synthetic corpus evaluation (45 entries, 30 queries, all thresholds):

| Method | MRR | Recall@5 | Hit@3 | NDCG@5 |
|--------|-----|----------|-------|--------|
| Search (full embedding) | 0.069 | 0.206 | 0.067 | 0.047 |
| SearchWithMetadataEmbedding | 0.481 | 0.494 | 0.533 | 0.371 |
| **HybridSearch** | **0.557** | **0.622** | **0.600** | **0.440** |

## Decision

Replace `SearchWithMetadataEmbedding` as the default search method in the MCP `SearchMemories` tool with a new `HybridSearch` method that combines:

1. **Vector search** (metadata embedding) — no hard distance threshold
2. **PostgreSQL full-text search** (tsvector with weighted fields + GIN index)
3. **Reciprocal Rank Fusion (RRF)** to merge results from both legs

### Search Architecture

**Leg 1 — Vector search**: Uses existing `embedding_metadata <=> @embedding` cosine distance, ordered by distance, no threshold filter. Retrieves `max(limit * 3, 30)` candidates.

**Leg 2 — Full-text search**: Uses `to_tsquery` with AND-prefix matching (e.g., `postgres:* & configuration:*`) and `ts_rank_cd` (cover density ranking). AND-prefix was chosen over `websearch_to_tsquery` because PostgreSQL's English stemmer creates mismatches between common variants (e.g., "postgres" stems to `postgr` but "postgresql" stems to `postgresql`). Prefix matching with `:*` resolves this while AND semantics prevent result explosion. The `search_vector` tsvector column is maintained by a trigger with weighted fields:
- **Weight A** (title): Strongest signal — title matches are definitive
- **Weight B** (tags): Secondary — categorical markers (hyphens replaced with spaces for tokenization)
- **Weight C** (text): Lowest — prevents body text from drowning out title/tag matches

**RRF Fusion** (k=60): `RRF_score(doc) = sum(weight / (k + rank))`
- Adaptive weighting: queries with 1-2 words get FTS weight=1.5, vector weight=1.0. Longer queries get equal 1.0/1.0 weights.
- 10% tag boost applied to final RRF scores (preserving the soft-boost behavior from the previous ADR).

### Similarity Threshold

The `minSimilarity` parameter is **accepted but not applied** in `HybridSearch`. This is intentional:

- The hard distance threshold was the root cause of zero-result failures
- RRF ranking naturally handles relevance ordering — low-quality results sort to the bottom
- The `limit` parameter controls result count
- FTS-only results (no vector match) would be incorrectly filtered by a vector-based threshold
- The parameter is retained for API compatibility with MCP tools and web UI

### Scope

This change affects:
- **MCP `SearchMemories` tool** — switched to `HybridSearch`
- **Project search** (`SearchProjectsAsync`) — refactored to use `HybridSearchSystemMemories` shared helper
- **Workspace search** (`SearchWorkspacesAsync`) — refactored to use `HybridSearchSystemMemories` shared helper
- **Search evaluation framework** — `HybridSearch` added as an eval method

This change does **not** affect:
- **Similar memory discovery** (`GetSimilarMemories`) — uses direct embedding comparison
- **Web UI search** — unless it calls the same `SearchMemories` MCP tool

### Database Migration

Migration `020_add_full_text_search.sql` adds:
- `search_vector tsvector` column on `memories` table
- Trigger function `memories_search_vector_update()` to maintain the column on INSERT/UPDATE
- GIN index `idx_memories_search_vector`
- Backfill of existing rows

A generated column (`GENERATED ALWAYS AS`) was not possible because `to_tsvector('english', ...)` is `STABLE`, not `IMMUTABLE`. The trigger approach is the standard PostgreSQL pattern.

## Consequences

### Positive

- Short keyword queries that previously returned zero results now return relevant content
- MRR improved 16%, Recall@5 improved 26%, NDCG@5 improved 19% over previous best method
- No new dependencies — uses built-in PostgreSQL FTS via existing Npgsql
- Backward compatible — existing search methods remain available
- Zero application code needed for tsvector maintenance (trigger handles it)

### Negative

- The `minSimilarity` threshold parameter becomes a no-op for the primary search path. UI elements and MCP tool descriptions that reference it may confuse users.
- Two SQL queries per search instead of one (vector + FTS). Mitigated by `LIMIT` on both legs and GIN index on FTS.
- FTS-only results don't have a vector similarity score displayed (shown as `n/a`)
- The trigger adds marginal overhead to INSERT/UPDATE on the memories table

### Future Considerations

- Consider removing or repurposing the `minSimilarity` parameter in the UI and MCP tool descriptions
- Consider exposing RRF weights as configuration for tuning
- Monitor FTS index size and query performance as the corpus grows
- Consider adding `simple` dictionary alongside `english` for non-English content
