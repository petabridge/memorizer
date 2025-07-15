# ADR: Always Return Relationship Type in MemoryRelationship API Responses

*Status: Accepted – 2025-05-22*

## Context

The system supports relationships between memories, modeled by the `MemoryRelationship` class and stored in the `memory_relationships` table. Each relationship has a `type` (e.g., Parent, Reference, Related) that describes the semantic connection. While the backend service layer has always retrieved and populated the `Type` property, it was not guaranteed that this information was consistently exposed in API responses or UI displays.

Recent improvements to the memory search and retrieval APIs now return related memories alongside main results. For the LLM and UI to make full use of these relationships, the `type` of each relationship must be included in all API responses.

## Decision

- The `Type` property of `MemoryRelationship` **must always be included** in all API and UI responses wherever relationships are returned.
- The backend service and data access layers already retrieve this field from the database; the controller, DTO, and serialization layers must ensure it is not omitted.
- Integration and unit tests should assert that the `type` is present in all relationship objects returned by the API.

## Consequences

- Downstream consumers (UI, LLM, tools) can reliably use the relationship type for richer context and logic.
- Any new endpoints or features involving relationships must include the `type` property by default.
- This change is backward compatible, as the field already exists in the model and database.

---

**Related:** Vector search relevance ADR, relationship fetching optimization ADR. 