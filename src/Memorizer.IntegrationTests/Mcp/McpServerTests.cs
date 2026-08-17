using Memorizer.IntegrationTests.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// End-to-end tests for the MCP server surface. Boots the real Memorizer app
/// in-process via <see cref="WebApplicationFactory{TEntryPoint}"/> (pointed at
/// the shared Postgres + Ollama Testcontainers) and drives it with the SDK MCP
/// client over the Streamable HTTP transport - exercising the same code path a
/// real MCP client uses, including the stateless-HTTP handshake.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class McpServerTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private WebApplicationFactory<Program> _factory = null!;

    public McpServerTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Highest-precedence config, so the app connects to the test containers.
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                        ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                        ["Embeddings:Model"] = "all-minilm",
                    });
                });
            });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private Task<McpTestClient> ConnectAsync(CancellationToken ct) =>
        McpTestClient.ConnectAsync(_factory.CreateClient(), cancellationToken: ct);

    [Fact]
    public async Task Server_advertises_memory_and_workspace_tools()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await ConnectAsync(cts.Token);

        var tools = await mcp.Client.ListToolsAsync(cancellationToken: cts.Token);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"Advertised tools ({names.Count}): {string.Join(", ", names.OrderBy(n => n))}");

        Assert.NotEmpty(tools);
        // A representative slice of the memory + workspace tool surface. The SDK advertises
        // tools in snake_case (derived from the [McpServerTool] method names).
        Assert.Contains("store", names);
        Assert.Contains("search_memories", names);
        Assert.Contains("get_workspace", names);
        Assert.Contains("get_project_context", names);
    }

    [Fact]
    public async Task Stateless_server_handles_independent_connections()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // Two independent handshakes against the stateless server. Each connection is a
        // separate client with no shared server-side session; both must succeed on their own.
        await using (var first = await ConnectAsync(cts.Token))
        {
            var result = await first.Client.CallToolAsync(
                "get_workspace",
                new Dictionary<string, object?>(),
                cancellationToken: cts.Token);

            Assert.NotEqual(true, result.IsError);
            Assert.NotEmpty(result.Content);
        }

        await using (var second = await ConnectAsync(cts.Token))
        {
            var tools = await second.Client.ListToolsAsync(cancellationToken: cts.Token);
            Assert.NotEmpty(tools);
        }
    }
}
