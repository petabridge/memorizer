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

    // ---- Argument-validation boundary (Piece 1) ----
    //
    // These drive the real server the way a confused agent does. Before the 2.3.0 SDK
    // bump these calls returned the opaque "An error occurred invoking '<tool>'"; the
    // CallTool boundary must now return a correctable message instead. The negative
    // assertion on OpaqueSdkError is the standing tripwire for the next SDK bump.

    private const string OpaqueSdkError = "An error occurred invoking";

    private static string ResultText(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task Store_without_a_required_field_returns_a_correctable_error_not_the_opaque_sdk_error()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await ConnectAsync(cts.Token);

        // 'title' omitted — still required (it's the search index). 'type'/'source' now
        // default, so title is the probe for the boundary's required-field handling.
        var result = await mcp.Client.CallToolAsync(
            "store",
            new Dictionary<string, object?>
            {
                ["text"] = "hello world",
            },
            cancellationToken: cts.Token);

        var text = ResultText(result);
        _output.WriteLine(text);
        Assert.True(result.IsError == true);
        Assert.DoesNotContain(OpaqueSdkError, text);
        // Positively assert the message is actually corrective — names the field and
        // tells the agent it is required. Without this, the test would still pass if the
        // SDK front-ran us with some other unhelpful message that merely lacks the opaque
        // string.
        Assert.Contains("title", text);
        Assert.Contains("required", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Valid_id_passes_the_filter_and_reaches_the_tool()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await ConnectAsync(cts.Token);

        // A well-formed, hyphenated UUID must pass straight through the filter to the
        // tool body — the positive counterpart to the N-format case. A nonexistent id
        // returns a graceful "not found" without touching the embedding backend, so the
        // assertion stays independent of Class B (embedding) behaviour.
        var result = await mcp.Client.CallToolAsync(
            "get",
            new Dictionary<string, object?> { ["id"] = "00000000-0000-0000-0000-000000000001" },
            cancellationToken: cts.Token);

        var text = ResultText(result);
        _output.WriteLine(text);
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("dec906c7e5d14abebaf827af5a842ae5")]      // 32-hex, no hyphens (N-format)
    [InlineData("doc-dec906c7e5d14abebaf827af5a842ae5")]  // recall doc- prefix, N-format
    public async Task Get_with_lightly_mangled_id_is_normalized_and_resolves(string id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await ConnectAsync(cts.Token);

        // Piece 2: ids are parsed loosely, so these resolve to a real UUID and reach the
        // tool (which returns a graceful "not found" since nothing is stored) instead of
        // erroring at the boundary. No embedding is generated on a not-found get.
        var result = await mcp.Client.CallToolAsync(
            "get",
            new Dictionary<string, object?> { ["id"] = id },
            cancellationToken: cts.Token);

        var text = ResultText(result);
        _output.WriteLine(text);
        Assert.NotEqual(true, result.IsError);
        Assert.DoesNotContain(OpaqueSdkError, text);
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_with_unparseable_id_returns_a_correctable_message()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await ConnectAsync(cts.Token);

        var result = await mcp.Client.CallToolAsync(
            "get",
            new Dictionary<string, object?> { ["id"] = "not-a-real-id" },
            cancellationToken: cts.Token);

        var text = ResultText(result);
        _output.WriteLine(text);
        Assert.DoesNotContain(OpaqueSdkError, text);
        // Tool-body validation returns a plain, corrective string (the existing convention).
        Assert.Contains("Invalid memory id", text);
    }

    [Theory]
    [InlineData("store")]
    [InlineData("get")]
    [InlineData("edit")]
    public async Task Omitting_required_parameters_never_yields_the_opaque_sdk_error(string tool)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await ConnectAsync(cts.Token);

        var result = await mcp.Client.CallToolAsync(
            tool,
            new Dictionary<string, object?>(), // no arguments at all
            cancellationToken: cts.Token);

        var text = ResultText(result);
        _output.WriteLine($"{tool}: {text}");
        Assert.True(result.IsError == true);
        Assert.DoesNotContain(OpaqueSdkError, text);
        // Every tool with required params must name one as missing — a corrective message,
        // not merely the absence of the opaque one.
        Assert.Contains("required", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Store_with_text_too_long_to_embed_fails_honestly_never_opaque_or_misleading()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await ConnectAsync(cts.Token);

        // A body far beyond the embedding model's context window. Depending on the backend
        // it may reject the embedding (fail) or truncate (succeed); either way the caller
        // must never see the opaque SDK error or the misleading "check the arguments"
        // advice, and if it fails it must be the honest "not saved" embedding message.
        var longText = string.Concat(Enumerable.Repeat("context and detail. ", 600)); // ~12 KB
        var result = await mcp.Client.CallToolAsync(
            "store",
            new Dictionary<string, object?>
            {
                ["text"] = longText,
                ["title"] = "Very long memory",
            },
            cancellationToken: cts.Token);

        var text = ResultText(result);
        _output.WriteLine(text.Length > 300 ? text[..300] : text);
        Assert.DoesNotContain(OpaqueSdkError, text);
        Assert.DoesNotContain("Check the arguments", text);
        if (result.IsError == true)
            Assert.Contains("not saved", text, StringComparison.OrdinalIgnoreCase);
    }
}
