using System.Text.Json;
using Memorizer.Models;

namespace Memorizer.UnitTests;

public class EntityIdTests
{
    #region MemoryId Tests

    [Fact]
    public void MemoryId_New_CreatesUniqueIds()
    {
        var id1 = MemoryId.New();
        var id2 = MemoryId.New();

        Assert.NotEqual(id1, id2);
        Assert.NotEqual(MemoryId.Empty, id1);
    }

    [Fact]
    public void MemoryId_Empty_ReturnsEmptyGuid()
    {
        Assert.Equal(Guid.Empty, MemoryId.Empty.Value);
    }

    [Fact]
    public void MemoryId_Parse_RoundTrips()
    {
        var original = MemoryId.New();
        var parsed = MemoryId.Parse(original.ToString());

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void MemoryId_TryParse_ValidGuid_ReturnsTrue()
    {
        var guid = Guid.NewGuid();
        var result = MemoryId.TryParse(guid.ToString(), out var id);

        Assert.True(result);
        Assert.Equal(guid, id.Value);
    }

    [Fact]
    public void MemoryId_TryParse_InvalidString_ReturnsFalse()
    {
        var result = MemoryId.TryParse("not-a-guid", out var id);

        Assert.False(result);
        Assert.Equal(MemoryId.Empty, id);
    }

    [Fact]
    public void MemoryId_TryParse_Null_ReturnsFalse()
    {
        var result = MemoryId.TryParse(null, out var id);

        Assert.False(result);
        Assert.Equal(MemoryId.Empty, id);
    }

    [Theory]
    [InlineData("dec906c7-e5d1-4abe-baf8-27af5a842ae5")]              // hyphenated (D)
    [InlineData("dec906c7e5d14abebaf827af5a842ae5")]                  // 32-hex (N)
    [InlineData("  dec906c7-e5d1-4abe-baf8-27af5a842ae5  ")]          // surrounding whitespace
    [InlineData("doc-dec906c7e5d14abebaf827af5a842ae5")]              // recall doc- prefix
    [InlineData("memory-dec906c7-e5d1-4abe-baf8-27af5a842ae5")]       // memory- prefix
    [InlineData("https://memory.testlab.petabridge.net/view/dec906c7-e5d1-4abe-baf8-27af5a842ae5")] // pasted URL
    [InlineData("https://memory.testlab.petabridge.net/view/dec906c7-e5d1-4abe-baf8-27af5a842ae5?v=2")] // URL + query
    [InlineData("DOC-dec906c7e5d14abebaf827af5a842ae5")]             // prefix is case-insensitive
    [InlineData("Memory-dec906c7-e5d1-4abe-baf8-27af5a842ae5")]      // mixed-case prefix
    [InlineData("DEC906C7-E5D1-4ABE-BAF8-27AF5A842AE5")]             // uppercase hex (D-format)
    [InlineData("DEC906C7E5D14ABEBAF827AF5A842AE5")]                 // uppercase hex (N-format)
    [InlineData("https://memory.testlab.petabridge.net/view/dec906c7-e5d1-4abe-baf8-27af5a842ae5/")]        // trailing slash
    [InlineData("https://memory.testlab.petabridge.net/view/dec906c7-e5d1-4abe-baf8-27af5a842ae5#details")] // URL fragment
    [InlineData("{dec906c7-e5d1-4abe-baf8-27af5a842ae5}")]           // braced form (GUID 'B')
    public void MemoryId_TryParseLoose_AcceptsAgentShapes(string input)
    {
        var ok = MemoryId.TryParseLoose(input, out var id);

        Assert.True(ok);
        Assert.Equal(Guid.Parse("dec906c7-e5d1-4abe-baf8-27af5a842ae5"), id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("doc-not-a-guid")]
    [InlineData("doc-")]                                              // prefix only, no id
    [InlineData("doc-memory-dec906c7e5d14abebaf827af5a842ae5")]       // only one prefix is stripped
    [InlineData("https://memory.testlab.petabridge.net/view/")]       // URL with no id segment
    [InlineData("12345")]                                             // too short to be a GUID
    public void MemoryId_TryParseLoose_RejectsGarbage(string? input)
    {
        var ok = MemoryId.TryParseLoose(input, out var id);

        Assert.False(ok);
        Assert.Equal(MemoryId.Empty, id);
    }

    [Fact]
    public void MemoryId_TryParseLoose_NeverMisreadsAValidGuid()
    {
        // Collision guard: normalization must never turn one valid GUID into a different
        // one. It only strips non-hex wrappers, and every strip-prefix (doc-/memory-/mem-)
        // begins with a non-hex letter while a canonical GUID string begins with a hex
        // digit — so no valid id can be mis-stripped. Verified over many random GUIDs in
        // both D and N forms; if a future prefix could ever lead a GUID, a round-trip here
        // would fail (either a mismatch or a rejected parse).
        for (var i = 0; i < 500; i++)
        {
            var g = Guid.NewGuid();

            Assert.True(MemoryId.TryParseLoose(g.ToString("D"), out var d));
            Assert.Equal(g, d.Value);

            Assert.True(MemoryId.TryParseLoose(g.ToString("N"), out var n));
            Assert.Equal(g, n.Value);
        }
    }

    [Fact]
    public void MemoryId_ExplicitCast_ToGuid_Works()
    {
        var guid = Guid.NewGuid();
        var memoryId = (MemoryId)guid;
        var backToGuid = (Guid)memoryId;

        Assert.Equal(guid, backToGuid);
    }

    [Fact]
    public void MemoryId_JsonSerialization_RoundTrips()
    {
        var original = MemoryId.New();
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<MemoryId>(json);

        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void MemoryId_JsonSerialization_SerializesAsString()
    {
        var id = MemoryId.New();
        var json = JsonSerializer.Serialize(id);

        // Should be a quoted string, not an object
        Assert.StartsWith("\"", json);
        Assert.EndsWith("\"", json);
    }

    [Fact]
    public void MemoryId_CompareTo_OrdersByGuidValue()
    {
        var id1 = new MemoryId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var id2 = new MemoryId(Guid.Parse("00000000-0000-0000-0000-000000000002"));

        Assert.True(id1.CompareTo(id2) < 0);
        Assert.True(id2.CompareTo(id1) > 0);
        Assert.Equal(0, id1.CompareTo(id1));
    }

    [Fact]
    public void MemoryId_Equality_WorksCorrectly()
    {
        var guid = Guid.NewGuid();
        var id1 = new MemoryId(guid);
        var id2 = new MemoryId(guid);
        var id3 = MemoryId.New();

        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.True(id1 == id2);
        Assert.True(id1 != id3);
    }

    #endregion

    #region RelationshipId Tests

    [Fact]
    public void RelationshipId_New_CreatesUniqueIds()
    {
        var id1 = RelationshipId.New();
        var id2 = RelationshipId.New();

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void RelationshipId_JsonSerialization_RoundTrips()
    {
        var original = RelationshipId.New();
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<RelationshipId>(json);

        Assert.Equal(original, deserialized);
    }

    #endregion

    #region VersionId Tests

    [Fact]
    public void VersionId_New_CreatesUniqueIds()
    {
        var id1 = VersionId.New();
        var id2 = VersionId.New();

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void VersionId_JsonSerialization_RoundTrips()
    {
        var original = VersionId.New();
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<VersionId>(json);

        Assert.Equal(original, deserialized);
    }

    #endregion

    #region EventId Tests

    [Fact]
    public void EventId_New_CreatesUniqueIds()
    {
        var id1 = EventId.New();
        var id2 = EventId.New();

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void EventId_JsonSerialization_RoundTrips()
    {
        var original = EventId.New();
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<EventId>(json);

        Assert.Equal(original, deserialized);
    }

    #endregion

    #region ProviderSettingsId Tests

    [Fact]
    public void ProviderSettingsId_New_CreatesUniqueIds()
    {
        var id1 = ProviderSettingsId.New();
        var id2 = ProviderSettingsId.New();

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ProviderSettingsId_JsonSerialization_RoundTrips()
    {
        var original = ProviderSettingsId.New();
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ProviderSettingsId>(json);

        Assert.Equal(original, deserialized);
    }

    #endregion

    #region Future ID Tests (WorkspaceId, ProjectId)

    [Fact]
    public void WorkspaceId_New_CreatesUniqueIds()
    {
        var id1 = WorkspaceId.New();
        var id2 = WorkspaceId.New();

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ProjectId_New_CreatesUniqueIds()
    {
        var id1 = ProjectId.New();
        var id2 = ProjectId.New();

        Assert.NotEqual(id1, id2);
    }

    #endregion

    #region Type Safety Tests

    [Fact]
    public void DifferentIdTypes_AreNotAssignable()
    {
        // This test documents the compile-time type safety
        // If these IDs were raw Guids, they could be accidentally swapped
        var memoryId = MemoryId.New();
        var relationshipId = RelationshipId.New();

        // These would be compile errors with strong types:
        // MemoryId wrongId = relationshipId; // Won't compile!

        // We can verify they're different types at runtime
        Assert.IsType<MemoryId>(memoryId);
        Assert.IsType<RelationshipId>(relationshipId);
        Assert.NotEqual(memoryId.GetType(), relationshipId.GetType());
    }

    [Fact]
    public void IdTypes_CanBeStoredInDictionary()
    {
        var dict = new Dictionary<MemoryId, string>();
        var id1 = MemoryId.New();
        var id2 = MemoryId.New();

        dict[id1] = "First";
        dict[id2] = "Second";

        Assert.Equal("First", dict[id1]);
        Assert.Equal("Second", dict[id2]);
    }

    [Fact]
    public void IdTypes_WorkInHashSet()
    {
        var set = new HashSet<MemoryId>();
        var id = MemoryId.New();

        set.Add(id);
        set.Add(id); // Adding same ID again

        Assert.Single(set);
        Assert.Contains(id, set);
    }

    #endregion
}
