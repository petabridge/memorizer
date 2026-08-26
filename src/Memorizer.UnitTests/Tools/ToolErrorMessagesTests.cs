using Memorizer.Services;
using Memorizer.Tools;

namespace Memorizer.UnitTests.Tools;

/// <summary>
/// Unit tests for <see cref="ToolErrorMessages"/> — the honest, caller-facing message a
/// tool returns when a write could not be saved because its search embedding could not be
/// generated (Piece 4). The message must state plainly that nothing was saved, surface the
/// backend's own reason, and never give the misleading "check the arguments" advice.
/// </summary>
public class ToolErrorMessagesTests
{
    private static EmbeddingGenerationException Failure(
        string backendMessage, string model = "all-minilm", int length = 6000)
        => new(
            $"Failed to generate embedding using model '{model}' for input of length {length}.",
            new HttpRequestException(backendMessage),
            model,
            length);

    [Fact]
    public void EmbeddingUnavailable_TooLong_TellsCallerToShorten_AndSurfacesLengthAndReason()
    {
        var ex = Failure("Embedding request failed with status 500 (InternalServerError): the input length exceeds the context length");

        var msg = ToolErrorMessages.EmbeddingUnavailable("store", ex);

        Assert.Contains("not saved", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("too long", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("6000", msg);                 // input length surfaced
        Assert.Contains("context length", msg);       // backend's own reason passed through
        Assert.DoesNotContain("Check the arguments", msg);
    }

    [Fact]
    public void EmbeddingUnavailable_BackendDown_TellsCallerToRetry()
    {
        var ex = Failure("Connection refused (localhost:11434)");

        var msg = ToolErrorMessages.EmbeddingUnavailable("store", ex);

        Assert.Contains("not saved", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Check the arguments", msg);
    }

    [Fact]
    public void EmbeddingUnavailable_IsPrefixedWithToolName()
    {
        var msg = ToolErrorMessages.EmbeddingUnavailable("store", Failure("boom"));
        Assert.StartsWith("store:", msg);
    }
}
