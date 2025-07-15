# ADR: Integration Test Design for Asynchronous Chunking System

*Status: Accepted – 2025-05-23*

## Context

The asynchronous memory chunking system introduced complex testing challenges due to its background processing nature. Unlike synchronous operations that complete within the test execution, chunking involves:

1. **Asynchronous Processing**: Chunking happens in background actors after the initial store operation returns
2. **External Dependencies**: Requires Ollama LLM service and PostgreSQL with pgvector extension  
3. **Actor System**: Akka.NET actors with supervision trees and message passing
4. **Multiple Components**: ChunkingQueue, ChunkingActor, LlmService, Storage, and relationship creation

Pain points in testing async chunking:

1. **Timing Issues**: No guarantee when chunking will complete
2. **End-to-End Verification**: Need to verify the entire pipeline from queueing to chunk creation
3. **Infrastructure Setup**: Complex TestContainer setup with multiple services
4. **Race Conditions**: Tests could pass/fail based on timing rather than correctness

## Decision

We designed a comprehensive integration test strategy with three layers of verification:

### Test Infrastructure
- **TestContainers**: PostgreSQL with pgvector and Ollama containers for isolated testing
- **TestKit Integration**: Akka.Hosting.TestKit for actor system testing with proper DI
- **Model Management**: Automatic pulling of lightweight models (`all-minilm`, `qwen2:0.5b`) for faster tests

### Test Categories

#### 1. Unit-Level Component Tests
```csharp
[Fact]
public void ChunkingQueue_Should_Queue_Large_Content_For_Background_Processing()
[Fact] 
public void ChunkingQueue_Should_Not_Queue_Small_Content()
```
- **Purpose**: Verify queueing logic and thresholds
- **Scope**: Isolated component behavior
- **Speed**: Fast (<1 second)

#### 2. Integration Tests with Fast Response Verification
```csharp
[Fact]
public async Task MemoryTools_Store_Should_Return_Fast_For_Large_Content()
```
- **Purpose**: Verify user-facing performance guarantees
- **Assertion**: Response time <5 seconds regardless of content size
- **Verification**: Confirms queueing message in response

#### 3. End-to-End Async Processing Tests
```csharp
[Fact]
public async Task Background_Chunking_Should_Eventually_Create_Chunks()
```
- **Purpose**: Verify complete chunking pipeline
- **Strategy**: Polling with timeout (30 seconds)
- **Verification**: Check for relationships and chunk-related memories

### Key Design Patterns

#### Polling with Timeout
```csharp
var maxWaitTime = TimeSpan.FromSeconds(30);
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
var chunkingCompleted = false;

while (stopwatch.Elapsed < maxWaitTime && !chunkingCompleted)
{
    await Task.Delay(1000); // Poll every second
    // Check for chunking completion indicators
}
```

#### Relationship-Based Verification
- Check for `"replaced-by"` relationships from original memory
- Verify chunk memories have appropriate types (`*-container`, `*-chunk`)
- Confirm tags include chunking indicators (`"chunked-container"`, `"chunk"`)

#### TestContainer Configuration
```csharp
PostgreSqlContainer = new PostgreSqlBuilder()
    .WithImage("pgvector/pgvector:pg17")
    .WithDatabase("postgmem")
    
OllamaContainer = new ContainerBuilder()
    .WithImage("ollama/ollama:latest")
    .WithPortBinding(11434, true)
```

## Consequences

### Positive
1. **Comprehensive Coverage**: Tests verify both immediate responsiveness and eventual consistency
2. **Isolation**: Each test runs with fresh containers, avoiding state pollution
3. **Realistic Environment**: Tests run against actual PostgreSQL and Ollama services
4. **Deterministic Results**: Polling with timeout avoids flaky timing-dependent tests
5. **Documentation Value**: Tests serve as examples of expected system behavior

### Negative
1. **Test Duration**: Full suite takes ~60 seconds due to container startup and LLM processing
2. **Resource Usage**: Requires Docker and significant memory for containers
3. **Complexity**: More complex than unit tests, harder to debug failures
4. **External Dependencies**: Failures could be due to container issues rather than code bugs

### Test Results
- **26/26 Integration Tests Passing**: Comprehensive verification of chunking pipeline
- **Background Processing Verified**: Confirmed chunking actor successfully processes requests
- **LLM Integration Working**: HTTP communication with Ollama confirmed through logs
- **Relationship System Tested**: Chunk linking and discovery functionality verified

### Monitoring and Debugging
- **Extensive Logging**: Actor operations, HTTP requests, and chunking decisions logged
- **Console Output**: Test results include "Chunking completed successfully" confirmations  
- **Timeout Handling**: Clear error messages when chunking doesn't complete within expected timeframe

The integration test design successfully validates the entire asynchronous chunking pipeline while maintaining test reliability and providing clear feedback on system behavior. 