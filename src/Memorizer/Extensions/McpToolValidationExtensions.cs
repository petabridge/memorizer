using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Memorizer.Tools;

namespace Memorizer.Extensions;

/// <summary>
/// Registers a CallTool boundary that owns the error surface for every MCP tool.
///
/// The 2.2.0 SDK marshals tool arguments strictly and throws before the tool method
/// runs when a required parameter is missing or a typed parameter is malformed, which
/// the client sees as an opaque <c>"An error occurred invoking '&lt;tool&gt;'"</c>. This
/// filter validates the raw arguments against each tool's own advertised schema and
/// short-circuits with a correctable, memorizer-authored message — so no opaque SDK
/// error ever reaches an agent, for any tool, now or after a future SDK bump.
/// </summary>
public static class McpToolValidationExtensions
{
    public static IMcpServerBuilder AddToolArgumentValidation(this IMcpServerBuilder builder)
    {
        builder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var toolName = context.Params?.Name ?? "tool";

                // The tool's advertised JSON schema — the same one emitted in ListTools —
                // is the single source of truth for what's required and typed. Reusing it
                // means validation can never drift from the contract.
                var schema = (context.MatchedPrimitive as McpServerTool)?.ProtocolTool.InputSchema
                             ?? default;

                var message = ToolArgumentGuard.Validate(toolName, schema, context.Params?.Arguments);
                if (message is not null)
                    return ErrorResult(message);

                try
                {
                    return await next(context, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw; // Cancellation is not a tool error.
                }
                catch (Exception ex)
                {
                    // Backstop: something threw past validation (an argument shape we did
                    // not anticipate, or a failure inside the tool body). Own the message
                    // rather than let the SDK emit its opaque one.
                    context.Services?.GetService<ILoggerFactory>()
                        ?.CreateLogger("Memorizer.Tools.Validation")
                        .LogError(ex, "Tool {Tool} threw after argument validation", toolName);

                    return ErrorResult(
                        $"{toolName}: the call could not be completed ({ex.Message}). "
                        + "Check the arguments against the tool's schema and retry.");
                }
            });
        });

        return builder;
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
    };
}
