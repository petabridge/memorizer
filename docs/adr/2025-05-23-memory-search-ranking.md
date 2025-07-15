# Architecture Decision Record: Memory Search Result Ranking and Tag Handling

**Date:** 2025-05-23
**Status:** Accepted

## Context

The memory search system is used by both LLMs and human users to retrieve relevant memories from a vector database. Previously, tag filtering was used as a hard filter, requiring memories to match all specified tags. This led to frequent cases where relevant results were excluded, especially when tags were inconsistent, missing, or the LLM generated unexpected tag queries. Users and LLMs expect to see relevant results even if tags are imperfect.

## Decision

- Similarity is the primary ranking criterion.
- Tag matches are used as a soft boost, not a hard filter.
- When a search is performed:
    - Retrieve up to 2x the requested number of results (e.g., if the user asks for 10, fetch up to 20).
    - Rank all results by similarity.
    - For each result, if at least one tag matches the filter set, apply a small boost to its score (e.g., `final_score = similarity + tag_boost`).
    - Sort results by this boosted score.
    - Return the top N results.
- If there are not enough results, do not further restrict by tags.
- Tag normalization (lowercasing, trimming) is performed on the backend, but is not relied upon for strict filtering.
- The system always errs on the side of showing something, not nothing.

## Consequences

- Users and LLMs will see relevant results even if tags are missing or inconsistent.
- Tag quality still matters, but is not a gatekeeper for recall.
- The system is robust to LLM-generated tag queries that may not match existing tags exactly.
- Performance is not impacted due to enforced result limits.

## Future Considerations
- Consider exposing tag boosting as a tunable parameter.
- Invest in tag suggestion/autocomplete to improve tag quality at entry.
- Consider hybrid scoring or explainability features in the UI.

---

**This ADR supersedes any previous guidance that used tags as a hard filter in memory search.** 