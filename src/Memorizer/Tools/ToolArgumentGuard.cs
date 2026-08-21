using System.Text;
using System.Text.Json;

namespace Memorizer.Tools;

/// <summary>
/// Schema-driven validation of MCP tool arguments, run at the CallTool boundary
/// <em>before</em> the SDK's argument marshaller. The 2.2.0 MCP SDK
/// (<c>Microsoft.Extensions.AI.AIFunctionFactory</c>) throws an
/// <see cref="ArgumentException"/> when a required parameter is missing or a typed
/// parameter (e.g. <see cref="Guid"/>) is malformed, and that exception surfaces to
/// the client as an opaque <c>"An error occurred invoking '&lt;tool&gt;'"</c>. Agents
/// cannot recover from that. This guard inspects the raw arguments against the tool's
/// own advertised JSON schema and returns a memorizer-authored, correctable message so
/// the model can fix its next call.
///
/// The logic is a pure function of (tool name, schema, arguments) so it can be unit
/// tested without booting the SDK. The CallTool filter is a thin adapter over it.
/// </summary>
public static class ToolArgumentGuard
{
    /// <summary>
    /// Validate <paramref name="arguments"/> against <paramref name="inputSchema"/>.
    /// Returns <c>null</c> when the call is well-formed (let it proceed), or a
    /// correctable, agent-facing message describing the first problem found.
    /// </summary>
    /// <param name="toolName">Snake_case tool name, for the message prefix.</param>
    /// <param name="inputSchema">The tool's advertised JSON schema (the same one the
    /// SDK emits in ListTools). If it is not a JSON object, validation is skipped.</param>
    /// <param name="arguments">The raw arguments the client sent.</param>
    public static string? Validate(
        string toolName,
        JsonElement inputSchema,
        IDictionary<string, JsonElement>? arguments)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object)
            return null; // No schema to validate against — fail open to the pipeline.

        var properties = inputSchema.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object
                ? props
                : default;

        // 1. Required fields must be present and not effectively empty.
        if (inputSchema.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var requiredField in required.EnumerateArray())
            {
                if (requiredField.ValueKind != JsonValueKind.String)
                    continue;

                var field = requiredField.GetString()!;
                var declaredType = DeclaredType(properties, field);

                if (arguments is null
                    || !arguments.TryGetValue(field, out var value)
                    || IsEffectivelyMissing(value, declaredType))
                {
                    return MissingRequiredMessage(toolName, field, properties, required);
                }
            }
        }

        // 2. Present fields must match their declared type / format.
        if (arguments is not null && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var (name, value) in arguments)
            {
                if (!properties.TryGetProperty(name, out var propSchema)
                    || propSchema.ValueKind != JsonValueKind.Object)
                    continue; // Unknown/extra arg — the marshaller ignores it; so do we.

                if (value.ValueKind is JsonValueKind.Null)
                    continue; // Null on an optional field is fine.

                var declaredType = TypeOf(propSchema);

                if (declaredType is { } t && !KindMatches(t, value.ValueKind))
                    return TypeMismatchMessage(toolName, name, t, value);

                if (IsUuidFormat(propSchema) && !IsBinderCompatibleGuid(value))
                    return InvalidIdMessage(toolName, name, value);
            }
        }

        return null;
    }

    /// <summary>
    /// A string field counts as "missing" when absent, JSON null, whitespace, or the
    /// literal string "null" — the shapes MCP clients (e.g. Cursor) send for an
    /// omitted value. See #166.
    /// </summary>
    private static bool IsEffectivelyMissing(JsonElement value, string? declaredType)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return true;

        if (declaredType == "string" && value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            return string.IsNullOrWhiteSpace(s) || string.Equals(s, "null", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string? DeclaredType(JsonElement properties, string field)
        => properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(field, out var p)
            && p.ValueKind == JsonValueKind.Object
                ? TypeOf(p)
                : null;

    /// <summary>Reads a schema node's "type", tolerating the JSON-Schema union form
    /// <c>["string","null"]</c> by taking the first non-null entry.</summary>
    private static string? TypeOf(JsonElement propSchema)
    {
        if (!propSchema.TryGetProperty("type", out var type))
            return null;

        if (type.ValueKind == JsonValueKind.String)
            return type.GetString();

        if (type.ValueKind == JsonValueKind.Array)
            foreach (var entry in type.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } s && s != "null")
                    return s;

        return null;
    }

    private static bool KindMatches(string declaredType, JsonValueKind kind) => declaredType switch
    {
        "string" => kind == JsonValueKind.String,
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        "number" or "integer" => kind == JsonValueKind.Number,
        "array" => kind == JsonValueKind.Array,
        "object" => kind == JsonValueKind.Object,
        _ => true, // Unknown/composite schema — don't second-guess the marshaller.
    };

    private static bool IsUuidFormat(JsonElement propSchema)
        => propSchema.TryGetProperty("format", out var fmt)
            && fmt.ValueKind == JsonValueKind.String
            && string.Equals(fmt.GetString(), "uuid", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the value would bind to a <see cref="Guid"/> through the SDK's
    /// System.Text.Json marshaller, which accepts only the hyphenated "D" form. A
    /// 32-hex "N" form (as recall subsystems emit, e.g. <c>doc-…</c> ids) parses via
    /// <see cref="Guid.TryParse"/> but is rejected by the binder — so we flag it here.
    /// </summary>
    private static bool IsBinderCompatibleGuid(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(value.GetString(), "D", out _);

    private static string MissingRequiredMessage(
        string toolName, string field, JsonElement properties, JsonElement required)
    {
        var sb = new StringBuilder();
        sb.Append($"{toolName}: required field '{field}' is missing.");

        if (Description(properties, field) is { Length: > 0 } desc)
            sb.Append($" {desc}");

        sb.Append(' ').Append("Provide it and retry. Minimal example: ")
          .Append(MinimalExample(properties, required));
        return sb.ToString();
    }

    private static string TypeMismatchMessage(string toolName, string field, string declaredType, JsonElement value)
        => $"{toolName}: field '{field}' must be {Article(declaredType)} {declaredType}, but got {value.ValueKind}. "
           + "Fix the value's type and retry.";

    private static string InvalidIdMessage(string toolName, string field, JsonElement value)
        => $"{toolName}: field '{field}' must be a UUID like '00000000-0000-0000-0000-000000000000'. "
           + $"Got '{value.GetString()}'. Use the id exactly as returned by store/search (drop any 'doc-' prefix).";

    private static string? Description(JsonElement properties, string field)
    {
        if (properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(field, out var p)
            && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("description", out var d)
            && d.ValueKind == JsonValueKind.String)
        {
            var text = d.GetString();
            return text is { Length: > 160 } ? text[..160] + "…" : text;
        }
        return null;
    }

    /// <summary>Builds a minimal JSON object containing exactly the required fields,
    /// with placeholder values keyed off each field's declared type, so the agent sees
    /// the shape it must send.</summary>
    private static string MinimalExample(JsonElement properties, JsonElement required)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var r in required.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.String) continue;
            var field = r.GetString()!;
            if (!first) sb.Append(", ");
            first = false;
            sb.Append('"').Append(field).Append("\": ").Append(Placeholder(DeclaredType(properties, field)));
        }
        return sb.Append('}').ToString();
    }

    private static string Placeholder(string? type) => type switch
    {
        "number" or "integer" => "1",
        "boolean" => "true",
        "array" => "[]",
        "object" => "{}",
        _ => "\"…\"",
    };

    private static string Article(string word)
        => word.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(word[0])) ? "an" : "a";
}
