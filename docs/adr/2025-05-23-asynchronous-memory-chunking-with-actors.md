# ADR-001: Asynchronous Memory Chunking with Akka.NET Actors

## Status
Accepted

## Context

PostgMem faced a vector embedding granularity problem where large documents had "averaged" embeddings that made specific details hard to find via semantic search. For example, searching for "C# Coding Standards" wouldn't find a large coding standards document unless similarity threshold was set very low (10%).

### The Problem
- Large text documents stored as single memories lose semantic granularity in embeddings
- Vector embeddings for large texts average semantic meaning across entire document
- Targeted searches fail to find relevant sections within large documents
- Users need fast HTTP/MCP responses regardless of content size
- Chunking analysis using LLM calls can take 2-30 seconds

### Requirements
- Break large content into semantically meaningful chunks
- Preserve fast response times for storage operations
- Maintain relationship links between original content and chunks
- Handle failures gracefully without blocking user operations
- Support background processing with observability

## Decision

We decided to implement **asynchronous memory chunking using Akka.NET actors** with the following architecture:

### Components
1. **ChunkingQueue** - Queues chunking work for background processing
2. **ChunkingActor** - Processes individual chunking jobs asynchronously
3. **LlmService** - Analyzes content for semantic boundaries using LLM
4. **Relationship System** - Links chunks to original memories

### Flow
```
Store Request → Immediate Storage → Queue for Chunking → Fast Response
                                         ↓
Background: LLM Analysis → Create Chunks → Link Relationships
```

### Key Design Decisions
- **Immediate Storage**: Memory stored immediately for fast response (<100ms)
- **Background Queueing**: Large content (>2000 characters) queued for analysis
- **Actor Processing**: Akka.NET provides supervision, fault tolerance, and isolation
- **Graceful Degradation**: Original memory preserved if chunking fails

## Alternatives Considered

### 1. Synchronous Chunking
- **Pros**: Simpler implementation, immediate consistency
- **Cons**: Slow response times (2-30 seconds), poor user experience, blocking operations

### 2. Background Tasks with HostedService
- **Pros**: Built into .NET, familiar pattern
- **Cons**: Less fault tolerance, no supervision trees, harder to scale

### 3. Message Queue (Redis/RabbitMQ)
- **Pros**: External persistence, horizontal scaling
- **Cons**: Additional infrastructure, increased complexity, overkill for current needs

### 4. Simple ThreadPool Tasks
- **Pros**: Minimal dependencies
- **Cons**: No supervision, poor error handling, harder to monitor

## Consequences

### Positive
- **Fast User Experience**: HTTP/MCP calls return in <100ms regardless of content size
- **Better Search Quality**: Focused embeddings improve search precision and recall
- **Fault Tolerance**: Actor supervision handles failures without crashing the system
- **Scalability**: Actor model naturally supports concurrent processing
- **Observability**: Rich logging and event stream for monitoring
- **Non-Blocking**: Large content processing doesn't impact system responsiveness

### Negative
- **Eventual Consistency**: Chunks not immediately available after storage
- **Increased Complexity**: Actor system adds architectural complexity
- **Resource Usage**: 2-5x storage overhead (original + container + chunks)
- **Dependencies**: Requires Akka.NET framework
- **Debugging**: Asynchronous nature makes debugging more challenging

### Mitigations
- **Status Indicators**: Clear messaging when content is queued for chunking
- **Fallback Behavior**: Original memory always accessible even if chunking fails
- **Monitoring**: Comprehensive logging and health checks for background processing
- **Documentation**: Clear guidance on async behavior and expected timings

## Implementation Notes

### Configuration
```json
{
  "Chunking": {
    "ApiUrl": "http://localhost:11434",
    "Model": "llama3", 
    "Enabled": true,
    "MinCharactersForChunking": 2000,
    "TargetChunkSize": 1500,
    "Timeout": "00:02:00"
  }
}
```

### Actor System Integration
- Uses Akka.Hosting for .NET dependency injection integration
- Each chunking job gets isolated actor instance
- Service scoping ensures fresh DI container per job
- Event stream publishes completion/failure events

### Relationship Types
- `"replaced-by"`: Original memory → Container memory
- `"chunk-of"`: Chunk memory → Container memory
- Container type: `{original-type}-container`  
- Chunk type: `{original-type}-chunk`

## References
- [CHUNKING.md](./CHUNKING.md) - Complete implementation documentation
- [Akka.NET Documentation](https://getakka.net/)
- [Vector Embeddings and Chunking Strategies](https://blog.langchain.dev/chunking-strategies-for-llm-applications/) 