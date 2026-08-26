using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Memorizer.Services;
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
                    var logger = context.Services?.GetService<ILoggerFactory>()
                        ?.CreateLogger("Memorizer.Tools.Validation");

                    // A write that could not generate its search embedding is an expected,
                    // honest failure (see #215): tell the caller plainly it was not saved
                    // and why, instead of the generic "check the arguments" message.
                    if (FindEmbeddingFailure(ex) is { } embeddingFailure)
                    {
                        logger?.LogWarning(ex, "Tool {Tool} could not generate an embedding", toolName);
                        return ErrorResult(ToolErrorMessages.EmbeddingUnavailable(toolName, embeddingFailure));
                    }

                    // Backstop: something else threw past validation (an argument shape we
                    // did not anticipate, or another failure in the tool body). Own the
                    // message rather than let the SDK emit its opaque one.
                    logger?.LogError(ex, "Tool {Tool} threw after argument validation", toolName);
                    return ErrorResult(
                        $"{toolName}: the call could not be completed ({ex.Message}). "
                        + "Check the arguments against the tool's schema and retry.");
                }
            });
        });

        return builder;
    }

    // Walk the exception chain — the SDK invocation layer may wrap the tool's exception.
    private static EmbeddingGenerationException? FindEmbeddingFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is EmbeddingGenerationException embedding)
                return embedding;
        return null;
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
    };
}
