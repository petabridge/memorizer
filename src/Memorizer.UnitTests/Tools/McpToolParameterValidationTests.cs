using Memorizer.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using PostgMem.Tools;

namespace Memorizer.UnitTests.Tools;

/// <summary>
/// Regression tests for MCP tool resilience to missing or malformed required parameters.
///
/// MCP clients (Claude Code, opencode) sometimes omit required parameters entirely, which
/// previously caused an unhandled <see cref="System.ArgumentException"/> ("the arguments
/// dictionary is missing a value for the required parameter") at the deserialization layer,
/// before tool code ran. These tests pin the defensive validation that converts those cases
/// into descriptive error strings — and guard against anyone reverting a <c>string?</c>
/// parameter back to a required <c>Guid</c>/<c>Guid[]</c>.
///
/// Every case here is exercised through the guard clauses, which return before any storage
/// call, so a bare fake <see cref="FakeWorkspaceStorage"/> is sufficient. The tests also
/// implicitly assert that no exception escapes: an awaited call that threw would fail the test.
/// </summary>
public class McpToolParameterValidationTests
{
    private static MemoryTools CreateMemoryTools() => new(
        new FakeWorkspaceStorage(),
        NullLogger<MemoryTools>.Instance,
        new SearchSettings { ReturnFullContent = false },
        new FakeCanonicalUrlService { IsConfigured = false });

    private static WorkspaceTools CreateWorkspaceTools() => new(
        new FakeWorkspaceStorage(),
        NullLogger<WorkspaceTools>.Instance,
        new FakeCanonicalUrlService { IsConfigured = false });

    // ===== Store =====
    // Only text and title are required now; 'type' and 'source' default (see Store defaults
    // coverage in MemoryToolsCanonicalUrlTests / ToolArgumentGuardTests).

    [Fact]
    public async Task Store_WhenTextMissing_ReturnsErrorNotException()
    {
        var result = await CreateMemoryTools().Store(text: "   ", title: "Title", type: "reference", source: "LLM");
        Assert.Contains("'text' is required", result);
    }

    [Fact]
    public async Task Store_WhenTitleMissing_ReturnsErrorNotException()
    {
        var result = await CreateMemoryTools().Store(text: "body", title: "", type: "reference", source: "LLM");
        Assert.Contains("'title' is required", result);
    }

    // ===== Edit =====

    [Fact]
    public async Task Edit_WhenOldTextMissing_ReturnsErrorNotException()
    {
        // 'old_text' omitted was observed 8x in production logs.
        var result = await CreateMemoryTools().Edit(id: Guid.NewGuid().ToString(), old_text: "", new_text: "replacement");
        Assert.Contains("'old_text' is required", result);
    }

    [Fact]
    public async Task Edit_WhenNewTextNull_ReturnsErrorNotException()
    {
        var result = await CreateMemoryTools().Edit(id: Guid.NewGuid().ToString(), old_text: "find me", new_text: null!);
        Assert.Contains("'new_text' is required", result);
    }

    // ===== CreateReference =====

    [Fact]
    public async Task CreateReference_WhenFromIdMissing_ReturnsErrorNotException()
    {
        // 'fromId' omitted was observed 10x in production logs — the most frequent case.
        var result = await CreateMemoryTools().CreateReference(fromId: null, toId: Guid.NewGuid().ToString(), type: "related-to");
        Assert.Contains("'fromId' is required", result);
    }

    [Fact]
    public async Task CreateReference_WhenToIdMissing_ReturnsErrorNotException()
    {
        var result = await CreateMemoryTools().CreateReference(fromId: Guid.NewGuid().ToString(), toId: null, type: "related-to");
        Assert.Contains("'toId' is required", result);
    }

    [Fact]
    public async Task CreateReference_WhenTypeMissing_ReturnsErrorNotException()
    {
        var result = await CreateMemoryTools().CreateReference(fromId: Guid.NewGuid().ToString(), toId: Guid.NewGuid().ToString(), type: "");
        Assert.Contains("'type' is required", result);
    }

    [Fact]
    public async Task CreateReference_WhenFromIdMalformed_ReturnsErrorNotException()
    {
        var result = await CreateMemoryTools().CreateReference(fromId: "not-a-guid", toId: Guid.NewGuid().ToString(), type: "related-to");
        Assert.Contains("'fromId' must be a valid GUID", result);
        Assert.Contains("not-a-guid", result);
    }

    [Fact]
    public async Task CreateReference_WhenToIdMalformed_ReturnsErrorNotException()
    {
        // A valid fromId proves parsing succeeds for well-formed GUIDs before toId is rejected.
        var result = await CreateMemoryTools().CreateReference(fromId: Guid.NewGuid().ToString(), toId: "bad-guid", type: "related-to");
        Assert.Contains("'toId' must be a valid GUID", result);
        Assert.Contains("bad-guid", result);
    }

    // ===== MoveMemory =====

    [Fact]
    public async Task MoveMemory_WhenMemoryIdsNull_ReturnsErrorNotException()
    {
        // 'memoryIds' omitted was observed 1x in production logs.
        var result = await CreateWorkspaceTools().MoveMemory(memoryIds: null, toUnfiled: true);
        Assert.Contains("'memoryIds' is required", result);
    }

    [Fact]
    public async Task MoveMemory_WhenMemoryIdsEmpty_ReturnsErrorNotException()
    {
        var result = await CreateWorkspaceTools().MoveMemory(memoryIds: Array.Empty<string>(), toUnfiled: true);
        Assert.Contains("'memoryIds' is required", result);
    }

    [Fact]
    public async Task MoveMemory_WhenAllMemoryIdsMalformed_ReturnsErrorNotException()
    {
        var result = await CreateWorkspaceTools().MoveMemory(memoryIds: new[] { "nope", "also-bad" }, toUnfiled: true);
        Assert.Contains("No valid memory GUIDs", result);
        Assert.Contains("nope", result);
        Assert.Contains("also-bad", result);
    }

    [Fact]
    public async Task MoveMemory_WhenIdValidButNoDestination_ParsesGuidThenRequiresDestination()
    {
        // A well-formed GUID string must survive parsing and reach the destination check —
        // proving the string[] -> Guid conversion accepts valid input, not just rejects bad input.
        var result = await CreateWorkspaceTools().MoveMemory(memoryIds: new[] { Guid.NewGuid().ToString() });
        Assert.Contains("Must specify a destination", result);
    }
}
