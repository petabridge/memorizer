using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memorizer.Models;

/// <summary>
/// Common interface for all strongly-typed entity identifiers.
/// </summary>
public interface IEntityId
{
    Guid Value { get; }
}

/// <summary>
/// Generic JSON converter for all IEntityId types.
/// Serializes as UUID string for API compatibility.
/// </summary>
public class EntityIdJsonConverter<T> : JsonConverter<T> where T : struct, IEntityId
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var guidString = reader.GetString();
        if (Guid.TryParse(guidString, out var guid))
        {
            // Use reflection to create instance - all ID types have (Guid) constructor
            return (T)Activator.CreateInstance(typeof(T), guid)!;
        }
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value.ToString());
    }
}

/// <summary>
/// Strongly-typed identifier for Memory entities.
/// Provides compile-time type safety to prevent mixing up different Guid identifiers.
/// </summary>
[JsonConverter(typeof(EntityIdJsonConverter<MemoryId>))]
public readonly record struct MemoryId(Guid Value) : IEntityId, IComparable<MemoryId>
{
    /// <summary>
    /// Creates a new random MemoryId.
    /// </summary>
    public static MemoryId New() => new(Guid.NewGuid());

    /// <summary>
    /// Represents an empty/default MemoryId.
    /// </summary>
    public static readonly MemoryId Empty = new(Guid.Empty);

    /// <summary>
    /// Attempts to parse a string as a MemoryId.
    /// </summary>
    public static bool TryParse(string? s, out MemoryId result)
    {
        if (Guid.TryParse(s, out var guid))
        {
            result = new MemoryId(guid);
            return true;
        }
        result = Empty;
        return false;
    }

    /// <summary>
    /// Parses a string as a MemoryId, throwing on failure.
    /// </summary>
    public static MemoryId Parse(string s) => new(Guid.Parse(s));

    // Prefixes that recall/other subsystems attach to a raw GUID (e.g. "doc-{guid}").
    private static readonly string[] KnownIdPrefixes = ["doc-", "memory-", "mem-"];

    /// <summary>
    /// Attempts to parse a string as a MemoryId, tolerating the shapes agents actually
    /// send instead of a bare GUID: surrounding whitespace, a pasted URL or path
    /// (".../view/{id}"), a query/fragment tail, and the "doc-"/"memory-"/"mem-" prefixes
    /// that recall subsystems attach. Accepts both the hyphenated ("D") and 32-hex ("N")
    /// GUID forms. Use this for MCP tool id parameters so a lightly-mangled id resolves
    /// instead of erroring.
    /// </summary>
    public static bool TryParseLoose(string? s, out MemoryId result)
    {
        result = Empty;
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var candidate = s.Trim();

        // Drop any query/fragment tail (a pasted URL may carry "?v=2" or "#section").
        var tail = candidate.IndexOfAny(['?', '#']);
        if (tail >= 0)
            candidate = candidate[..tail];

        // Ignore trailing slashes, then keep only the last path segment if a URL/path
        // was pasted (".../view/{id}" or ".../view/{id}/").
        candidate = candidate.TrimEnd('/');
        var lastSlash = candidate.LastIndexOf('/');
        if (lastSlash >= 0)
            candidate = candidate[(lastSlash + 1)..];

        // Strip a known id prefix, if present.
        foreach (var prefix in KnownIdPrefixes)
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[prefix.Length..];
                break;
            }
        }

        return TryParse(candidate, out result);
    }

    public int CompareTo(MemoryId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();

    // Explicit conversion to/from Guid for database boundary
    public static explicit operator Guid(MemoryId id) => id.Value;
    public static explicit operator MemoryId(Guid guid) => new(guid);
}

/// <summary>
/// Strongly-typed identifier for MemoryRelationship entities.
/// </summary>
[JsonConverter(typeof(EntityIdJsonConverter<RelationshipId>))]
public readonly record struct RelationshipId(Guid Value) : IEntityId, IComparable<RelationshipId>
{
    public static RelationshipId New() => new(Guid.NewGuid());
    public static readonly RelationshipId Empty = new(Guid.Empty);

    public static bool TryParse(string? s, out RelationshipId result)
    {
        if (Guid.TryParse(s, out var guid))
        {
            result = new RelationshipId(guid);
            return true;
        }
        result = Empty;
        return false;
    }

    public static RelationshipId Parse(string s) => new(Guid.Parse(s));

    public int CompareTo(RelationshipId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();

    public static explicit operator Guid(RelationshipId id) => id.Value;
    public static explicit operator RelationshipId(Guid guid) => new(guid);
}

/// <summary>
/// Strongly-typed identifier for MemoryVersion entities.
/// </summary>
[JsonConverter(typeof(EntityIdJsonConverter<VersionId>))]
public readonly record struct VersionId(Guid Value) : IEntityId, IComparable<VersionId>
{
    public static VersionId New() => new(Guid.NewGuid());
    public static readonly VersionId Empty = new(Guid.Empty);

    public static bool TryParse(string? s, out VersionId result)
    {
        if (Guid.TryParse(s, out var guid))
        {
            result = new VersionId(guid);
            return true;
        }
        result = Empty;
        return false;
    }

    public static VersionId Parse(string s) => new(Guid.Parse(s));

    public int CompareTo(VersionId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();

    public static explicit operator Guid(VersionId id) => id.Value;
    public static explicit operator VersionId(Guid guid) => new(guid);
}

/// <summary>
/// Strongly-typed identifier for MemoryEvent entities.
/// </summary>
[JsonConverter(typeof(EntityIdJsonConverter<EventId>))]
public readonly record struct EventId(Guid Value) : IEntityId, IComparable<EventId>
{
    public static EventId New() => new(Guid.NewGuid());
    public static readonly EventId Empty = new(Guid.Empty);

    public static bool TryParse(string? s, out EventId result)
    {
        if (Guid.TryParse(s, out var guid))
        {
            result = new EventId(guid);
            return true;
        }
        result = Empty;
        return false;
    }

    public static EventId Parse(string s) => new(Guid.Parse(s));

    public int CompareTo(EventId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();

    public static explicit operator Guid(EventId id) => id.Value;
    public static explicit operator EventId(Guid guid) => new(guid);
}

/// <summary>
/// Strongly-typed identifier for ProviderSettings entities.
/// </summary>
[JsonConverter(typeof(EntityIdJsonConverter<ProviderSettingsId>))]
public readonly record struct ProviderSettingsId(Guid Value) : IEntityId, IComparable<ProviderSettingsId>
{
    public static ProviderSettingsId New() => new(Guid.NewGuid());
    public static readonly ProviderSettingsId Empty = new(Guid.Empty);

    public static bool TryParse(string? s, out ProviderSettingsId result)
    {
        if (Guid.TryParse(s, out var guid))
        {
            result = new ProviderSettingsId(guid);
            return true;
        }
        result = Empty;
        return false;
    }

    public static ProviderSettingsId Parse(string s) => new(Guid.Parse(s));

    public int CompareTo(ProviderSettingsId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();

    public static explicit operator Guid(ProviderSettingsId id) => id.Value;
    public static explicit operator ProviderSettingsId(Guid guid) => new(guid);
}

/// <summary>
/// Strongly-typed identifier for Workspace entities (future use).
/// Defined now for consistency when Workspaces feature is implemented.
/// </summary>
[JsonConverter(typeof(EntityIdJsonConverter<WorkspaceId>))]
public readonly record struct WorkspaceId(Guid Value) : IEntityId, IComparable<WorkspaceId>
{
    public static WorkspaceId New() => new(Guid.NewGuid());
    public static readonly WorkspaceId Empty = new(Guid.Empty);

    public static bool TryParse(string? s, out WorkspaceId result)
    {
        if (Guid.TryParse(s, out var guid))
        {
            result = new WorkspaceId(guid);
            return true;
        }
        result = Empty;
        return false;
    }

    public static WorkspaceId Parse(string s) => new(Guid.Parse(s));

    public int CompareTo(WorkspaceId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();

    public static explicit operator Guid(WorkspaceId id) => id.Value;
    public static explicit operator WorkspaceId(Guid guid) => new(guid);
}

/// <summary>
/// Strongly-typed identifier for Project entities (future use).
/// Defined now for consistency when Projects feature is implemented.
/// </summary>
[JsonConverter(typeof(EntityIdJsonConverter<ProjectId>))]
public readonly record struct ProjectId(Guid Value) : IEntityId, IComparable<ProjectId>
{
    public static ProjectId New() => new(Guid.NewGuid());
    public static readonly ProjectId Empty = new(Guid.Empty);

    public static bool TryParse(string? s, out ProjectId result)
    {
        if (Guid.TryParse(s, out var guid))
        {
            result = new ProjectId(guid);
            return true;
        }
        result = Empty;
        return false;
    }

    public static ProjectId Parse(string s) => new(Guid.Parse(s));

    public int CompareTo(ProjectId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();

    public static explicit operator Guid(ProjectId id) => id.Value;
    public static explicit operator ProjectId(Guid guid) => new(guid);
}
