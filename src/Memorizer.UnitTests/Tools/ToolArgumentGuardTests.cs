using System.Text.Json;
using Memorizer.Tools;

namespace Memorizer.UnitTests.Tools;

/// <summary>
/// Unit tests for <see cref="ToolArgumentGuard"/> — the pure, schema-driven validation
/// that backs the CallTool boundary. These assert that a malformed call yields a
/// correctable message (and a well-formed one is allowed through) without booting the
/// MCP SDK. The end-to-end proof that the filter fires before the SDK marshaller lives
/// in the integration tests.
/// </summary>
public class ToolArgumentGuardTests
{
    private const string StoreSchema = """
    {
      "type": "object",
      "properties": {
        "type":       { "type": "string", "description": "The type of memory (e.g. 'reference', 'how-to')." },
        "text":       { "type": "string" },
        "source":     { "type": "string" },
        "title":      { "type": "string" },
        "confidence": { "type": "number" },
        "tags":       { "type": "array" }
      },
      "required": ["type", "text", "source", "title"]
    }
    """;

    private const string IdSchema = """
    {
      "type": "object",
      "properties": { "id": { "type": "string", "format": "uuid" } },
      "required": ["id"]
    }
    """;

    private static string? Validate(string schemaJson, string argsJson, string tool = "store")
    {
        using var schemaDoc = JsonDocument.Parse(schemaJson);
        using var argsDoc = JsonDocument.Parse(argsJson);
        var args = argsDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        return ToolArgumentGuard.Validate(tool, schemaDoc.RootElement, args);
    }

    [Fact]
    public void Missing_required_field_is_named_with_an_example()
    {
        // 'type' omitted — the exact shape that failed in the incident.
        var msg = Validate(StoreSchema, """{ "text": "hi", "source": "LLM", "title": "T" }""");

        Assert.NotNull(msg);
        Assert.Contains("type", msg);
        Assert.Contains("required", msg, StringComparison.OrdinalIgnoreCase);
        // The minimal example teaches the shape the agent must send.
        Assert.Contains("\"type\"", msg);
        Assert.Contains("\"text\"", msg);
    }

    [Fact]
    public void Missing_required_field_surfaces_its_description_as_a_hint()
    {
        var msg = Validate(StoreSchema, """{ "text": "hi", "source": "LLM", "title": "T" }""");
        Assert.Contains("type of memory", msg!);
    }

    [Theory]
    [InlineData("\"\"")]        // empty string
    [InlineData("\"   \"")]     // whitespace
    [InlineData("\"null\"")]    // literal "null" (Cursor-style), per #166
    [InlineData("null")]        // JSON null
    public void Empty_or_null_string_for_required_counts_as_missing(string typeValue)
    {
        var msg = Validate(StoreSchema, $$"""{ "type": {{typeValue}}, "text": "hi", "source": "LLM", "title": "T" }""");
        Assert.NotNull(msg);
        Assert.Contains("type", msg);
    }

    [Fact]
    public void All_required_present_is_allowed_through()
    {
        var msg = Validate(StoreSchema, """{ "type": "reference", "text": "hi", "source": "LLM", "title": "T" }""");
        Assert.Null(msg);
    }

    [Fact]
    public void Wrong_json_type_for_a_field_is_reported()
    {
        // text declared string, sent as a number.
        var msg = Validate(StoreSchema, """{ "type": "reference", "text": 42, "source": "LLM", "title": "T" }""");
        Assert.NotNull(msg);
        Assert.Contains("text", msg);
        Assert.Contains("string", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_uuid_is_reported_as_invalid_id()
    {
        // The N-format id (no hyphens) the model lifted from a recall doc-id.
        var msg = Validate(IdSchema, """{ "id": "dec906c7e5d14abebaf827af5a842ae5" }""", tool: "get");
        Assert.NotNull(msg);
        Assert.Contains("id", msg);
        Assert.Contains("UUID", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hyphenated_uuid_is_allowed_through()
    {
        var msg = Validate(IdSchema, """{ "id": "dec906c7-e5d1-4abe-baf8-27af5a842ae5" }""", tool: "get");
        Assert.Null(msg);
    }

    [Fact]
    public void Extra_unknown_argument_is_ignored_when_required_are_present()
    {
        var msg = Validate(StoreSchema,
            """{ "type": "reference", "text": "hi", "source": "LLM", "title": "T", "_rationale": "why" }""");
        Assert.Null(msg);
    }

    [Fact]
    public void Non_object_schema_skips_validation()
    {
        // Fail open: if there's no object schema to check against, don't block the call.
        var msg = Validate("\"not-a-schema\"", """{ "anything": 1 }""");
        Assert.Null(msg);
    }

    [Fact]
    public void Message_is_prefixed_with_the_tool_name()
    {
        var msg = Validate(StoreSchema, """{ "text": "hi", "source": "LLM", "title": "T" }""", tool: "store");
        Assert.StartsWith("store:", msg);
    }
}
