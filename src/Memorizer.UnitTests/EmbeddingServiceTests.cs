using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Memorizer.UnitTests;

/// <summary>
/// Verifies that <see cref="EmbeddingService"/> fails loudly when the underlying
/// embedding API client fails, rather than silently substituting a random vector
/// (which would corrupt the persisted embedding/embedding_metadata columns).
/// </summary>
public class EmbeddingServiceTests
{
    [Fact]
    public async Task Generate_WhenApiClientThrows_ThrowsInsteadOfReturningRandomVector()
    {
        var underlying = new HttpRequestException("simulated Ollama 503");
        var service = CreateService(new ThrowingEmbeddingApiClient(underlying));

        var thrown = await Assert.ThrowsAsync<EmbeddingGenerationException>(
            () => service.Generate("some text that should not be persisted with a garbage embedding"));

        // The original failure must be preserved as the inner exception so callers can diagnose it.
        Assert.Same(underlying, thrown.InnerException);
        // The message carries the model name for diagnostics.
        Assert.Contains("test-model", thrown.Message);
    }

    [Fact]
    public async Task Generate_WhenApiClientThrows_DoesNotLeakInputTextInException()
    {
        const string secret = "SUPER-SECRET-INPUT-THAT-MUST-NOT-BE-LOGGED-OR-THROWN";
        var service = CreateService(new ThrowingEmbeddingApiClient(new InvalidOperationException("boom")));

        var thrown = await Assert.ThrowsAsync<EmbeddingGenerationException>(
            () => service.Generate(secret));

        Assert.DoesNotContain(secret, thrown.Message);
        // Length is safe to include; the raw text is not.
        Assert.Contains(secret.Length.ToString(), thrown.Message);
    }

    [Fact]
    public async Task Generate_WhenCallerCancels_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Simulate the api client honoring the cancellation token.
        var service = CreateService(new ThrowingEmbeddingApiClient(
            new OperationCanceledException(cts.Token)));

        // Cancellation must propagate as-is, NOT be wrapped as an embedding failure.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.Generate("text", cts.Token));
    }

    [Fact]
    public async Task Generate_WhenTimeoutButTokenNotCancelled_FailsLoudly()
    {
        // HttpClient timeouts surface as TaskCanceledException (an OperationCanceledException)
        // even though the caller's token was never cancelled. This must still fail loudly.
        var service = CreateService(new ThrowingEmbeddingApiClient(
            new TaskCanceledException("The request timed out")));

        await Assert.ThrowsAsync<EmbeddingGenerationException>(
            () => service.Generate("text", CancellationToken.None));
    }

    private static EmbeddingService CreateService(IEmbeddingApiClient apiClient)
    {
        var settings = new EmbeddingSettings
        {
            Provider = ProviderNames.Ollama,
            ApiUrl = new Uri("http://embed.local"),
            Model = "test-model"
        };

        return new EmbeddingService(
            apiClient,
            new TestOptionsSnapshot<EmbeddingSettings>(settings),
            NullLogger<EmbeddingService>.Instance);
    }

    private sealed class ThrowingEmbeddingApiClient : IEmbeddingApiClient
    {
        private readonly Exception _toThrow;

        public ThrowingEmbeddingApiClient(Exception toThrow)
        {
            _toThrow = toThrow;
        }

        public Task<float[]> GenerateAsync(string model, string text, CancellationToken cancellationToken = default)
            => Task.FromException<float[]>(_toThrow);
    }

    private sealed class TestOptionsSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public TestOptionsSnapshot(T value) { Value = value; }
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
