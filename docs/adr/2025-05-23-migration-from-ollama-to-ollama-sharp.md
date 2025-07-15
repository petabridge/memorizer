# ADR: Migration from Ollama package to OllamaSharp

*Status: Accepted – 2025-05-23*

## Context

PostgMem's LLM service originally used the `Ollama` package (tryAGI/Ollama v1.15.0) for intelligent memory chunking. During development, we encountered dependency injection issues and discovered that `OllamaSharp` (awaescher/OllamaSharp v5.1.18) offered better integration with the .NET ecosystem.

Pain points with the original Ollama package:

1. **Dependency Injection Compatibility**: The service registration pattern `AddHttpClient<ILlmService, LlmService>()` expected a constructor accepting `HttpClient`, but our implementation created the client directly from settings.
2. **API Inconsistencies**: The Ollama package had a more complex API surface with less intuitive method naming.
3. **Ecosystem Integration**: OllamaSharp is used by Microsoft Semantic Kernel and .NET Aspire, indicating better long-term support.
4. **Documentation**: OllamaSharp had clearer documentation and examples for .NET integration.

## Decision

We migrated from `Ollama` package to `OllamaSharp` with the following changes:

### Package References
- **Directory.Packages.props**: `Ollama 1.15.0` → `OllamaSharp 5.1.18`
- **PostgMem.csproj**: Updated package reference

### API Changes in LlmService
- **Client Construction**: `new OllamaApiClient(baseUri, timeout)` → `new OllamaApiClient(httpClient)`
- **Request Method**: `Completions.GenerateCompletionAsync()` → `GenerateAsync(GenerateRequest)`
- **Request Object**: Changed to `OllamaSharp.Models.GenerateRequest` with properties
- **Response Handling**: Switched to streaming collection with `StringBuilder`
- **Constructor**: Added `HttpClient` parameter to work with DI registration

### Key API Differences
```csharp
// Before (Ollama)
var client = new OllamaApiClient(settings.ApiUrl, settings.Timeout);
var response = await client.Completions.GenerateCompletionAsync(
    new GenerateCompletionRequest 
    { 
        Model = model, 
        Prompt = prompt,
        Format = ResponseFormat.Json 
    });

// After (OllamaSharp)  
var client = new OllamaApiClient(httpClient) { SelectedModel = model };
var request = new OllamaSharp.Models.GenerateRequest
{
    Model = model,
    Prompt = prompt,
    Stream = true,
    Format = "json"
};
var responseStream = client.GenerateAsync(request, cancellationToken);
```

## Consequences

### Positive
1. **Proper DI Integration**: `HttpClient` parameter allows seamless integration with `AddHttpClient<>()` pattern
2. **Better Ecosystem Support**: Used by Microsoft projects, indicating long-term viability
3. **Cleaner API**: More intuitive method names and request/response patterns
4. **Improved Documentation**: Better examples and community support

### Negative
1. **Migration Effort**: Required changes to service implementation and testing
2. **API Learning Curve**: Team needed to learn new API patterns and naming conventions

### Verification
- **All Integration Tests Passing**: 26/26 tests succeeded after migration
- **HTTP Communication Working**: Confirmed through logs showing successful API requests to Ollama
- **Background Chunking Functional**: Actor system processing chunking requests correctly
- **Dependency Injection Resolved**: LlmService properly instantiated via DI container

The migration successfully resolved the original dependency injection issues while improving the overall API experience and ecosystem alignment. 