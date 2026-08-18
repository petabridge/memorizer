using Memorizer.IntegrationTests.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Protocol;
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

    [Fact]
    public async Task Server_advertises_usage_prompts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await ConnectAsync(cts.Token);

        var prompts = await mcp.Client.ListPromptsAsync(cancellationToken: cts.Token);
        var names = prompts.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"Advertised prompts ({names.Count}): {string.Join(", ", names.OrderBy(n => n))}");

        Assert.Contains("memorizer_overview", names);
        Assert.Contains("store_memory", names);
        Assert.Contains("find_context", names);
        Assert.Contains("start_project", names);

        // Fetch a parameterized prompt and confirm it renders the argument and the tool guidance.
        var result = await mcp.Client.GetPromptAsync(
            "find_context",
            new Dictionary<string, object?> { ["topic"] = "database pooling" },
            cancellationToken: cts.Token);

        Assert.NotEmpty(result.Messages);
        var text = string.Join("\n", result.Messages.Select(m => (m.Content as TextContentBlock)?.Text ?? ""));
        Assert.Contains("database pooling", text);
        Assert.Contains("search_memories", text);
    }

    [Fact]
    public async Task Server_exposes_workspace_tree_resource()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await ConnectAsync(cts.Token);

        // The workspace tree resource must appear in the resource list.
        var resources = await mcp.Client.ListResourcesAsync(cancellationToken: cts.Token);
        var uris = resources.Select(r => r.Uri).ToHashSet();
        _output.WriteLine($"Resources: {string.Join(", ", uris)}");
        Assert.Contains("memorizer://workspaces", uris);

        // Create a workspace via a tool, then confirm the resource reflects it (unique name
        // to stay isolated from other tests sharing the container).
        var workspaceName = $"McpResourceTest-{Guid.NewGuid():N}";
        await mcp.Client.CallToolAsync(
            "create_workspace",
            new Dictionary<string, object?> { ["name"] = workspaceName },
            cancellationToken: cts.Token);

        var read = await mcp.Client.ReadResourceAsync("memorizer://workspaces", cancellationToken: cts.Token);
        var contents = string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
        Assert.Contains(workspaceName, contents);
    }
}
