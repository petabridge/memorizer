using System.Text.Json;
using Memorizer.Services;

namespace Memorizer.UnitTests;

public class ProviderConfigSanitizerTests
{
    [Fact]
    public void RedactForDisplay_ReplacesApiKeyWithConfiguredFlag()
    {
        using var config = JsonDocument.Parse("""
        {
            "apiUrl": "https://api.openai.com",
            "model": "text-embedding-3-small",
            "apiKey": "sk-secret"
        }
        """);

        var redacted = ProviderConfigSanitizer.RedactForDisplay(config);

        Assert.False(redacted.ContainsKey("apiKey"));
        Assert.True(redacted["apiKeyConfigured"]!.GetValue<bool>());
        Assert.Equal("https://api.openai.com", redacted["apiUrl"]!.GetValue<string>());
        Assert.Equal("text-embedding-3-small", redacted["model"]!.GetValue<string>());
    }

    [Fact]
    public void RedactForDisplay_EmptyApiKeyReportsNotConfigured()
    {
        using var config = JsonDocument.Parse("""
        {
            "apiUrl": "https://api.openai.com",
            "model": "text-embedding-3-small",
            "apiKey": ""
        }
        """);

        var redacted = ProviderConfigSanitizer.RedactForDisplay(config);

        Assert.False(redacted.ContainsKey("apiKey"));
        Assert.False(redacted["apiKeyConfigured"]!.GetValue<bool>());
    }

    [Fact]
    public void CleanForStorage_DoesNotPreserveExistingApiKey()
    {
        var request = new Dictionary<string, object>
        {
            ["apiUrl"] = "https://gateway.local",
            ["model"] = "new-model"
        };

        using var merged = ProviderConfigSanitizer.CleanForStorage(request);

        Assert.Equal("https://gateway.local", merged.RootElement.GetProperty("apiUrl").GetString());
        Assert.Equal("new-model", merged.RootElement.GetProperty("model").GetString());
        Assert.False(merged.RootElement.TryGetProperty("apiKey", out _));
    }

    [Fact]
    public void CleanForStorage_DropsRequestedApiKey()
    {
        var request = new Dictionary<string, object>
        {
            ["apiUrl"] = "https://gateway.local",
            ["model"] = "new-model",
            ["apiKey"] = "sk-new"
        };

        using var merged = ProviderConfigSanitizer.CleanForStorage(request);

        Assert.Equal("https://gateway.local", merged.RootElement.GetProperty("apiUrl").GetString());
        Assert.Equal("new-model", merged.RootElement.GetProperty("model").GetString());
        Assert.False(merged.RootElement.TryGetProperty("apiKey", out _));
    }

    [Fact]
    public void CleanForStorage_DropsConfiguredMarkersFromClientPayload()
    {
        var request = new Dictionary<string, object>
        {
            ["apiUrl"] = "https://gateway.local",
            ["model"] = "new-model",
            ["apiKeyConfigured"] = true
        };

        using var merged = ProviderConfigSanitizer.CleanForStorage(request);

        Assert.False(merged.RootElement.TryGetProperty("apiKeyConfigured", out _));
    }

    [Fact]
    public void CleanForStorage_DropsNestedSensitiveKeys()
    {
        var request = new Dictionary<string, object>
        {
            ["apiUrl"] = "https://gateway.local",
            ["model"] = "new-model",
            ["headers"] = new Dictionary<string, object>
            {
                ["Authorization"] = "Bearer secret",
                ["X-Api-Key"] = "nested-secret",
                ["X-Trace-Id"] = "trace-1"
            }
        };

        using var merged = ProviderConfigSanitizer.CleanForStorage(request);
        var headers = merged.RootElement.GetProperty("headers");

        Assert.False(headers.TryGetProperty("Authorization", out _));
        Assert.False(headers.TryGetProperty("X-Api-Key", out _));
        Assert.Equal("trace-1", headers.GetProperty("X-Trace-Id").GetString());
    }
}
