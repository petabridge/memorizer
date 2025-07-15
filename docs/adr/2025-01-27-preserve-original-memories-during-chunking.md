# ADR: Preserve Original Memories During Chunking

*Status: Accepted – 2025-01-27*

## Context

The current chunking implementation has a critical design flaw: it **destroys the original memory content** by replacing it with an LLM-generated summary. This violates the principle of data preservation and creates potential information loss.

### Current Problematic Flow

1. User stores large memory content
2. Chunking system analyzes content and generates summary + chunks  
3. **Original memory content is replaced with summary** (data loss!)
4. Container and chunk memories are created with complex relationships
5. Original detailed content is permanently lost

### Problems with Current Approach

1. **Data Loss**: Original complete content is destroyed and cannot be recovered
2. **Violates ADR Principles**: The plain-text memory column ADR established that chunking should "rely on" the text column, not modify it
3. **User Trust**: Users lose confidence when their original content disappears
4. **Debugging Difficulty**: Cannot compare chunks back to original source
5. **Relationship Complexity**: Creates confusing "container" memories that serve no clear purpose

## Decision

**Preserve original memories completely unchanged during chunking.** Instead of modifying the original memory:

1. **Keep Original Memory Intact**: Never modify the original memory's content, type, or core properties
2. **Create Summary Memory (Optional)**: If LLM generates a useful summary, store it as a separate memory with `type: "{original-type}-summary"`
3. **Create Chunk Memories**: Store each chunk as separate memories with `type: "{original-type}-chunk"`
4. **Use Clear Relationships**: Establish bidirectional relationships for discoverability

### New Relationship Structure

```
Original Memory (unchanged)
├── summarizes ← Summary Memory (optional)
├── chunk-of ← Chunk Memory 1  
├── chunk-of ← Chunk Memory 2
└── chunk-of ← Chunk Memory N

Summary Memory
└── summarizes → Original Memory

Each Chunk Memory  
├── chunk-of → Original Memory
└── part-of-sequence → Next/Previous Chunks (optional)
```

### Implementation Changes

1. **Remove `UpdateMemory` call** from chunking process
2. **Add optional summary creation** with proper relationship
3. **Simplify relationship structure** - no "container" memory needed
4. **Update chunk type naming** to be more descriptive
5. **Preserve all original metadata** (tags, confidence, etc.)

## Consequences

### Positive
1. **Zero Data Loss**: Original content always preserved and accessible
2. **Clear Semantics**: Relationships clearly indicate purpose (summarizes, chunk-of)
3. **Better Debugging**: Can always trace chunks back to original
4. **User Trust**: Users' content is never modified unexpectedly
5. **Simpler Architecture**: No confusing "container" memories

### Considerations
1. **More Storage**: Keeps original + summary + chunks (acceptable trade-off)
2. **Relationship Updates**: Need to update relationship queries in UI
3. **Migration**: Existing chunked memories may need data recovery

### Breaking Changes
1. `ChunkingCompleted` event no longer includes `ContainerMemoryId`
2. Chunked memories will have different relationship patterns
3. Tests expecting content replacement will need updates

## Implementation Plan

1. Update `ChunkingQueue.PerformChunking()` to preserve original
2. Add optional summary memory creation
3. Update relationship creation logic
4. Update tests to expect preservation behavior
5. Update UI to handle new relationship patterns
6. Consider migration strategy for existing chunked memories 