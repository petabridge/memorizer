using System.Net.Http.Headers;
using System.Net.Http.Json;
using Memorizer.Models;
using Memorizer.Settings;
using Microsoft.Extensions.Options;

namespace Memorizer.Services;

/// <summary>
/// Sends embedding requests to the configured provider and normalises the response
/// to a <c>float[]</c>. Supports Ollama's native <c>/api/embeddings</c> shape and the
/// OpenAI-compatible <c>/v1/embeddings</c> shape (also exposed by LiteLLM, vLLM, Azure
/// OpenAI, LocalAI, etc.).
/// </summary>
public interface IEmbeddingApiClient
{
    Task<float[]> GenerateAsync(string model, string text, CancellationToken cancellationToken = default);
}

public sealed class EmbeddingApiClient : IEmbeddingApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsSnapshot<EmbeddingSettings> _settingsSnapshot;
    private readonly ILogger<EmbeddingApiClient> _logger;

    private EmbeddingSettings Settings => _settingsSnapshot.Value;

    public EmbeddingApiClient(
        HttpClient httpClient,
        IOptionsSnapshot<EmbeddingSettings> settingsSnapshot,
        ILogger<EmbeddingApiClient> logger)
    {
        _httpClient = httpClient;
        _settingsSnapshot = settingsSnapshot;
        _logger = logger;

        _httpClient.BaseAddress = Settings.ApiUrl;
        _httpClient.Timeout = Settings.Timeout;
    }

    public async Task<float[]> GenerateAsync(string model, string text, CancellationToken cancellationToken = default)
    {
        var provider = Settings.Provider;

        if (string.Equals(provider, ProviderNames.OpenAI, StringComparison.OrdinalIgnoreCase))
        {
            return await GenerateOpenAIAsync(model, text, cancellationToken);
        }

        return await GenerateOllamaAsync(model, text, cancellationToken);
    }

    private async Task<float[]> GenerateOllamaAsync(string model, string text, CancellationToken cancellationToken)
    {
        var request = new EmbeddingRequest { Model = model, Prompt = text };

        _logger.LogDebug("Sending Ollama embedding request to {ApiUrl}", Settings.ApiUrl);
        var response = await _httpClient.PostAsJsonAsync("api/embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken);
        if (result?.Embedding is null || result.Embedding.Length == 0)
        {
            throw new InvalidOperationException("Empty embedding response from Ollama API");
        }

        return result.Embedding;
    }

    private async Task<float[]> GenerateOpenAIAsync(string model, string text, CancellationToken cancellationToken)
    {
        var request = new OpenAIEmbeddingRequest { Model = model, Input = text };
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/embeddings")
        {
            Content = JsonContent.Create(request)
        };

        if (!string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            httpRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", Settings.ApiKey);
        }

        _logger.LogDebug("Sending OpenAI-compatible embedding request to {ApiUrl}", Settings.ApiUrl);
        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAIEmbeddingResponse>(cancellationToken: cancellationToken);
        var first = result?.Data.FirstOrDefault();
        if (first?.Embedding is null || first.Embedding.Length == 0)
        {
            throw new InvalidOperationException("Empty embedding response from OpenAI-compatible API");
        }

        return first.Embedding;
    }
}
