# ADR: Introduce dedicated `text` column for memories

*Status: Accepted – 2025-05-21*

## Context

Originally the `memories` table stored the user-supplied material only inside a `JSONB content` column.  While JSON is great for rich metadata, it is **not** the format consumed by the vector embedding model and is inconvenient when the data is returned to an LLM.

Pain points discovered:

1. Embedding pipeline had to stringify the JSON and the field names polluted the token stream, degrading similarity quality.
2. Returning data through the MCP server tool produced noisy brace/quote-laden output.
3. Full-text / trigram indexes could not target the actual prose because it was buried inside JSON.

## Decision

We add a **first-class `TEXT text` column** that always stores the plain body (markdown / code / prose) we want to embed and show.

* The old `content` JSON column remains for optional structured metadata – this keeps the API backward compatible.
* `Storage.StoreMemory` now auto-detects whether the caller passed JSON or plain text:
  * If the string parses as JSON → stored in `content`, best-effort extraction populates `text`.
  * Otherwise the raw string is stored in `text` and `{}` is put into `content`.
* All embedding, search and display operations now use `text`.

## Consequences

1. **Database migration** `005_add_text_column.sql` adds the new column and back-fills existing rows.
2. **Model change** `Models/Memory.cs` gains a `Text` property; `Content` is kept for compatibility.
3. **Tool surface** `MemoryTools.Store` now advertises a `text` parameter instead of `content`.
4. All tests have been updated to send plain text. One compatibility test still stores JSON to guarantee the fallback path continues to work.
5. Future features (e.g. summaries, chunking) can rely on a predictable plain-text column without touching JSON. 