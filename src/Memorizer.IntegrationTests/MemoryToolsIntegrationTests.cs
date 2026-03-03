using Memorizer.Extensions;
using Memorizer.IntegrationTests.Logging;
using Memorizer.Models;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PostgMem.Tools;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for the MCP Memory Tools - Edit, Store, UpdateMetadata, etc.
/// These tests verify the tools work correctly for common agent use cases.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MemoryToolsIntegrationTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly IServiceProvider _services;

    public void Dispose()
    {
        (_services as IDisposable)?.Dispose();
    }

    public MemoryToolsIntegrationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _services = CreateServices();
    }

    private IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                ["Embeddings:Model"] = "all-minilm",
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString()
            })
            .Build());

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
        services.AddLogging(builder => builder.AddXUnit(_output));

        // Register MemoryTools
        services.AddScoped<MemoryTools>();

        return services.BuildServiceProvider();
    }

    #region Edit Tool Tests

    [Fact]
    public async Task Edit_CanCheckOffTodoItem_InMarkdownList()
    {
        // Arrange - This is the key scenario: checking off a to-do item in markdown
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var todoListContent = @"# My To-Do List

- [ ] Buy groceries
- [ ] Walk the dog
- [ ] Write integration tests
- [ ] Review pull request";

        var memory = await storage.StoreMemory(
            "todo-list",
            todoListContent,
            "user",
            new[] { "todo", "daily-tasks" },
            new Confidence(1.0),
            "Daily To-Do List");

        _output.WriteLine($"Created to-do list memory: {memory.Id}");
        _output.WriteLine($"Original content:\n{memory.Text}");

        // Act - Check off "Write integration tests"
        var result = await tools.Edit(
            memory.Id.Value,
            "- [ ] Write integration tests",
            "- [x] Write integration tests",
            replace_all: false);

        _output.WriteLine($"Edit result: {result}");

        // Assert
        Assert.Contains("Edit successful", result);
        Assert.Contains("1 replacement(s)", result);

        // Verify the change persisted
        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.Contains("- [x] Write integration tests", updatedMemory.Text);
        Assert.Contains("- [ ] Buy groceries", updatedMemory.Text); // Other items unchanged
        Assert.Contains("- [ ] Walk the dog", updatedMemory.Text);
        Assert.Contains("- [ ] Review pull request", updatedMemory.Text);
        Assert.Equal(new VersionNumber(2), updatedMemory.CurrentVersion); // Version incremented after edit

        _output.WriteLine($"Updated content:\n{updatedMemory.Text}");
        _output.WriteLine($"New version: {updatedMemory.CurrentVersion}");
    }

    [Fact]
    public async Task Edit_CanReplaceMultipleOccurrences_WithReplaceAll()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var content = @"Use foo for initialization.
The foo function takes no arguments.
Call foo() to start the process.";

        var memory = await storage.StoreMemory(
            "documentation",
            content,
            "user",
            new[] { "docs" },
            new Confidence(1.0),
            "Foo Documentation");

        _output.WriteLine($"Created memory: {memory.Id}");

        // Act - Replace all occurrences of "foo" with "initialize"
        var result = await tools.Edit(
            memory.Id.Value,
            "foo",
            "initialize",
            replace_all: true);

        _output.WriteLine($"Edit result: {result}");

        // Assert
        Assert.Contains("Edit successful", result);
        Assert.Contains("3 replacement(s)", result);

        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.DoesNotContain("foo", updatedMemory.Text);
        Assert.Contains("initialize", updatedMemory.Text);

        _output.WriteLine($"Updated content:\n{updatedMemory.Text}");
    }

    [Fact]
    public async Task Edit_ReplacesOnlyFirstOccurrence_ByDefault()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var content = "apple, apple, apple";

        var memory = await storage.StoreMemory(
            "test",
            content,
            "user",
            null,
            new Confidence(1.0),
            "Apple Test");

        // Act - Replace without replace_all (defaults to false)
        var result = await tools.Edit(
            memory.Id.Value,
            "apple",
            "orange");

        _output.WriteLine($"Edit result: {result}");

        // Assert
        Assert.Contains("1 replacement(s)", result);

        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.Equal("orange, apple, apple", updatedMemory.Text);

        _output.WriteLine($"Updated content: {updatedMemory.Text}");
    }

    [Fact]
    public async Task Edit_FailsWithHelpfulError_WhenOldTextNotFound()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "test",
            "Hello World",
            "user",
            null,
            new Confidence(1.0),
            "Test Memory");

        // Act - Try to replace text that doesn't exist
        var result = await tools.Edit(
            memory.Id.Value,
            "Goodbye World",
            "New Text");

        _output.WriteLine($"Edit result: {result}");

        // Assert - Should get helpful error
        Assert.Contains("Edit failed", result);
        Assert.Contains("old_text was not found", result);
        Assert.Contains("Hello World", result); // Shows preview
        Assert.Contains("Tip: Use Get tool first", result); // Helpful tip
    }

    [Fact]
    public async Task Edit_FailsGracefully_WhenMemoryNotFound()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await tools.Edit(
            nonExistentId,
            "old text",
            "new text");

        _output.WriteLine($"Edit result: {result}");

        // Assert
        Assert.Contains("not found", result);
        Assert.Contains(nonExistentId.ToString(), result);
    }

    [Fact]
    public async Task Edit_HandlesMultiLineReplacements()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var content = @"# Header

Old paragraph here
with multiple lines
that should be replaced.

# Footer";

        var memory = await storage.StoreMemory(
            "document",
            content,
            "user",
            null,
            new Confidence(1.0),
            "Multi-line Test");

        // Act - Replace multi-line section
        var result = await tools.Edit(
            memory.Id.Value,
            @"Old paragraph here
with multiple lines
that should be replaced.",
            "New single line paragraph.");

        _output.WriteLine($"Edit result: {result}");

        // Assert
        Assert.Contains("Edit successful", result);

        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.Contains("# Header", updatedMemory.Text);
        Assert.Contains("New single line paragraph.", updatedMemory.Text);
        Assert.Contains("# Footer", updatedMemory.Text);
        Assert.DoesNotContain("Old paragraph here", updatedMemory.Text);

        _output.WriteLine($"Updated content:\n{updatedMemory.Text}");
    }

    [Fact]
    public async Task Edit_CreatesVersionHistory()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "test",
            "Version 1 content",
            "user",
            null,
            new Confidence(1.0),
            "Version Test");

        // Act - Make multiple edits
        await tools.Edit(memory.Id.Value, "Version 1", "Version 2");
        await tools.Edit(memory.Id.Value, "Version 2", "Version 3");
        await tools.Edit(memory.Id.Value, "Version 3", "Version 4");

        // Assert - Check version history
        // Each edit creates a snapshot of the PREVIOUS state, so 3 edits = 3 version snapshots
        var versions = await storage.GetVersionHistory(memory.Id, 10);
        Assert.Equal(3, versions.Count); // 3 edits create 3 snapshots of previous states

        var finalMemory = await storage.Get(memory.Id);
        Assert.NotNull(finalMemory);
        Assert.Equal("Version 4 content", finalMemory.Text);
        Assert.Equal(new VersionNumber(4), finalMemory.CurrentVersion); // current_version is incremented on each edit

        _output.WriteLine($"Final version: {finalMemory.CurrentVersion}");
        _output.WriteLine($"Version history count: {versions.Count}");
    }

    [Fact]
    public async Task Edit_ReturnsNoChange_WhenReplacementIsIdentical()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "test",
            "Same content",
            "user",
            null,
            new Confidence(1.0),
            "Identity Test");

        // Act - Try to replace with identical text
        var result = await tools.Edit(
            memory.Id.Value,
            "Same",
            "Same");

        _output.WriteLine($"Edit result: {result}");

        // Assert
        Assert.Contains("No changes made", result);

        // Version should not increment
        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.Equal(new VersionNumber(1), updatedMemory.CurrentVersion);
    }

    #endregion

    #region UpdateMetadata Tests

    [Fact]
    public async Task UpdateMetadata_CanUpdateTitle()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "test",
            "Content stays the same",
            "user",
            null,
            new Confidence(1.0),
            "Original Title");

        // Act
        var result = await tools.UpdateMetadata(
            memory.Id.Value,
            title: "Updated Title");

        _output.WriteLine($"UpdateMetadata result: {result}");

        // Assert
        Assert.Contains("Metadata updated successfully", result);
        Assert.Contains("title='Updated Title'", result);

        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.Equal("Updated Title", updatedMemory.Title);
        Assert.Equal("Content stays the same", updatedMemory.Text); // Content unchanged
        Assert.Equal(new VersionNumber(2), updatedMemory.CurrentVersion); // Version incremented after metadata update
    }

    [Fact]
    public async Task UpdateMetadata_CanUpdateTags()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "test",
            "Content",
            "user",
            new[] { "old-tag" },
            new Confidence(1.0),
            "Tag Test");

        // Act
        var result = await tools.UpdateMetadata(
            memory.Id.Value,
            tags: new[] { "new-tag-1", "new-tag-2" });

        _output.WriteLine($"UpdateMetadata result: {result}");

        // Assert
        Assert.Contains("Metadata updated successfully", result);
        Assert.Contains("tags=", result);

        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.NotNull(updatedMemory.Tags);
        Assert.Contains("new-tag-1", updatedMemory.Tags);
        Assert.Contains("new-tag-2", updatedMemory.Tags);
        Assert.DoesNotContain("old-tag", updatedMemory.Tags);
    }

    [Fact]
    public async Task UpdateMetadata_CanUpdateType()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "draft",
            "Content",
            "user",
            null,
            new Confidence(1.0),
            "Type Test");

        // Act
        var result = await tools.UpdateMetadata(
            memory.Id.Value,
            type: "published");

        _output.WriteLine($"UpdateMetadata result: {result}");

        // Assert
        Assert.Contains("type='published'", result);

        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.Equal("published", updatedMemory.Type);
    }

    [Fact]
    public async Task UpdateMetadata_CanUpdateConfidence()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "test",
            "Content",
            "user",
            null,
            new Confidence(0.5),
            "Confidence Test");

        // Act
        var result = await tools.UpdateMetadata(
            memory.Id.Value,
            confidence: 0.95);

        _output.WriteLine($"UpdateMetadata result: {result}");

        // Assert
        Assert.Contains("confidence=0.95", result);

        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.Equal(new Confidence(0.95), updatedMemory.Confidence);
    }

    [Fact]
    public async Task UpdateMetadata_CanUpdateMultipleFields()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "draft",
            "Content",
            "user",
            new[] { "initial" },
            new Confidence(0.5),
            "Initial Title");

        // Act - Update everything at once
        var result = await tools.UpdateMetadata(
            memory.Id.Value,
            title: "Final Title",
            type: "reference",
            tags: new[] { "final", "tested" },
            confidence: 1.0);

        _output.WriteLine($"UpdateMetadata result: {result}");

        // Assert
        Assert.Contains("title='Final Title'", result);
        Assert.Contains("type='reference'", result);
        Assert.Contains("tags=", result);
        Assert.Contains("confidence=1.00", result);

        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.Equal("Final Title", updatedMemory.Title);
        Assert.Equal("reference", updatedMemory.Type);
        Assert.NotNull(updatedMemory.Tags);
        Assert.Contains("final", updatedMemory.Tags);
        Assert.Equal(new Confidence(1.0), updatedMemory.Confidence);
    }

    [Fact]
    public async Task UpdateMetadata_PreservesUnchangedFields()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        var memory = await storage.StoreMemory(
            "important-type",
            "Important content",
            "user",
            new[] { "critical", "production" },
            new Confidence(0.99),
            "Important Memory");

        // Act - Only update title, everything else should be preserved
        var result = await tools.UpdateMetadata(
            memory.Id.Value,
            title: "Updated Important Memory");

        _output.WriteLine($"UpdateMetadata result: {result}");

        // Assert - Other fields preserved
        var updatedMemory = await storage.Get(memory.Id);
        Assert.NotNull(updatedMemory);
        Assert.Equal("Updated Important Memory", updatedMemory.Title);
        Assert.Equal("important-type", updatedMemory.Type);
        Assert.NotNull(updatedMemory.Tags);
        Assert.Contains("critical", updatedMemory.Tags);
        Assert.Contains("production", updatedMemory.Tags);
        Assert.Equal(new Confidence(0.99), updatedMemory.Confidence);
        Assert.Equal("Important content", updatedMemory.Text);
    }

    [Fact]
    public async Task UpdateMetadata_FailsGracefully_WhenMemoryNotFound()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await tools.UpdateMetadata(
            nonExistentId,
            title: "New Title");

        _output.WriteLine($"UpdateMetadata result: {result}");

        // Assert
        Assert.Contains("not found", result);
        Assert.Contains(nonExistentId.ToString(), result);
    }

    #endregion

    #region Store Tool Tests

    [Fact]
    public async Task Store_CreatesNewMemory_WithAllFields()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        // Act
        var result = await tools.Store(
            type: "reference",
            text: "This is a test memory created by Store tool",
            source: "integration-test",
            title: "Store Test Memory",
            tags: new[] { "test", "integration" },
            confidence: 0.95);

        _output.WriteLine($"Store result: {result}");

        // Assert
        Assert.Contains("Memory stored successfully", result);
        Assert.Contains("ID:", result);

        // Extract ID from result and verify
        var idMatch = System.Text.RegularExpressions.Regex.Match(result, @"ID: ([a-f0-9-]+)");
        Assert.True(idMatch.Success, "Should contain memory ID");
        var memoryId = Guid.Parse(idMatch.Groups[1].Value);

        var storedMemory = await storage.Get((MemoryId)memoryId);
        Assert.NotNull(storedMemory);
        Assert.Equal("reference", storedMemory.Type);
        Assert.Equal("This is a test memory created by Store tool", storedMemory.Text);
        Assert.Equal("integration-test", storedMemory.Source);
        Assert.Equal("Store Test Memory", storedMemory.Title);
        Assert.NotNull(storedMemory.Tags);
        Assert.Contains("test", storedMemory.Tags);
        Assert.Equal(new Confidence(0.95), storedMemory.Confidence);
        Assert.Equal(new VersionNumber(1), storedMemory.CurrentVersion);
    }

    [Fact]
    public async Task Store_CreatesRelationship_WhenSpecified()
    {
        // Arrange
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        // Create first memory
        var firstMemory = await storage.StoreMemory(
            "concept",
            "This is the main concept",
            "test",
            null,
            new Confidence(1.0),
            "Main Concept");

        // Act - Create related memory with relationship
        var result = await tools.Store(
            type: "example",
            text: "This is an example of the concept",
            source: "test",
            title: "Concept Example",
            relatedTo: firstMemory.Id.Value.ToString(),
            relationshipType: "example-of");

        _output.WriteLine($"Store result: {result}");

        // Assert
        Assert.Contains("Memory stored successfully", result);

        // Verify relationship was created
        var idMatch = System.Text.RegularExpressions.Regex.Match(result, @"ID: ([a-f0-9-]+)");
        Assert.True(idMatch.Success);
        var newMemoryId = (MemoryId)Guid.Parse(idMatch.Groups[1].Value);

        var newMemory = await storage.Get(newMemoryId);
        Assert.NotNull(newMemory);
        Assert.NotNull(newMemory.Relationships);
        Assert.Contains(newMemory.Relationships, r =>
            r.Type == "example-of" &&
            (r.FromMemoryId == newMemoryId || r.ToMemoryId == newMemoryId));
    }

    #endregion

    #region Combined Workflow Tests

    [Fact]
    public async Task AgentWorkflow_CreateTodoList_ThenCheckOffItems()
    {
        // This test simulates a realistic agent workflow:
        // 1. Agent creates a to-do list
        // 2. Agent checks off items one by one
        // 3. All changes are versioned for rollback

        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        // Step 1: Agent creates a to-do list
        var createResult = await tools.Store(
            type: "todo-list",
            text: @"# Sprint Tasks

- [ ] Design API endpoints
- [ ] Implement storage layer
- [ ] Write unit tests
- [ ] Write integration tests
- [ ] Code review",
            source: "LLM",
            title: "Sprint 42 Tasks",
            tags: new[] { "sprint-42", "todo" });

        _output.WriteLine($"Create result: {createResult}");
        Assert.Contains("Memory stored successfully", createResult);

        var idMatch = System.Text.RegularExpressions.Regex.Match(createResult, @"ID: ([a-f0-9-]+)");
        var memoryId = Guid.Parse(idMatch.Groups[1].Value);

        // Step 2: Agent completes tasks one by one
        var tasks = new[]
        {
            ("- [ ] Design API endpoints", "- [x] Design API endpoints"),
            ("- [ ] Implement storage layer", "- [x] Implement storage layer"),
            ("- [ ] Write unit tests", "- [x] Write unit tests"),
        };

        foreach (var (oldText, newText) in tasks)
        {
            var editResult = await tools.Edit(memoryId, oldText, newText);
            _output.WriteLine($"Edit '{oldText}' -> {editResult}");
            Assert.Contains("Edit successful", editResult);
        }

        // Step 3: Verify final state
        var finalMemory = await storage.Get((MemoryId)memoryId);
        Assert.NotNull(finalMemory);

        _output.WriteLine($"\nFinal to-do list:\n{finalMemory.Text}");

        Assert.Contains("- [x] Design API endpoints", finalMemory.Text);
        Assert.Contains("- [x] Implement storage layer", finalMemory.Text);
        Assert.Contains("- [x] Write unit tests", finalMemory.Text);
        Assert.Contains("- [ ] Write integration tests", finalMemory.Text); // Not done yet
        Assert.Contains("- [ ] Code review", finalMemory.Text); // Not done yet

        // Verify version history exists for rollback
        // Each edit creates a snapshot of the PREVIOUS state, so 3 edits = 3 version snapshots
        var versions = await storage.GetVersionHistory(finalMemory.Id, 10);
        Assert.Equal(3, versions.Count); // 3 edits means 3 snapshots of previous states
        _output.WriteLine($"\nVersion history: {versions.Count} versions");
    }

    [Fact]
    public async Task AgentWorkflow_EditThenRevert()
    {
        // Test that an agent can make changes and then revert if needed
        var tools = _services.GetRequiredService<MemoryTools>();
        var storage = _services.GetRequiredService<IStorage>();

        // Create initial content
        var createResult = await tools.Store(
            type: "document",
            text: "This is the original correct content.",
            source: "user",
            title: "Important Document");

        var idMatch = System.Text.RegularExpressions.Regex.Match(createResult, @"ID: ([a-f0-9-]+)");
        var memoryId = Guid.Parse(idMatch.Groups[1].Value);

        // Make a mistake
        await tools.Edit(memoryId, "original correct", "wrong");

        var wrongMemory = await storage.Get((MemoryId)memoryId);
        Assert.NotNull(wrongMemory);
        Assert.Contains("wrong content", wrongMemory.Text);
        Assert.Equal(new VersionNumber(2), wrongMemory.CurrentVersion);

        // Revert to original
        var revertResult = await tools.RevertToVersion(memoryId, new VersionNumber(1));
        _output.WriteLine($"Revert result: {revertResult}");

        Assert.Contains("successfully reverted", revertResult);

        // Verify content is restored
        var revertedMemory = await storage.Get((MemoryId)memoryId);
        Assert.NotNull(revertedMemory);
        Assert.Contains("original correct", revertedMemory.Text);
        Assert.Equal(new VersionNumber(3), revertedMemory.CurrentVersion); // Revert creates new version
    }

    #endregion
}
