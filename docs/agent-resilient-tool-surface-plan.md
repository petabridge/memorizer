# Fix-up plan: agent-resilient MCP tool surface

Status: proposed
Owner: TBD
Related: #166, #184, #211, #215

## Why this exists

After the 2.3.0 deploy, agents began getting opaque `An error occurred
invoking '<tool>'` failures from `store` and `get`. We traced it end to end
(netclaw session logs → memorizer OTel logs in Seq).

Two **separate** problem classes surfaced. Only the first is what agents hit in
the incident; the second is a real, distinct risk that this plan must not
pretend to cover with the same fix.

### Class A — argument-binding failures (the incident)

Server-side exceptions in Seq (`service = memorizer`, 2026-08-20 ~18:31 UTC):

```
"store" threw an unhandled exception.
System.ArgumentException: The arguments dictionary is missing a value for the
required parameter 'type'.
  at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunctionDescriptor…GetParameterMarshaller
```

Root cause: **#211 upgraded the MCP SDK** (`ModelContextProtocol`
`0.4.0-preview.3 → 2.2.0`). The stable SDK marshals tool arguments through
`AIFunctionFactory` and **validates before the tool method runs** — a missing
required parameter (`type`) or a malformed typed parameter (an unhyphenated
32-hex string for a `Guid id`) throws at the binder and surfaces as the generic
error. The tool code did not change between 2.2.0 and 2.3.0 (byte-identical
`MemoryTools.cs`); only the SDK moved.

This defeated our own resilience work:

- **#166** changed optional `Guid?` params to `string?` + `Guid.TryParse`
  because "some MCP clients send empty strings or the literal 'null'… causing
  System.Text.Json to throw before the tool method is ever reached." The
  **required** `id` was left as `Guid` — the gap now biting.
- **#184** added in-body `if (string.IsNullOrWhiteSpace(type)) return "…"`
  checks. These only run if the method is called. The preview SDK delivered
  loose/missing args to the method; 2.2.0 rejects them at the binder first, so
  the checks are now dead code.

Timeline (Seq): first binder exception 2026-08-18, none before — i.e. it
started with the deploy.

Confirmed failing inputs from the incident:
- `store` × 2: missing `type` (model dropped it on retry).
- `get` × 1: `dec906c7e5d14abebaf827af5a842ae5` — a netclaw recall id
  (`doc-…` prefix stripped), N-format, which `Guid` binding rejects.

### Class B — embedding-layer store failures (separate)

**#215** removed the silent random-embedding fallback. Embedding-backend
failures now throw `EmbeddingGenerationException` from `StoreMemory`, **after**
argument validation, aborting the write. This is correct (it stopped persisting
garbage vectors) but it means a healthy, well-formed `store` call can still fail
at the embedding step. **The Class A boundary does nothing for this.** It needs
its own handling (Piece 4).

## Design principles

1. **Own the error surface.** Never let the SDK author the message. Every
   rejection returns a memorizer-written, *correctable* message: what was wrong,
   why, the expected shape, and a minimal valid example.
2. **Pit of success.** The natural, low-effort call an agent makes should be the
   one that works. Fail correctable, never opaque. Minimize required fields.
   Be liberal in what you accept (Postel's law).
3. **Require load-bearing fields; default organizational ones.** Don't default a
   field the core function depends on — that silently degrades quality (the same
   mistake the random-embedding fallback made). Do default metadata that has a
   safe fallback.

## Field policy (the require-vs-default decision)

`store` should succeed with `text` alone. The rest default, **except `title`.**

| Field | Role | Vectorized? | Decision |
|---|---|---|---|
| `text` | the memory body | yes (content embedding) | **required** |
| `title` | the search index — dominates the metadata embedding (`embedding_metadata = title + tags`) and is prepended to the content embedding | **yes** | **required**, with a teaching error that explains it is what search indexes |
| `type` | free-form category / `GetByFilter` facet; written to `type_legacy`; not validated against the enum | no | **default** `reference` (the `MemoryTypeEnum` fallback already is `reference`) |
| `source` | provenance label | no | **default** `LLM` |
| `confidence` | score | no | default `1.0` (already) |
| `archetype` | `document` (mutable) vs `record` (immutable) | no | default `document` (already) |

Rationale for keeping `title` required: `metadataText = title` (+ tags) is the
vector that metadata search and similarity rank on
(`embedding_metadata <=> @embedding`). Deriving a title from the first line of
`text` (e.g. a bash script or JSON blob) would produce a weak/garbage search
vector and make the memory unfindable — the same silent-quality-loss failure
mode we just removed in #215. Requiring it with a good error guides the agent to
write a real, searchable title, which is the correct path *and* the easy one.

Note: `type` — the exact field the agent dropped in the incident — is one we can
safely default. With a default, that specific `store` failure does not occur.

## The plan

### Piece 1 — CallTool validation boundary (own the error)

Register one filter via the 2.2.0 SDK's real seam:
`IMcpServerBuilder.WithRequestFilters(f => f.AddCallToolFilter(next => …))`.

Filter logic (runs for every tool, no per-tool wiring):

1. Read `ctx.Params.Arguments` (`IDictionary<string, JsonElement>`) and the
   tool's own schema from `ctx.MatchedPrimitive` (`McpServerTool.ProtocolTool.InputSchema`).
2. Validate against the schema: required present, each present value's JSON kind
   matches the declared type, formats (uuid/enum) parse. This reuses the schema
   the SDK already emits for `ListTools`, so validation and contract can't drift.
3. On failure: return `CallToolResult { IsError = true, Content =
   [TextContentBlock { Text = correctable message }] }` **without calling
   `next`** — the SDK marshaller never runs, so it never authors the error.
4. On success: `await next(ctx, ct)`, wrapped in a backstop `try/catch` that
   converts any later exception into an owned, logged message. Nothing opaque
   escapes, ever.

Message contract lives in one helper so every tool speaks the same format:
`store: required field 'type' is missing. Provide a string like "document" or
"how-to". Minimal call: {"text":"…","type":"document"}`.

### Piece 2 — Fall-into-success tool ergonomics

- **Defaults** per the field-policy table. Make defaulted params
  nullable-with-default so binding always succeeds and the body fills gaps.
- **`title` stays required** — enforced by the boundary with the teaching error.
- **ID normalization.** Add `MemoryId.TryParseLoose(string)`: trim, strip known
  prefixes (`doc-`, `memory-`, a pasted `/view/{id}` URL), accept N- and
  D-format via `Guid.TryParse`. Change `get`, `get_many`, `delete`, `edit`,
  `revert_to_version`, `archive`, `restore` from `Guid id` to `string id` using
  it. This extends the #166 pattern (already applied to optional ids) to the
  required id.
- **Postel.** Accept a bare string where `string[]` is declared (tags, ids),
  comma/space-delimited lists, case-insensitive `type`/`archetype`.

### Piece 3 — Agent-shaped regression tests (the guard)

Extend `McpServerTests` (boots the real app via `WebApplicationFactory<Program>`
against the Testcontainers Postgres + Ollama, drives the real MCP client over
Streamable HTTP). Assert graceful, correctable results — not exceptions:

- `store` with `type` omitted → `IsError`, message names `type` + example (the
  literal 18:31 repro).
- `store` with only `text` → **succeeds** (defaults fill `type`, `source`, etc.).
- `store` with `title` omitted → `IsError`, message explains title is the search
  index (verifies we did NOT default it).
- `get` with N-format id, and with a `doc-`-prefixed id → normalized, returns
  found/not-found, never a binder error.
- `get` with genuine garbage id → correctable "invalid id" message.
- Parameterized sweep: every tool, omit each required param → never an exception.
- **Tripwire:** assert no response text ever equals the SDK's generic
  `"An error occurred invoking …"`. This is the standing guard that fails the
  next SDK bump the moment it changes binding behavior — the thing #211 lacked.
- Verify snapshot of the tool input schemas so drift is a reviewed diff.

### Piece 4 — Embedding-failure handling in the store path (Class B)

Separate track; the boundary does not cover this. Catch
`EmbeddingGenerationException` in the store path and return an actionable,
retryable result instead of an opaque failure. Decide the real policy:

- chunk/truncate over-long text before embedding, and/or
- queue a re-embed and store flagged-degraded, and/or
- surface a clear "embedding backend unavailable, memory not stored" that the
  agent can retry.

Also audit: memories written **before** 2.3.0 may carry random-fallback
embeddings and be unfindable by semantic search — candidate for a re-embed pass.

## Sequencing

1. Piece 1 + Piece 3 together — converts every opaque arg-binding failure into a
   correctable one and locks it with the tripwire. Biggest immediate win, no
   signature changes required beyond the filter.
2. Piece 2 — defaults + id normalization ergonomics.
3. Piece 4 — embedding-failure handling (independent; can proceed in parallel).

All server-side in `petabridge/memorizer`; deploys via the `memory-mcp` repo, no
client change.
