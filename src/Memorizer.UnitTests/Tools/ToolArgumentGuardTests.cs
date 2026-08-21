using System.Text.Json;
using Memorizer.Tools;
using ModelContextProtocol.Server;
using PostgMem.Tools;

namespace Memorizer.UnitTests.Tools;

/// <summary>
/// Unit tests for <see cref="ToolArgumentGuard"/> — the pure, schema-driven validation
/// that backs the CallTool boundary. These assert that a malformed call yields a
/// correctable message (and a well-formed one is allowed through) without booting the
/// MCP SDK server. The end-to-end proof that the filter fires before the SDK marshaller
/// lives in the integration tests.
///
/// The schemas here are generated from the real tool methods via the same SDK path the
/// server uses (<see cref="McpServerTool.Create(Delegate, McpServerToolCreateOptions?)"/>
/// → <c>ProtocolTool.InputSchema</c>), so the tests validate against the actual tool
/// contract rather than a hand-maintained copy that could drift.
/// </summary>
public class ToolArgumentGuardTests
{
    // Field values are irrelevant — only the method signatures/attributes drive schema
    // generation, which never invokes the tool, so null dependencies are fine.
    private static readonly MemoryTools SampleTools = new(null!, null!, null!, null!);

    private static JsonElement SchemaFor(Delegate toolMethod)
        => McpServerTool.Create(toolMethod).ProtocolTool.InputSchema;

    private static JsonElement StoreSchema => SchemaFor(SampleTools.Store);

    // Synthetic schema for a uuid-format field. The memory tools now take ids as loosely
    // parsed strings (Piece 2), so no real tool advertises format:uuid — but the guard's
    // uuid check is still exercised here for any tool that might.
    private const string UuidSchema = """
    { "type": "object", "properties": { "id": { "type": "string", "format": "uuid" } }, "required": ["id"] }
    """;

    private static string? Validate(JsonElement schema, string argsJson, string tool)
    {
        using var argsDoc = JsonDocument.Parse(argsJson);
        var args = argsDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        return ToolArgumentGuard.Validate(tool, schema, args);
    }

    private static string? Validate(string schemaJson, string argsJson, string tool)
    {
        using var schemaDoc = JsonDocument.Parse(schemaJson);
        return Validate(schemaDoc.RootElement, argsJson, tool);
    }

    [Fact]
    public void Missing_required_field_is_named_with_an_example()
    {
        // 'text' omitted — a required field (type/source now default, so they can't be the probe).
        var msg = Validate(StoreSchema, """{ "title": "T" }""", "store");

        Assert.NotNull(msg);
        Assert.Contains("text", msg);
        Assert.Contains("required", msg, StringComparison.OrdinalIgnoreCase);
        // The minimal example teaches the required shape.
        Assert.Contains("\"text\"", msg);
        Assert.Contains("\"title\"", msg);
    }

    [Fact]
    public void Missing_required_field_surfaces_its_description_as_a_hint()
    {
        var msg = Validate(StoreSchema, """{ "title": "T" }""", "store");
        Assert.Contains("Plain text", msg!);
    }

    [Theory]
    [InlineData("\"\"")]        // empty string
    [InlineData("\"   \"")]     // whitespace
    [InlineData("\"null\"")]    // literal "null" (Cursor-style), per #166
    [InlineData("null")]        // JSON null
    public void Empty_or_null_string_for_required_counts_as_missing(string textValue)
    {
        var msg = Validate(StoreSchema, $$"""{ "text": {{textValue}}, "title": "T" }""", "store");
        Assert.NotNull(msg);
        Assert.Contains("text", msg);
    }

    [Fact]
    public void All_required_present_is_allowed_through()
    {
        var msg = Validate(StoreSchema, """{ "type": "reference", "text": "hi", "source": "LLM", "title": "T" }""", "store");
        Assert.Null(msg);
    }

    [Fact]
    public void Wrong_json_type_for_a_field_is_reported()
    {
        // 'confidence' is declared as a number; send a string.
        var msg = Validate(StoreSchema,
            """{ "type": "reference", "text": "hi", "source": "LLM", "title": "T", "confidence": "high" }""", "store");
        Assert.NotNull(msg);
        Assert.Contains("confidence", msg);
        Assert.Contains("number", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_uuid_is_reported_as_invalid_id_when_schema_declares_uuid_format()
    {
        var msg = Validate(UuidSchema, """{ "id": "dec906c7e5d14abebaf827af5a842ae5" }""", "get");
        Assert.NotNull(msg);
        Assert.Contains("id", msg);
        Assert.Contains("UUID", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hyphenated_uuid_is_allowed_through_when_schema_declares_uuid_format()
    {
        var msg = Validate(UuidSchema, """{ "id": "dec906c7-e5d1-4abe-baf8-27af5a842ae5" }""", "get");
        Assert.Null(msg);
    }

    [Fact]
    public void Store_schema_requires_only_text_and_title()
    {
        // Piece 2: 'type' and 'source' now default, so they must not be advertised as required.
        var required = StoreSchema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();
        Assert.Contains("text", required);
        Assert.Contains("title", required);
        Assert.DoesNotContain("type", required);
        Assert.DoesNotContain("source", required);
    }

    [Fact]
    public void Extra_unknown_argument_is_ignored_when_required_are_present()
    {
        var msg = Validate(StoreSchema,
            """{ "type": "reference", "text": "hi", "source": "LLM", "title": "T", "_rationale": "why" }""", "store");
        Assert.Null(msg);
    }

    [Fact]
    public void Non_object_schema_skips_validation()
    {
        // Fail open: with no object schema to check against, don't block the call.
        using var doc = JsonDocument.Parse("\"not-a-schema\"");
        var args = new Dictionary<string, JsonElement>();
        Assert.Null(ToolArgumentGuard.Validate("store", doc.RootElement, args));
    }

    [Fact]
    public void Message_is_prefixed_with_the_tool_name()
    {
        var msg = Validate(StoreSchema, """{ "title": "T" }""", "store");
        Assert.StartsWith("store:", msg);
    }
}
