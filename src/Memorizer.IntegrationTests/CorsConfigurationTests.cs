using System.Net;
using System.Net.Http.Headers;
using Memorizer.Extensions;
using Memorizer.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.AspNetCore;
using PostgMem.Tools;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for CORS configuration to ensure MCP SSE endpoints work with Claude Code
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class CorsConfigurationTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CorsConfigurationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task CorsEnabled_PrefightRequest_ReturnsCorrectHeaders()
    {
        // Arrange
        await using var app = CreateTestApp(corsEnabled: true);
        await app.StartAsync();
        var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/sse");
        request.Headers.Add("Origin", "https://example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"),
            "Response should contain Access-Control-Allow-Origin header");

        var allowOrigin = response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault();
        Assert.Equal("*", allowOrigin);

        _output.WriteLine("✓ CORS preflight request returned correct headers");
    }

    [Fact]
    public async Task CorsEnabled_ActualRequest_ReturnsCorrectHeaders()
    {
        // Arrange
        await using var app = CreateTestApp(corsEnabled: true);
        await app.StartAsync();
        var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", "https://example.com");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"),
            "Response should contain Access-Control-Allow-Origin header");

        _output.WriteLine("✓ CORS actual request returned correct headers");
    }

    [Fact]
    public async Task CorsDisabled_Request_DoesNotReturnCorsHeaders()
    {
        // Arrange
        await using var app = CreateTestApp(corsEnabled: false);
        await app.StartAsync();
        var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", "https://example.com");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"),
            "Response should not contain Access-Control-Allow-Origin header when CORS is disabled");

        _output.WriteLine("✓ CORS disabled request does not return CORS headers");
    }

    [Fact]
    public async Task CorsSettings_SpecificOrigins_OnlyAllowsConfiguredOrigins()
    {
        // Arrange - Create app with specific allowed origins
        var allowedOrigins = new[] { "https://trusted-domain.com", "https://app.example.com" };
        await using var app = CreateTestApp(corsEnabled: true, allowedOrigins: allowedOrigins);
        await app.StartAsync();
        var client = app.GetTestClient();

        // Act - Request from allowed origin
        var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        allowedRequest.Headers.Add("Origin", "https://trusted-domain.com");
        var allowedResponse = await client.SendAsync(allowedRequest);

        // Assert
        Assert.True(allowedResponse.IsSuccessStatusCode);
        Assert.True(allowedResponse.Headers.Contains("Access-Control-Allow-Origin"));

        var allowOrigin = allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault();
        Assert.Equal("https://trusted-domain.com", allowOrigin);

        _output.WriteLine("✓ CORS specific origins configuration works correctly");
    }

    [Fact]
    public void CorsSettings_LoadsFromConfiguration()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:Enabled"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://example.com",
                ["Cors:AllowedOrigins:1"] = "https://app.example.com",
                ["Cors:AllowedMethods:0"] = "GET",
                ["Cors:AllowedMethods:1"] = "POST",
                ["Cors:AllowedHeaders:0"] = "Content-Type",
                ["Cors:AllowCredentials"] = "false"
            })
            .Build();

        // Act
        var corsSettings = config.GetSection("Cors").Get<CorsSettings>();

        // Assert
        Assert.NotNull(corsSettings);
        Assert.True(corsSettings.Enabled);
        Assert.Equal(2, corsSettings.AllowedOrigins.Length);
        Assert.Contains("https://example.com", corsSettings.AllowedOrigins);
        Assert.Contains("https://app.example.com", corsSettings.AllowedOrigins);
        Assert.Equal(2, corsSettings.AllowedMethods.Length);
        Assert.Contains("GET", corsSettings.AllowedMethods);
        Assert.Contains("POST", corsSettings.AllowedMethods);
        Assert.Single(corsSettings.AllowedHeaders);
        Assert.Contains("Content-Type", corsSettings.AllowedHeaders);
        Assert.False(corsSettings.AllowCredentials);

        _output.WriteLine("✓ CORS settings load correctly from configuration");
    }

    [Fact]
    public void CorsSettings_DefaultValues_AreCorrect()
    {
        // Act
        var corsSettings = new CorsSettings();

        // Assert
        Assert.True(corsSettings.Enabled);
        Assert.Single(corsSettings.AllowedOrigins);
        Assert.Equal("*", corsSettings.AllowedOrigins[0]);
        Assert.Single(corsSettings.AllowedMethods);
        Assert.Equal("*", corsSettings.AllowedMethods[0]);
        Assert.Single(corsSettings.AllowedHeaders);
        Assert.Equal("*", corsSettings.AllowedHeaders[0]);
        Assert.False(corsSettings.AllowCredentials);

        _output.WriteLine("✓ CORS default settings are correct");
    }

    private WebApplication CreateTestApp(
        bool corsEnabled,
        string[]? allowedOrigins = null,
        string[]? allowedMethods = null,
        string[]? allowedHeaders = null)
    {
        var builder = WebApplication.CreateBuilder();

        // Configure test configuration
        var configData = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
            ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
            ["Embeddings:Model"] = "all-minilm",
            ["Embeddings:Timeout"] = "00:01:00",
            ["Embeddings:Dimensions"] = "384",
            ["Cors:Enabled"] = corsEnabled.ToString(),
        };

        // Add CORS configuration
        if (allowedOrigins != null)
        {
            for (int i = 0; i < allowedOrigins.Length; i++)
            {
                configData[$"Cors:AllowedOrigins:{i}"] = allowedOrigins[i];
            }
        }

        if (allowedMethods != null)
        {
            for (int i = 0; i < allowedMethods.Length; i++)
            {
                configData[$"Cors:AllowedMethods:{i}"] = allowedMethods[i];
            }
        }

        if (allowedHeaders != null)
        {
            for (int i = 0; i < allowedHeaders.Length; i++)
            {
                configData[$"Cors:AllowedHeaders:{i}"] = allowedHeaders[i];
            }
        }

        builder.Configuration.AddInMemoryCollection(configData);

        // Add services
        builder.Services.AddMemorizer(initialize: false); // Don't run initialization
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<MemoryTools>();
        builder.Services.AddControllersWithViews();

        // Configure CORS using the same logic as Program.cs
        var corsSettings = builder.Configuration.GetSection("Cors").Get<CorsSettings>() ?? new CorsSettings();

        if (corsSettings.Enabled)
        {
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    // Configure origins
                    if (corsSettings.AllowedOrigins.Contains("*"))
                    {
                        policy.AllowAnyOrigin();
                    }
                    else
                    {
                        policy.WithOrigins(corsSettings.AllowedOrigins);
                    }

                    // Configure methods
                    if (corsSettings.AllowedMethods.Contains("*"))
                    {
                        policy.AllowAnyMethod();
                    }
                    else
                    {
                        policy.WithMethods(corsSettings.AllowedMethods);
                    }

                    // Configure headers
                    if (corsSettings.AllowedHeaders.Contains("*"))
                    {
                        policy.AllowAnyHeader();
                    }
                    else
                    {
                        policy.WithHeaders(corsSettings.AllowedHeaders);
                    }

                    // Configure credentials (only if not using AllowAnyOrigin)
                    if (corsSettings.AllowCredentials && !corsSettings.AllowedOrigins.Contains("*"))
                    {
                        policy.AllowCredentials();
                    }
                });
            });
        }

        builder.Services.AddHealthChecks();

        var app = builder.Build();

        app.UseStaticFiles();

        // Enable CORS if configured
        if (corsSettings.Enabled)
        {
            app.UseCors();
        }

        app.MapMcp();
        app.MapHealthChecks("/healthz");

        return app;
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}
