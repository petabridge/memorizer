# ADR: Fix Infinite Recursion Connection Leak in Memory Relationship Loading

*Status: Accepted – 2025-05-23*

## Context

The PostgMem system experienced critical connection pool exhaustion under load, manifesting as PostgreSQL "too many clients already" errors. Investigation revealed an infinite recursion bug in the Memory service's relationship loading logic:

**The Problem:**
1. `GetMany()` loads memories and calls `GetRelationshipsForMany()` to populate relationships
2. `GetRelationshipsForMany()` calls `GetMany()` again to populate `RelatedMemoryTitle` and `RelatedMemoryType` 
3. This creates infinite recursion: `GetMany() → GetRelationshipsForMany() → GetMany() → ...`
4. Each recursive call opens a new database connection using `await _dataSource.OpenConnectionAsync()`
5. The infinite recursion prevents proper connection disposal, rapidly exhausting the connection pool
6. Similar issue exists in `GetRelationships()` method

**Root Causes:**
- N+1 query pattern disguised as relationship loading optimization
- Circular dependency between entity loading and relationship loading
- Over-fetching full Memory objects when only title/type needed
- No recursion depth control or cycle detection

## Decision

**Replace recursive N+1 pattern with efficient JOIN queries:**

### Before (Infinite Recursion):
```csharp
// GetRelationshipsForMany() - BROKEN
var relationshipRecords = await GetRelationshipRecords(memoryIds);
var relatedMemories = await GetMany(toMemoryIds); // ← Calls GetRelationshipsForMany() again!
// Populate titles from full Memory objects
```

### After (Single JOIN Query):
```csharp
// GetRelationshipsForMany() - FIXED  
const string sql = @"
    SELECT r.id, r.from_memory_id, r.to_memory_id, r.type, r.created_at,
           m.title as related_title, m.type as related_type
    FROM memory_relationships r
    LEFT JOIN memories m ON r.to_memory_id = m.id
    WHERE r.from_memory_id = ANY(@ids)";
// Single query, no recursion, gets all needed data
```

**Key Changes:**
1. **Eliminate Recursion:** `GetRelationshipsForMany()` no longer calls `GetMany()`
2. **JOIN Query:** Use LEFT JOIN to fetch titles/types in one query
3. **Minimal Data Fetching:** Only fetch title/type, not full Memory objects
4. **Connection Efficiency:** One connection per method call, not per recursion level

## Consequences

### Positive:
- ✅ **Eliminates infinite recursion** - no more circular method calls
- ✅ **Fixes connection leaks** - each method opens exactly one connection  
- ✅ **Improves performance** - single JOIN query vs N+1 recursive queries
- ✅ **Predictable behavior** - no unbounded recursion or memory growth
- ✅ **Simpler code** - removes complex depth-limiting workarounds

### Neutral:
- Relationships now load only basic title/type info (which was the intended use case)
- API responses unchanged - same data structure returned to consumers

### Monitoring:
- Connection pool metrics should show stable connection usage
- Query performance should improve due to elimination of N+1 pattern
- Memory usage should be more predictable without recursive stack growth

## Alternatives Considered

1. **Recursion Depth Limiting** - Rejected as "lipstick on a pig" - still has N+1 queries, connection waste, and unpredictable cutoffs
2. **Lazy Loading** - Would break existing API contracts and require significant refactoring
3. **Explicit Loading Strategy Enum** - Adds complexity without addressing root architectural issue
4. **Caching** - Would mask the problem without fixing the underlying inefficiency

## Implementation Notes

- Updated both `GetRelationshipsForMany()` and `GetRelationships()` methods
- Maintains backward compatibility with existing API responses  
- All integration tests pass (except unrelated chunking timing test)
- No database schema changes required

---

**Related:** Memory relationship type ADR, chunking integration test design ADR. 