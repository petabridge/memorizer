using System.Net;
using System.Text;
using System.Text.Json;
using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Memorizer.UnitTests;

public class EmbeddingApiClientTests
{
    [Fact]
    public async Task GenerateAsync_OllamaProvider_PostsToApiEmbeddings_WithModelAndPrompt()
    {
        var handler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            embedding = new float[] { 0.1f, 0.2f, 0.3f }
        }));

        var client = CreateClient(handler, new EmbeddingSettings
        {
            Provider = ProviderNames.Ollama,
            ApiUrl = new Uri("http://embed.local"),
            Model = "all-minilm"
        });

        var result = await client.GenerateAsync("all-minilm", "hello world");

        Assert.Equal([0.1f, 0.2f, 0.3f], result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/api/embeddings", handler.LastRequest!.RequestUri!.AbsolutePath);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("all-minilm", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello world", body.RootElement.GetProperty("prompt").GetString());
        Assert.False(body.RootElement.TryGetProperty("input", out _));
        Assert.Null(handler.LastRequest.Headers.Authorization);
    }

    [Fact]
    public async Task GenerateAsync_OpenAIProvider_PostsToV1Embeddings_WithModelAndInput_AndBearerAuth()
    {
        var handler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { embedding = new float[] { 0.4f, 0.5f, 0.6f }, index = 0 }
            }
        }));

        var client = CreateClient(handler, new EmbeddingSettings
        {
            Provider = ProviderNames.OpenAI,
            ApiUrl = new Uri("https://api.openai.com"),
            Model = "text-embedding-3-small",
            ApiKey = "sk-test-key"
        });

        var result = await client.GenerateAsync("text-embedding-3-small", "hello world");

        Assert.Equal([0.4f, 0.5f, 0.6f], result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/v1/embeddings", handler.LastRequest!.RequestUri!.AbsolutePath);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("text-embedding-3-small", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello world", body.RootElement.GetProperty("input").GetString());
        Assert.False(body.RootElement.TryGetProperty("prompt", out _));

        Assert.NotNull(handler.LastRequest.Headers.Authorization);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test-key", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GenerateAsync_OpenAIProvider_WithoutApiKey_DoesNotSendAuthHeader()
    {
        var handler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            data = new[] { new { embedding = new float[] { 1f }, index = 0 } }
        }));

        var client = CreateClient(handler, new EmbeddingSettings
        {
            Provider = ProviderNames.OpenAI,
            ApiUrl = new Uri("http://localhost:8000"),
            Model = "local-model",
            ApiKey = null
        });

        await client.GenerateAsync("local-model", "probe");

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task GenerateAsync_ProviderMatchIsCaseInsensitive()
    {
        var handler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            data = new[] { new { embedding = new float[] { 1f }, index = 0 } }
        }));

        var client = CreateClient(handler, new EmbeddingSettings
        {
            Provider = "OpenAI",
            ApiUrl = new Uri("https://api.openai.com"),
            Model = "text-embedding-3-small"
        });

        await client.GenerateAsync("text-embedding-3-small", "x");

        Assert.Equal("/v1/embeddings", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GenerateAsync_OllamaProvider_EmptyResponse_Throws()
    {
        var handler = new RecordingHandler(JsonSerializer.Serialize(new { embedding = Array.Empty<float>() }));

        var client = CreateClient(handler, new EmbeddingSettings
        {
            Provider = ProviderNames.Ollama,
            ApiUrl = new Uri("http://embed.local"),
            Model = "m"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync("m", "t"));
    }

    [Fact]
    public async Task GenerateAsync_OpenAIProvider_NoData_Throws()
    {
        var handler = new RecordingHandler(JsonSerializer.Serialize(new { data = Array.Empty<object>() }));

        var client = CreateClient(handler, new EmbeddingSettings
        {
            Provider = ProviderNames.OpenAI,
            ApiUrl = new Uri("https://api.openai.com"),
            Model = "m"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync("m", "t"));
    }

    private static EmbeddingApiClient CreateClient(HttpMessageHandler handler, EmbeddingSettings settings)
    {
        var httpClient = new HttpClient(handler);
        var snapshot = new TestOptionsSnapshot<EmbeddingSettings>(settings);
        return new EmbeddingApiClient(httpClient, snapshot, NullLogger<EmbeddingApiClient>.Instance);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public RecordingHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TestOptionsSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public TestOptionsSnapshot(T value) { Value = value; }
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
