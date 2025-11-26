using System.Text.Json;
using Memorizer.Extensions;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for memory versioning functionality.
/// Tests version history creation, editing, and the handling of pre-versioning memories.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MemoryVersioningTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly IServiceProvider _serviceProvider;
    private readonly NpgsqlDataSource _dataSource;

    public MemoryVersioningTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;

        // Build a simple service provider for testing
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Create a data source for direct database access with pgvector support
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_fixture.PostgresConnectionString);
        dataSourceBuilder.UseVector();
        _dataSource = dataSourceBuilder.Build();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Add configuration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                ["Embeddings:Model"] = "all-minilm",
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString()
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);

        // Add HTTP client for embedding service
        services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
        {
            client.BaseAddress = new Uri(_fixture.OllamaApiUrl);
            client.Timeout = TimeSpan.FromMinutes(1);
        });

        // Add embedding settings
        services.AddSingleton(new EmbeddingSettings
        {
            ApiUrl = new Uri(_fixture.OllamaApiUrl),
            Model = "all-minilm",
            Timeout = TimeSpan.FromMinutes(1)
        });

        // Add Memorizer services
        services.AddMemorizer();

        // Add logging
        services.AddLogging();
    }

    /// <summary>
    /// Helper method to insert a pre-versioning memory directly into the database
    /// without creating version snapshots. Uses NpgsqlCommand to handle Vector types properly.
    /// </summary>
    private async Task InsertPreVersioningMemory(
        NpgsqlConnection connection,
        Guid memoryId,
        string type,
        string text,
        string source,
        string[] tags,
        double confidence,
        string title,
        float[] contentEmbedding,
        float[] metadataEmbedding)
    {
        const string sql = @"
            INSERT INTO memories (id, type, content, text, source, embedding, embedding_metadata, tags, confidence, created_at, updated_at, title, current_version)
            VALUES (@id, @type, @content, @text, @source, @embedding, @embeddingMetadata, @tags, @confidence, @createdAt, @updatedAt, @title, 1)";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("id", memoryId);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("content", JsonDocument.Parse("{}"));
        cmd.Parameters.AddWithValue("text", text);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("embedding", new Vector(contentEmbedding));
        cmd.Parameters.AddWithValue("embeddingMetadata", new Vector(metadataEmbedding));
        cmd.Parameters.AddWithValue("tags", tags);
        cmd.Parameters.AddWithValue("confidence", confidence);
        cmd.Parameters.AddWithValue("createdAt", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("title", title);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Simulates a pre-versioning memory by inserting directly into the database
    /// without creating any version snapshots. This is the scenario that occurs
    /// when a memory exists from before the versioning feature was added, but
    /// the migration to create version snapshots wasn't run or failed.
    /// </summary>
    [Fact]
    public async Task UpdateMemory_PreVersioningMemoryWithoutSnapshot_ShouldSucceed()
    {
        // Arrange: Create a memory directly in the database WITHOUT version snapshots
        // This simulates a memory that existed before versioning was added
        var memoryId = Guid.NewGuid();
        var embeddingService = _serviceProvider.GetRequiredService<IEmbeddingService>();
        var storage = _serviceProvider.GetRequiredService<IStorage>();

        // Generate embedding for the test content
        var contentEmbedding = await embeddingService.Generate("Original content for pre-versioning test", CancellationToken.None);
        var metadataEmbedding = await embeddingService.Generate("Pre-Versioning Test Memory", CancellationToken.None);

        await using var connection = await _dataSource.OpenConnectionAsync();

        // Insert memory directly without going through Storage service (simulating pre-versioning state)
        await InsertPreVersioningMemory(
            connection,
            memoryId,
            "pre-versioning-test",
            "Original content for pre-versioning test",
            "test",
            new[] { "test", "pre-versioning" },
            1.0,
            "Pre-Versioning Test Memory",
            contentEmbedding,
            metadataEmbedding);

        // Verify no version snapshots exist
        await using var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM memory_versions WHERE memory_id = @memoryId", connection);
        countCmd.Parameters.AddWithValue("memoryId", memoryId);
        var versionCount = (long)(await countCmd.ExecuteScalarAsync())!;
        Assert.Equal(0, versionCount);

        _output.WriteLine($"Created pre-versioning memory {memoryId} with no version snapshots");

        try
        {
            // Act: Update the memory using the Storage service
            // This should NOT throw a duplicate key error
            var updatedMemory = await storage.UpdateMemory(
                memoryId,
                "pre-versioning-test-updated",
                "Updated content after versioning feature added",
                "test",
                new[] { "test", "pre-versioning", "updated" },
                0.95,
                "Updated Pre-Versioning Test Memory",
                CancellationToken.None
            );

            // Assert: Update should succeed
            Assert.NotNull(updatedMemory);
            Assert.Equal(memoryId, updatedMemory.Id);
            Assert.Equal("Updated content after versioning feature added", updatedMemory.Text);
            Assert.Equal(2, updatedMemory.CurrentVersion);

            // Verify version snapshot was created for the old state
            var versions = await storage.GetVersionHistory(memoryId, null, CancellationToken.None);
            Assert.Single(versions);
            Assert.Equal(1, versions[0].VersionNumber);
            Assert.Equal("Original content for pre-versioning test", versions[0].Text);

            _output.WriteLine($"✓ Successfully updated pre-versioning memory. New version: {updatedMemory.CurrentVersion}");
        }
        finally
        {
            // Cleanup
            await storage.Delete(memoryId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tests that a memory created through the normal StoreMemory flow
    /// can be edited multiple times without duplicate key errors.
    /// </summary>
    [Fact]
    public async Task UpdateMemory_NewlyCreatedMemory_MultipleEdits_ShouldSucceed()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "version-test",
            "Initial content for versioning test",
            "test",
            new[] { "versioning", "test" },
            1.0,
            "Versioning Test Memory"
        );

        _output.WriteLine($"Created memory {memory.Id} with current_version={memory.CurrentVersion}");

        try
        {
            // Act: Perform multiple edits
            // First edit
            var edit1 = await storage.UpdateMemory(
                memory.Id,
                "version-test",
                "First edit content",
                "test",
                new[] { "versioning", "test", "edit1" },
                0.9,
                "Versioning Test Memory - Edit 1",
                CancellationToken.None
            );

            Assert.NotNull(edit1);
            Assert.Equal(2, edit1.CurrentVersion);
            _output.WriteLine($"First edit successful. Current version: {edit1.CurrentVersion}");

            // Second edit
            var edit2 = await storage.UpdateMemory(
                memory.Id,
                "version-test",
                "Second edit content",
                "test",
                new[] { "versioning", "test", "edit2" },
                0.85,
                "Versioning Test Memory - Edit 2",
                CancellationToken.None
            );

            Assert.NotNull(edit2);
            Assert.Equal(3, edit2.CurrentVersion);
            _output.WriteLine($"Second edit successful. Current version: {edit2.CurrentVersion}");

            // Third edit
            var edit3 = await storage.UpdateMemory(
                memory.Id,
                "version-test",
                "Third edit content",
                "test",
                new[] { "versioning", "test", "edit3" },
                0.8,
                "Versioning Test Memory - Edit 3",
                CancellationToken.None
            );

            Assert.NotNull(edit3);
            Assert.Equal(4, edit3.CurrentVersion);
            _output.WriteLine($"Third edit successful. Current version: {edit3.CurrentVersion}");

            // Verify version history
            var versions = await storage.GetVersionHistory(memory.Id, null, CancellationToken.None);
            Assert.Equal(3, versions.Count); // v1, v2, v3 snapshots (v4 is current, not snapshotted yet)

            _output.WriteLine($"✓ All edits successful. Version history count: {versions.Count}");
        }
        finally
        {
            // Cleanup
            await storage.Delete(memory.Id, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tests the specific bug scenario: editing a memory that has current_version=1
    /// but NO version 1 snapshot, then editing it again should work without errors.
    /// </summary>
    [Fact]
    public async Task UpdateMemory_PreVersioningMemory_MultipleEdits_ShouldSucceed()
    {
        // Arrange: Create a memory directly without version snapshots
        var memoryId = Guid.NewGuid();
        var embeddingService = _serviceProvider.GetRequiredService<IEmbeddingService>();
        var storage = _serviceProvider.GetRequiredService<IStorage>();

        var contentEmbedding = await embeddingService.Generate("Pre-versioning content", CancellationToken.None);
        var metadataEmbedding = await embeddingService.Generate("Pre-Versioning Multi-Edit Test", CancellationToken.None);

        await using var connection = await _dataSource.OpenConnectionAsync();

        await InsertPreVersioningMemory(
            connection,
            memoryId,
            "pre-versioning-multi-test",
            "Pre-versioning content",
            "test",
            new[] { "test", "multi-edit" },
            1.0,
            "Pre-Versioning Multi-Edit Test",
            contentEmbedding,
            metadataEmbedding);

        _output.WriteLine($"Created pre-versioning memory {memoryId}");

        try
        {
            // Act: First edit - should create version 1 snapshot and update to version 2
            var edit1 = await storage.UpdateMemory(
                memoryId,
                "pre-versioning-multi-test",
                "First edit of pre-versioning memory",
                "test",
                new[] { "test", "multi-edit", "edit1" },
                0.9,
                "Pre-Versioning Multi-Edit Test - Edit 1",
                CancellationToken.None
            );

            Assert.NotNull(edit1);
            Assert.Equal(2, edit1.CurrentVersion);
            _output.WriteLine($"First edit successful. Current version: {edit1.CurrentVersion}");

            // Second edit - should create version 2 snapshot and update to version 3
            // This is where the duplicate key error would occur if the bug exists
            var edit2 = await storage.UpdateMemory(
                memoryId,
                "pre-versioning-multi-test",
                "Second edit of pre-versioning memory",
                "test",
                new[] { "test", "multi-edit", "edit2" },
                0.85,
                "Pre-Versioning Multi-Edit Test - Edit 2",
                CancellationToken.None
            );

            Assert.NotNull(edit2);
            Assert.Equal(3, edit2.CurrentVersion);
            _output.WriteLine($"Second edit successful. Current version: {edit2.CurrentVersion}");

            // Third edit for good measure
            var edit3 = await storage.UpdateMemory(
                memoryId,
                "pre-versioning-multi-test",
                "Third edit of pre-versioning memory",
                "test",
                new[] { "test", "multi-edit", "edit3" },
                0.8,
                "Pre-Versioning Multi-Edit Test - Edit 3",
                CancellationToken.None
            );

            Assert.NotNull(edit3);
            Assert.Equal(4, edit3.CurrentVersion);
            _output.WriteLine($"Third edit successful. Current version: {edit3.CurrentVersion}");

            // Verify version history is correct
            var versions = await storage.GetVersionHistory(memoryId, null, CancellationToken.None);
            Assert.Equal(3, versions.Count);

            // Versions should be 3, 2, 1 (descending order)
            Assert.Equal(3, versions[0].VersionNumber);
            Assert.Equal(2, versions[1].VersionNumber);
            Assert.Equal(1, versions[2].VersionNumber);

            _output.WriteLine($"✓ All edits successful. Version history: {string.Join(", ", versions.Select(v => $"v{v.VersionNumber}"))}");
        }
        finally
        {
            // Cleanup
            await storage.Delete(memoryId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tests revert functionality on pre-versioning memories.
    /// </summary>
    [Fact]
    public async Task RevertToVersion_PreVersioningMemory_ShouldSucceed()
    {
        // Arrange: Create a pre-versioning memory and edit it
        var memoryId = Guid.NewGuid();
        var embeddingService = _serviceProvider.GetRequiredService<IEmbeddingService>();
        var storage = _serviceProvider.GetRequiredService<IStorage>();

        var contentEmbedding = await embeddingService.Generate("Original pre-versioning content for revert test", CancellationToken.None);
        var metadataEmbedding = await embeddingService.Generate("Revert Test Memory", CancellationToken.None);

        await using var connection = await _dataSource.OpenConnectionAsync();

        await InsertPreVersioningMemory(
            connection,
            memoryId,
            "revert-test",
            "Original pre-versioning content for revert test",
            "test",
            new[] { "test", "revert" },
            1.0,
            "Revert Test Memory",
            contentEmbedding,
            metadataEmbedding);

        try
        {
            // Edit the memory to create version history
            var edited = await storage.UpdateMemory(
                memoryId,
                "revert-test",
                "Edited content that we will revert from",
                "test",
                new[] { "test", "revert", "edited" },
                0.9,
                "Revert Test Memory - Edited",
                CancellationToken.None
            );

            Assert.NotNull(edited);
            Assert.Equal(2, edited.CurrentVersion);
            _output.WriteLine($"Edited memory. Current version: {edited.CurrentVersion}");

            // Act: Revert to version 1
            var reverted = await storage.RevertToVersion(memoryId, 1, "test-user", CancellationToken.None);

            // Assert
            Assert.NotNull(reverted);
            Assert.Equal(3, reverted.CurrentVersion); // Revert creates a new version
            Assert.Equal("Original pre-versioning content for revert test", reverted.Text);
            Assert.Equal("Revert Test Memory", reverted.Title);

            _output.WriteLine($"✓ Revert successful. Current version: {reverted.CurrentVersion}, Content restored to original");
        }
        finally
        {
            // Cleanup
            await storage.Delete(memoryId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tests the exact bug scenario: a memory that has current_version=1 AND already has
    /// a version 1 snapshot (from migration). When edited, the code should NOT try to
    /// create another version 1 snapshot.
    ///
    /// This is the scenario where the migration ran and created version snapshots for
    /// existing memories, but then the user tries to edit that memory.
    /// </summary>
    [Fact]
    public async Task UpdateMemory_PostMigrationMemoryWithExistingSnapshot_ShouldNotDuplicateVersion()
    {
        // Arrange: Create a memory that simulates post-migration state:
        // - Memory exists with current_version = 1
        // - Version 1 snapshot already exists (from migration)
        var memoryId = Guid.NewGuid();
        var embeddingService = _serviceProvider.GetRequiredService<IEmbeddingService>();
        var storage = _serviceProvider.GetRequiredService<IStorage>();

        var contentEmbedding = await embeddingService.Generate("Post-migration content", CancellationToken.None);
        var metadataEmbedding = await embeddingService.Generate("Post-Migration Test Memory", CancellationToken.None);

        await using var connection = await _dataSource.OpenConnectionAsync();

        // Step 1: Insert the memory with current_version = 1
        await InsertPreVersioningMemory(
            connection,
            memoryId,
            "post-migration-test",
            "Post-migration content",
            "test",
            new[] { "test", "post-migration" },
            1.0,
            "Post-Migration Test Memory",
            contentEmbedding,
            metadataEmbedding);

        // Step 2: Simulate what the migration does - create a version 1 snapshot
        const string insertVersionSql = @"
            INSERT INTO memory_versions (memory_id, version_number, type, content, text, source, tags, confidence, title, relationship_ids, created_at)
            VALUES (@memoryId, 1, @type, @content, @text, @source, @tags, @confidence, @title, @relationshipIds, @createdAt)";

        await using var versionCmd = new NpgsqlCommand(insertVersionSql, connection);
        versionCmd.Parameters.AddWithValue("memoryId", memoryId);
        versionCmd.Parameters.AddWithValue("type", "post-migration-test");
        versionCmd.Parameters.AddWithValue("content", JsonDocument.Parse("{}"));
        versionCmd.Parameters.AddWithValue("text", "Post-migration content");
        versionCmd.Parameters.AddWithValue("source", "test");
        versionCmd.Parameters.AddWithValue("tags", new[] { "test", "post-migration" });
        versionCmd.Parameters.AddWithValue("confidence", 1.0);
        versionCmd.Parameters.AddWithValue("title", "Post-Migration Test Memory");
        versionCmd.Parameters.AddWithValue("relationshipIds", Array.Empty<Guid>());
        versionCmd.Parameters.AddWithValue("createdAt", DateTime.UtcNow);
        await versionCmd.ExecuteNonQueryAsync();

        // Step 3: Also create the memory_created event (like migration does)
        const string insertEventSql = @"
            INSERT INTO memory_events (memory_id, version_number, event_type, event_data, timestamp, changed_by)
            VALUES (@memoryId, 1, 'memory_created', @eventData, @timestamp, 'migration')";

        await using var eventCmd = new NpgsqlCommand(insertEventSql, connection);
        eventCmd.Parameters.AddWithValue("memoryId", memoryId);
        eventCmd.Parameters.AddWithValue("eventData", JsonDocument.Parse("{\"migration\": true}"));
        eventCmd.Parameters.AddWithValue("timestamp", DateTime.UtcNow);
        await eventCmd.ExecuteNonQueryAsync();

        // Verify setup: version 1 snapshot exists
        await using var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM memory_versions WHERE memory_id = @memoryId AND version_number = 1", connection);
        countCmd.Parameters.AddWithValue("memoryId", memoryId);
        var versionCount = (long)(await countCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, versionCount);

        _output.WriteLine($"Created post-migration memory {memoryId} WITH version 1 snapshot (simulating migration state)");

        try
        {
            // Act: Try to update the memory - this is where duplicate key error would occur
            // if the code doesn't check for existing version snapshot before creating one
            var updatedMemory = await storage.UpdateMemory(
                memoryId,
                "post-migration-test-updated",
                "Edited content after migration",
                "test",
                new[] { "test", "post-migration", "edited" },
                0.95,
                "Post-Migration Test Memory - Edited",
                CancellationToken.None
            );

            // Assert: Update should succeed without duplicate key error
            Assert.NotNull(updatedMemory);
            Assert.Equal(memoryId, updatedMemory.Id);
            Assert.Equal("Edited content after migration", updatedMemory.Text);
            Assert.Equal(2, updatedMemory.CurrentVersion);

            // Verify version history now has v1 (from migration) and v2 event
            var versions = await storage.GetVersionHistory(memoryId, null, CancellationToken.None);

            // Should have version 1 (from migration) - might also have version 2 depending on implementation
            Assert.True(versions.Count >= 1);
            Assert.Contains(versions, v => v.VersionNumber == 1);

            _output.WriteLine($"✓ Successfully updated post-migration memory without duplicate key error. New version: {updatedMemory.CurrentVersion}");
        }
        finally
        {
            // Cleanup
            await storage.Delete(memoryId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tests that version stats correctly account for pre-versioning memories.
    /// </summary>
    [Fact]
    public async Task GetVersionStats_WithPreVersioningMemories_ShouldReturnAccurateStats()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();
        var embeddingService = _serviceProvider.GetRequiredService<IEmbeddingService>();

        // Create a normal memory (will have version snapshots after edits)
        var normalMemory = await storage.StoreMemory(
            "stats-test-normal",
            "Normal memory content",
            "test",
            new[] { "stats-test" },
            1.0,
            "Normal Stats Test Memory"
        );

        // Create a pre-versioning memory directly
        var preVersioningId = Guid.NewGuid();
        var contentEmbedding = await embeddingService.Generate("Pre-versioning stats test", CancellationToken.None);
        var metadataEmbedding = await embeddingService.Generate("Pre-Versioning Stats Test", CancellationToken.None);

        await using var connection = await _dataSource.OpenConnectionAsync();

        await InsertPreVersioningMemory(
            connection,
            preVersioningId,
            "stats-test-pre",
            "Pre-versioning stats test",
            "test",
            new[] { "stats-test" },
            1.0,
            "Pre-Versioning Stats Test",
            contentEmbedding,
            metadataEmbedding);

        try
        {
            // Edit both memories
            await storage.UpdateMemory(
                normalMemory.Id,
                "stats-test-normal",
                "Normal memory edited",
                "test",
                new[] { "stats-test", "edited" },
                0.9,
                "Normal Stats Test Memory - Edited",
                CancellationToken.None
            );

            await storage.UpdateMemory(
                preVersioningId,
                "stats-test-pre",
                "Pre-versioning memory edited",
                "test",
                new[] { "stats-test", "edited" },
                0.9,
                "Pre-Versioning Stats Test - Edited",
                CancellationToken.None
            );

            // Act
            var stats = await storage.GetVersionStats(CancellationToken.None);

            // Assert
            Assert.True(stats.TotalMemories >= 2);
            Assert.True(stats.TotalVersions >= 2); // At least 2 version snapshots from our edits

            _output.WriteLine($"✓ Version stats: {stats.TotalMemories} memories, {stats.TotalVersions} versions, {stats.TotalEvents} events");
        }
        finally
        {
            // Cleanup
            await storage.Delete(normalMemory.Id, CancellationToken.None);
            await storage.Delete(preVersioningId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tests the "revert the revert" scenario: after reverting from v2 to v1,
    /// the v2 snapshot should be preserved and you should be able to revert back to v2.
    /// This verifies the fix for the bug where the pre-revert state was not being saved correctly.
    /// </summary>
    [Fact]
    public async Task RevertToVersion_ShouldPreservePreRevertState_AllowingRevertTheRevert()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();

        // Step 1: Create a memory (v1)
        var memory = await storage.StoreMemory(
            "revert-test",
            "Version 1 content - the original",
            "test",
            new[] { "v1", "original" },
            1.0,
            "Revert Test V1"
        );
        var memoryId = memory.Id;
        _output.WriteLine($"Created memory at v{memory.CurrentVersion}: '{memory.Text}'");

        try
        {
            // Step 2: Edit to create v2
            var v2 = await storage.UpdateMemory(
                memoryId,
                "revert-test",
                "Version 2 content - the edited version",
                "test",
                new[] { "v2", "edited" },
                0.9,
                "Revert Test V2",
                CancellationToken.None
            );
            Assert.NotNull(v2);
            Assert.Equal(2, v2.CurrentVersion);
            _output.WriteLine($"Edited memory to v{v2.CurrentVersion}: '{v2.Text}'");

            // Step 3: Revert to v1 (creates v3 with v1's content)
            var v3 = await storage.RevertToVersion(memoryId, 1, "test-user", CancellationToken.None);
            Assert.NotNull(v3);
            Assert.Equal(3, v3.CurrentVersion);
            Assert.Equal("Version 1 content - the original", v3.Text);
            _output.WriteLine($"Reverted to v1, now at v{v3.CurrentVersion}: '{v3.Text}'");

            // Step 4: Verify v2 snapshot was preserved
            var versions = await storage.GetVersionHistory(memoryId, null, CancellationToken.None);
            _output.WriteLine($"Version history has {versions.Count} versions:");
            foreach (var v in versions)
            {
                _output.WriteLine($"  - v{v.VersionNumber}: '{v.Text?.Substring(0, Math.Min(50, v.Text?.Length ?? 0))}...'");
            }

            // v2 should exist in the history with v2's content (NOT v1's content)
            var v2Snapshot = versions.FirstOrDefault(v => v.VersionNumber == 2);
            Assert.NotNull(v2Snapshot);
            Assert.Equal("Version 2 content - the edited version", v2Snapshot.Text);
            _output.WriteLine($"✓ v2 snapshot correctly preserved with content: '{v2Snapshot.Text}'");

            // Step 5: THE KEY TEST - Revert back to v2 ("revert the revert")
            var v4 = await storage.RevertToVersion(memoryId, 2, "test-user", CancellationToken.None);
            Assert.NotNull(v4);
            Assert.Equal(4, v4.CurrentVersion);
            Assert.Equal("Version 2 content - the edited version", v4.Text);
            Assert.Equal("Revert Test V2", v4.Title);
            _output.WriteLine($"✓ Successfully reverted to v2, now at v{v4.CurrentVersion}: '{v4.Text}'");

            // Step 6: Verify v3 was also preserved (the state before the second revert)
            var finalVersions = await storage.GetVersionHistory(memoryId, null, CancellationToken.None);
            var v3Snapshot = finalVersions.FirstOrDefault(v => v.VersionNumber == 3);
            Assert.NotNull(v3Snapshot);
            Assert.Equal("Version 1 content - the original", v3Snapshot.Text);
            _output.WriteLine($"✓ v3 snapshot correctly preserved with content: '{v3Snapshot.Text}'");

            _output.WriteLine($"✓ Full version chain: v1 -> edit -> v2 -> revert to v1 -> v3 -> revert to v2 -> v4");
        }
        finally
        {
            // Cleanup
            await storage.Delete(memoryId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tests that old versions are automatically pruned when MaxVersionsPerMemory is exceeded.
    /// This tests the inline auto-pruning feature that deletes oldest versions (FIFO) when
    /// the version count exceeds the configured limit.
    /// </summary>
    [Fact]
    public async Task UpdateMemory_WhenExceedingMaxVersions_ShouldAutoPruneOldestVersions()
    {
        // Arrange: Create a service provider with a small MaxVersionsPerMemory limit
        var services = new ServiceCollection();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                ["Embeddings:Model"] = "all-minilm",
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString(),
                // Set a small max versions limit for testing
                ["Versioning:MaxVersionsPerMemory"] = "3"
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
        {
            client.BaseAddress = new Uri(_fixture.OllamaApiUrl);
            client.Timeout = TimeSpan.FromMinutes(1);
        });
        services.AddSingleton(new EmbeddingSettings
        {
            ApiUrl = new Uri(_fixture.OllamaApiUrl),
            Model = "all-minilm",
            Timeout = TimeSpan.FromMinutes(1)
        });
        services.AddMemorizer();
        services.AddLogging();

        var testServiceProvider = services.BuildServiceProvider();
        var storage = testServiceProvider.GetRequiredService<IStorage>();

        try
        {
            // Create a memory (v1)
            var memory = await storage.StoreMemory(
                "prune-test",
                "Version 1 content",
                "test",
                new[] { "prune-test" },
                1.0,
                "Prune Test V1"
            );
            var memoryId = memory.Id;
            _output.WriteLine($"Created memory at v{memory.CurrentVersion}");

            // Edit to create v2 (snapshot of v1 is created)
            var v2 = await storage.UpdateMemory(
                memoryId,
                "prune-test",
                "Version 2 content",
                "test",
                new[] { "prune-test" },
                0.9,
                "Prune Test V2",
                CancellationToken.None
            );
            Assert.Equal(2, v2!.CurrentVersion);
            _output.WriteLine($"Edited to v{v2.CurrentVersion}");

            // Edit to create v3 (snapshot of v2 is created)
            var v3 = await storage.UpdateMemory(
                memoryId,
                "prune-test",
                "Version 3 content",
                "test",
                new[] { "prune-test" },
                0.8,
                "Prune Test V3",
                CancellationToken.None
            );
            Assert.Equal(3, v3!.CurrentVersion);
            _output.WriteLine($"Edited to v{v3.CurrentVersion}");

            // Edit to create v4 (snapshot of v3 is created)
            var v4 = await storage.UpdateMemory(
                memoryId,
                "prune-test",
                "Version 4 content",
                "test",
                new[] { "prune-test" },
                0.7,
                "Prune Test V4",
                CancellationToken.None
            );
            Assert.Equal(4, v4!.CurrentVersion);
            _output.WriteLine($"Edited to v{v4.CurrentVersion}");

            // At this point we have v1, v2, v3 snapshots. Max is 3, so no pruning yet.
            var versionsAfter4 = await storage.GetVersionHistory(memoryId, null, CancellationToken.None);
            _output.WriteLine($"After v4: {versionsAfter4.Count} versions in history");
            Assert.Equal(3, versionsAfter4.Count);

            // Edit to create v5 (snapshot of v4 is created, should prune v1)
            var v5 = await storage.UpdateMemory(
                memoryId,
                "prune-test",
                "Version 5 content",
                "test",
                new[] { "prune-test" },
                0.6,
                "Prune Test V5",
                CancellationToken.None
            );
            Assert.Equal(5, v5!.CurrentVersion);
            _output.WriteLine($"Edited to v{v5.CurrentVersion}");

            // Verify auto-pruning: should still have only 3 versions (v2, v3, v4 - v1 was pruned)
            var versionsAfter5 = await storage.GetVersionHistory(memoryId, null, CancellationToken.None);
            _output.WriteLine($"After v5: {versionsAfter5.Count} versions in history");
            foreach (var v in versionsAfter5)
            {
                _output.WriteLine($"  - v{v.VersionNumber}: '{v.Text}'");
            }

            Assert.Equal(3, versionsAfter5.Count);
            // v1 should be pruned, remaining versions should be v4, v3, v2 (descending order)
            Assert.DoesNotContain(versionsAfter5, v => v.VersionNumber == 1);
            Assert.Contains(versionsAfter5, v => v.VersionNumber == 2);
            Assert.Contains(versionsAfter5, v => v.VersionNumber == 3);
            Assert.Contains(versionsAfter5, v => v.VersionNumber == 4);

            _output.WriteLine($"✓ Auto-pruning working: v1 was automatically pruned when exceeding max versions");

            // Edit to create v6 (should prune v2)
            var v6 = await storage.UpdateMemory(
                memoryId,
                "prune-test",
                "Version 6 content",
                "test",
                new[] { "prune-test" },
                0.5,
                "Prune Test V6",
                CancellationToken.None
            );
            Assert.Equal(6, v6!.CurrentVersion);

            var versionsAfter6 = await storage.GetVersionHistory(memoryId, null, CancellationToken.None);
            Assert.Equal(3, versionsAfter6.Count);
            Assert.DoesNotContain(versionsAfter6, v => v.VersionNumber == 1);
            Assert.DoesNotContain(versionsAfter6, v => v.VersionNumber == 2);
            Assert.Contains(versionsAfter6, v => v.VersionNumber == 3);
            Assert.Contains(versionsAfter6, v => v.VersionNumber == 4);
            Assert.Contains(versionsAfter6, v => v.VersionNumber == 5);

            _output.WriteLine($"✓ Auto-pruning continues: v2 was automatically pruned");

            // Cleanup
            await storage.Delete(memoryId, CancellationToken.None);
        }
        finally
        {
            (testServiceProvider as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Tests that auto-pruning is disabled when MaxVersionsPerMemory is set to 0 or negative.
    /// </summary>
    [Fact]
    public async Task UpdateMemory_WhenMaxVersionsDisabled_ShouldNotPrune()
    {
        // Arrange: Create a service provider with pruning disabled (MaxVersionsPerMemory = 0)
        var services = new ServiceCollection();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                ["Embeddings:Model"] = "all-minilm",
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString(),
                // Disable pruning
                ["Versioning:MaxVersionsPerMemory"] = "0"
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
        {
            client.BaseAddress = new Uri(_fixture.OllamaApiUrl);
            client.Timeout = TimeSpan.FromMinutes(1);
        });
        services.AddSingleton(new EmbeddingSettings
        {
            ApiUrl = new Uri(_fixture.OllamaApiUrl),
            Model = "all-minilm",
            Timeout = TimeSpan.FromMinutes(1)
        });
        services.AddMemorizer();
        services.AddLogging();

        var testServiceProvider = services.BuildServiceProvider();
        var storage = testServiceProvider.GetRequiredService<IStorage>();

        try
        {
            // Create a memory and edit it 5 times
            var memory = await storage.StoreMemory(
                "no-prune-test",
                "Version 1",
                "test",
                new[] { "no-prune-test" },
                1.0,
                "No Prune Test"
            );
            var memoryId = memory.Id;

            for (int i = 2; i <= 6; i++)
            {
                await storage.UpdateMemory(
                    memoryId,
                    "no-prune-test",
                    $"Version {i}",
                    "test",
                    new[] { "no-prune-test" },
                    1.0 - (i * 0.1),
                    $"No Prune Test V{i}",
                    CancellationToken.None
                );
            }

            // Verify all 5 version snapshots exist (v1 through v5, v6 is current)
            var versions = await storage.GetVersionHistory(memoryId, null, CancellationToken.None);
            _output.WriteLine($"Versions with pruning disabled: {versions.Count}");

            Assert.Equal(5, versions.Count);
            for (int i = 1; i <= 5; i++)
            {
                Assert.Contains(versions, v => v.VersionNumber == i);
            }

            _output.WriteLine($"✓ No pruning when MaxVersionsPerMemory is 0: all {versions.Count} versions retained");

            // Cleanup
            await storage.Delete(memoryId, CancellationToken.None);
        }
        finally
        {
            (testServiceProvider as IDisposable)?.Dispose();
        }
    }

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        _dataSource?.Dispose();
    }
}
