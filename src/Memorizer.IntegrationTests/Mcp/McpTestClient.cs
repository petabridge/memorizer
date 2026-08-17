using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Memorizer.IntegrationTests.Mcp;

/// <summary>
/// Lightweight, programmable MCP client for integration tests. Connects to a
/// Memorizer MCP endpoint over the SDK's Streamable HTTP transport using a
/// caller-supplied <see cref="HttpClient"/> (e.g. one from
/// <c>WebApplicationFactory</c>, so the whole exchange runs in-process).
///
/// Intentionally minimal - no OAuth, catalog subscriptions, protocol-version
/// fallback, or session management - just enough to exercise the server end to
/// end. The full-featured client lives elsewhere; this is the thin harness.
/// </summary>
public sealed class McpTestClient : IAsyncDisposable
{
    /// <summary>The underlying SDK client, for tool discovery and invocation.</summary>
    public McpClient Client { get; }

    private McpTestClient(McpClient client) => Client = client;

    /// <summary>
    /// Connect to <paramref name="endpoint"/> on the server behind
    /// <paramref name="httpClient"/> and complete the MCP initialize handshake.
    /// </summary>
    public static async Task<McpTestClient> ConnectAsync(
        HttpClient httpClient,
        string endpoint = "/mcp",
        CancellationToken cancellationToken = default)
    {
        var baseAddress = httpClient.BaseAddress ?? new Uri("http://localhost/");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(baseAddress, endpoint),
                // Memorizer serves stateless Streamable HTTP; pin the transport to it so the
                // client does not first probe for a legacy SSE endpoint that does not exist.
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        var client = await McpClient.CreateAsync(transport, null, NullLoggerFactory.Instance, cancellationToken);
        return new McpTestClient(client);
    }

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
