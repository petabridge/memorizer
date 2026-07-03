using Memorizer.Extensions;
using Memorizer.IntegrationTests.Logging;
using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for MCP tools project scoping functionality.
/// Tests the new projectId, owner, and archetype parameters in Store and Search.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class McpToolsProjectScopingTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly IServiceProvider _services;

    public void Dispose()
    {
        (_services as IDisposable)?.Dispose();
    }

    public McpToolsProjectScopingTests(IntegrationTestFixture fixture, ITestOutputHelper output)
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

        return services.BuildServiceProvider();
    }

    #region StoreMemory with Owner and Archetype Tests

    [Fact]
    public async Task StoreMemory_WithProjectOwner_AssignsCorrectly()
    {
        var storage = _services.GetRequiredService<IStorage>();

        // Create a workspace and project first
        var workspace = await storage.CreateWorkspaceAsync("Test Workspace", "For testing", cancellationToken: default);
        var project = await storage.CreateProjectAsync(workspace.Id, "Test Project", "For testing", cancellationToken: default);

        try
        {
            // Store memory with project owner
            var owner = MemoryOwner.ForProject(project.Id);
            var memory = await storage.StoreMemory(
                type: "test",
                content: "Test content for project scoping",
                source: "integration-test",
                tags: new[] { "test" },
                confidence: new Confidence(1.0),
                title: "Project Scoped Memory",
                owner: owner,
                archetype: ArchetypeEnum.Document,
                cancellationToken: default
            );

            Assert.NotNull(memory);
            Assert.Equal(project.Id, memory.Owner.ProjectId);
            Assert.Equal(OwnerTypeEnum.Project, memory.Owner.Type);
            Assert.Equal(ArchetypeEnum.Document, memory.Archetype);

            _output.WriteLine($"Stored memory {memory.Id} with project owner {project.Id}");

            // Verify by retrieving
            var retrieved = await storage.Get(memory.Id, default);
            Assert.NotNull(retrieved);
            Assert.Equal(project.Id, retrieved.Owner.ProjectId);

            // Cleanup memory
            await storage.Delete(memory.Id, default);
        }
        finally
        {
            await storage.DeleteProjectAsync(project.Id, default);
            await storage.DeleteWorkspaceAsync(workspace.Id, default);
        }
    }

    [Fact]
    public async Task StoreMemory_WithNoOwner_DefaultsToUnfiled()
    {
        var storage = _services.GetRequiredService<IStorage>();

        // Store memory without specifying owner (should default to Unfiled)
        var memory = await storage.StoreMemory(
            type: "test",
            content: "Test content with default owner",
            source: "integration-test",
            tags: new[] { "test" },
            confidence: new Confidence(1.0),
            title: "Default Owner Memory",
            owner: null, // Explicitly null
            archetype: ArchetypeEnum.Document,
            cancellationToken: default
        );

        try
        {
            Assert.NotNull(memory);
            Assert.True(memory.Owner.IsUnfiled, "Memory should be in Unfiled workspace");
            Assert.Equal(OwnerTypeEnum.Workspace, memory.Owner.Type);
            Assert.Equal(Guid.Empty, memory.Owner.Id);

            _output.WriteLine($"Stored memory {memory.Id} in Unfiled workspace");
        }
        finally
        {
            await storage.Delete(memory.Id, default);
        }
    }

    [Fact]
    public async Task StoreMemory_WithRecordArchetype_StoresCorrectly()
    {
        var storage = _services.GetRequiredService<IStorage>();

        // Store memory with Record archetype
        var memory = await storage.StoreMemory(
            type: "work-log",
            content: "Work log entry for today",
            source: "integration-test",
            tags: new[] { "work-log" },
            confidence: new Confidence(1.0),
            title: "Work Log Entry",
            owner: null,
            archetype: ArchetypeEnum.Record, // Record = immutable
            cancellationToken: default
        );

        try
        {
            Assert.NotNull(memory);
            Assert.Equal(ArchetypeEnum.Record, memory.Archetype);
            Assert.False(memory.Archetype.IsMutable(), "Record archetype should not be mutable");

            _output.WriteLine($"Stored record memory {memory.Id} with archetype {memory.Archetype}");
        }
        finally
        {
            await storage.Delete(memory.Id, default);
        }
    }

    #endregion

    #region SearchWithMetadataEmbedding with Project Scoping Tests

    [Fact]
    public async Task SearchWithMetadataEmbedding_WithProjectId_FiltersCorrectly()
    {
        var storage = _services.GetRequiredService<IStorage>();

        // Create workspace and two projects
        var workspace = await storage.CreateWorkspaceAsync("Search Test Workspace", null, cancellationToken: default);
        var project1 = await storage.CreateProjectAsync(workspace.Id, "Project Alpha", null, cancellationToken: default);
        var project2 = await storage.CreateProjectAsync(workspace.Id, "Project Beta", null, cancellationToken: default);

        Memory? memoryInProject1 = null;
        Memory? memoryInProject2 = null;
        Memory? memoryUnfiled = null;

        try
        {
            // Create memories in different locations
            memoryInProject1 = await storage.StoreMemory(
                type: "reference",
                content: "Alpha project documentation about widgets",
                source: "test",
                tags: new[] { "docs" },
                confidence: new Confidence(1.0),
                title: "Alpha Widget Docs",
                owner: MemoryOwner.ForProject(project1.Id),
                cancellationToken: default
            );

            memoryInProject2 = await storage.StoreMemory(
                type: "reference",
                content: "Beta project documentation about widgets",
                source: "test",
                tags: new[] { "docs" },
                confidence: new Confidence(1.0),
                title: "Beta Widget Docs",
                owner: MemoryOwner.ForProject(project2.Id),
                cancellationToken: default
            );

            memoryUnfiled = await storage.StoreMemory(
                type: "reference",
                content: "Unfiled documentation about widgets",
                source: "test",
                tags: new[] { "docs" },
                confidence: new Confidence(1.0),
                title: "Unfiled Widget Docs",
                owner: null, // Unfiled
                cancellationToken: default
            );

            _output.WriteLine($"Created 3 test memories across 2 projects and Unfiled");

            // Search within Project 1 only
            var project1Results = await storage.SearchWithMetadataEmbedding(
                query: "widget documentation",
                limit: 10,
                minSimilarity: new SimilarityScore(0.3), // Low threshold for test
                filterTags: null,
                projectId: project1.Id,
                includeUnassigned: false,
                cancellationToken: default
            );

            _output.WriteLine($"Project 1 search returned {project1Results.Count} results");

            // Should only contain the memory from Project 1
            Assert.All(project1Results, m => Assert.Equal(project1.Id, m.Owner.ProjectId));

            // Search within Project 1 INCLUDING unfiled
            var project1WithUnfiled = await storage.SearchWithMetadataEmbedding(
                query: "widget documentation",
                limit: 10,
                minSimilarity: new SimilarityScore(0.3),
                filterTags: null,
                projectId: project1.Id,
                includeUnassigned: true,
                cancellationToken: default
            );

            _output.WriteLine($"Project 1 + Unfiled search returned {project1WithUnfiled.Count} results");

            // Should contain memories from Project 1 OR Unfiled
            Assert.All(project1WithUnfiled, m =>
                Assert.True(
                    m.Owner.ProjectId == project1.Id || m.Owner.IsUnfiled,
                    $"Memory {m.Id} should be in Project 1 or Unfiled"
                )
            );
        }
        finally
        {
            // Cleanup
            if (memoryInProject1 != null) await storage.Delete(memoryInProject1.Id, default);
            if (memoryInProject2 != null) await storage.Delete(memoryInProject2.Id, default);
            if (memoryUnfiled != null) await storage.Delete(memoryUnfiled.Id, default);

            await storage.DeleteProjectAsync(project1.Id, default);
            await storage.DeleteProjectAsync(project2.Id, default);
            await storage.DeleteWorkspaceAsync(workspace.Id, default);
        }
    }

    [Fact]
    public async Task SearchWithMetadataEmbedding_WithNoProjectId_SearchesAll()
    {
        var storage = _services.GetRequiredService<IStorage>();

        // Create a workspace and project
        var workspace = await storage.CreateWorkspaceAsync("Global Search Test", null, cancellationToken: default);
        var project = await storage.CreateProjectAsync(workspace.Id, "Test Project", null, cancellationToken: default);

        Memory? memoryInProject = null;
        Memory? memoryUnfiled = null;

        try
        {
            // Create one memory in project, one unfiled
            memoryInProject = await storage.StoreMemory(
                type: "reference",
                content: "Project memory about global search testing",
                source: "test",
                tags: null,
                confidence: new Confidence(1.0),
                title: "Project Global Search Test",
                owner: MemoryOwner.ForProject(project.Id),
                cancellationToken: default
            );

            memoryUnfiled = await storage.StoreMemory(
                type: "reference",
                content: "Unfiled memory about global search testing",
                source: "test",
                tags: null,
                confidence: new Confidence(1.0),
                title: "Unfiled Global Search Test",
                owner: null,
                cancellationToken: default
            );

            // Search without projectId (should search all).
            // Use minSimilarity=0.0 (accept any distance) so this test verifies owner-filter
            // bypass behavior rather than embedding quality, which is nondeterministic in CI.
            var globalResults = await storage.SearchWithMetadataEmbedding(
                query: "global search testing",
                limit: 10,
                minSimilarity: new SimilarityScore(0.0),
                filterTags: null,
                projectId: null, // No project filter
                includeUnassigned: false, // Ignored when projectId is null
                cancellationToken: default
            );

            _output.WriteLine($"Global search returned {globalResults.Count} results");

            // Both specific memories must appear regardless of embedding similarity score
            var resultIds = globalResults.Select(m => m.Id).ToHashSet();
            Assert.Contains(memoryInProject!.Id, resultIds);
            Assert.Contains(memoryUnfiled!.Id, resultIds);
        }
        finally
        {
            if (memoryInProject != null) await storage.Delete(memoryInProject.Id, default);
            if (memoryUnfiled != null) await storage.Delete(memoryUnfiled.Id, default);

            await storage.DeleteProjectAsync(project.Id, default);
            await storage.DeleteWorkspaceAsync(workspace.Id, default);
        }
    }

    #endregion

    #region MoveToProject Tests

    [Fact]
    public async Task SetMemoryOwner_MovesMemoryToProject()
    {
        var storage = _services.GetRequiredService<IStorage>();

        // Create workspace and project
        var workspace = await storage.CreateWorkspaceAsync("Move Test Workspace", null, cancellationToken: default);
        var project = await storage.CreateProjectAsync(workspace.Id, "Target Project", null, cancellationToken: default);

        try
        {
            // Create memory in Unfiled
            var memory = await storage.StoreMemory(
                type: "reference",
                content: "Memory to be moved",
                source: "test",
                tags: null,
                confidence: new Confidence(1.0),
                title: "Moveable Memory",
                owner: null, // Start in Unfiled
                cancellationToken: default
            );

            Assert.True(memory.Owner.IsUnfiled, "Memory should start in Unfiled");

            // Move to project
            await storage.SetMemoryOwnerAsync(memory.Id, MemoryOwner.ForProject(project.Id), default);

            // Verify move
            var movedMemory = await storage.Get(memory.Id, default);
            Assert.NotNull(movedMemory);
            Assert.Equal(project.Id, movedMemory.Owner.ProjectId);
            Assert.Equal(OwnerTypeEnum.Project, movedMemory.Owner.Type);

            _output.WriteLine($"Successfully moved memory {memory.Id} to project {project.Id}");

            // Cleanup
            await storage.Delete(memory.Id, default);
        }
        finally
        {
            await storage.DeleteProjectAsync(project.Id, default);
            await storage.DeleteWorkspaceAsync(workspace.Id, default);
        }
    }

    [Fact]
    public async Task MoveMemoryToUnfiled_MovesBackToUnfiled()
    {
        var storage = _services.GetRequiredService<IStorage>();

        // Create workspace and project
        var workspace = await storage.CreateWorkspaceAsync("Unfiled Move Test", null, cancellationToken: default);
        var project = await storage.CreateProjectAsync(workspace.Id, "Source Project", null, cancellationToken: default);

        try
        {
            // Create memory in project
            var memory = await storage.StoreMemory(
                type: "reference",
                content: "Memory in project to be unfiled",
                source: "test",
                tags: null,
                confidence: new Confidence(1.0),
                title: "Project Memory",
                owner: MemoryOwner.ForProject(project.Id),
                cancellationToken: default
            );

            Assert.Equal(project.Id, memory.Owner.ProjectId);

            // Move to Unfiled
            await storage.MoveMemoryToUnfiledAsync(memory.Id, default);

            // Verify
            var unfiledMemory = await storage.Get(memory.Id, default);
            Assert.NotNull(unfiledMemory);
            Assert.True(unfiledMemory.Owner.IsUnfiled, "Memory should be in Unfiled after move");

            _output.WriteLine($"Successfully moved memory {memory.Id} back to Unfiled");

            // Cleanup
            await storage.Delete(memory.Id, default);
        }
        finally
        {
            await storage.DeleteProjectAsync(project.Id, default);
            await storage.DeleteWorkspaceAsync(workspace.Id, default);
        }
    }

    #endregion

    #region Workspace-Scoped Search Tests

    [Fact]
    public async Task SearchWithMetadataEmbedding_WithWorkspaceId_RollsUpWorkspaceAndProjects()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var workspaceA = await storage.CreateWorkspaceAsync("Workspace A", null, cancellationToken: default);
        var workspaceB = await storage.CreateWorkspaceAsync("Workspace B", null, cancellationToken: default);
        var projectA1 = await storage.CreateProjectAsync(workspaceA.Id, "Project A1", null, cancellationToken: default);
        var projectA2 = await storage.CreateProjectAsync(workspaceA.Id, "Project A2", null, cancellationToken: default);
        var projectB1 = await storage.CreateProjectAsync(workspaceB.Id, "Project B1", null, cancellationToken: default);

        Memory? memoryOnWorkspaceA = null;
        Memory? memoryInProjectA1 = null;
        Memory? memoryInProjectA2 = null;
        Memory? memoryOnWorkspaceB = null;
        Memory? memoryInProjectB1 = null;

        try
        {
            memoryOnWorkspaceA = await storage.StoreMemory(
                type: "reference",
                content: "Workspace A shared note about flux capacitor configuration",
                source: "test",
                tags: null,
                confidence: new Confidence(1.0),
                title: "Workspace A Note",
                owner: MemoryOwner.ForWorkspace(workspaceA.Id),
                cancellationToken: default
            );

            memoryInProjectA1 = await storage.StoreMemory(
                type: "reference",
                content: "Project A1 details about flux capacitor configuration",
                source: "test",
                tags: null,
                confidence: new Confidence(1.0),
                title: "Project A1 Note",
                owner: MemoryOwner.ForProject(projectA1.Id),
                cancellationToken: default
            );

            memoryInProjectA2 = await storage.StoreMemory(
                type: "reference",
                content: "Project A2 details about flux capacitor configuration",
                source: "test",
                tags: null,
                confidence: new Confidence(1.0),
                title: "Project A2 Note",
                owner: MemoryOwner.ForProject(projectA2.Id),
                cancellationToken: default
            );

            memoryOnWorkspaceB = await storage.StoreMemory(
                type: "reference",
                content: "Workspace B unrelated note about flux capacitor configuration",
                source: "test",
                tags: null,
                confidence: new Confidence(1.0),
                title: "Workspace B Note",
                owner: MemoryOwner.ForWorkspace(workspaceB.Id),
                cancellationToken: default
            );

            memoryInProjectB1 = await storage.StoreMemory(
                type: "reference",
                content: "Project B1 unrelated note about flux capacitor configuration",
                source: "test",
                tags: null,
                confidence: new Confidence(1.0),
                title: "Project B1 Note",
                owner: MemoryOwner.ForProject(projectB1.Id),
                cancellationToken: default
            );

            var results = await storage.SearchWithMetadataEmbedding(
                query: "flux capacitor configuration",
                limit: 20,
                minSimilarity: new SimilarityScore(0.0),
                filterTags: null,
                projectId: null,
                includeUnassigned: false,
                includeArchived: false,
                includeSystem: false,
                workspaceId: workspaceA.Id,
                cancellationToken: default
            );

            var resultIds = results.Select(m => m.Id).ToHashSet();

            Assert.Contains(memoryOnWorkspaceA.Id, resultIds);
            Assert.Contains(memoryInProjectA1.Id, resultIds);
            Assert.Contains(memoryInProjectA2.Id, resultIds);
            Assert.DoesNotContain(memoryOnWorkspaceB.Id, resultIds);
            Assert.DoesNotContain(memoryInProjectB1.Id, resultIds);
        }
        finally
        {
            if (memoryOnWorkspaceA != null) await storage.Delete(memoryOnWorkspaceA.Id, default);
            if (memoryInProjectA1 != null) await storage.Delete(memoryInProjectA1.Id, default);
            if (memoryInProjectA2 != null) await storage.Delete(memoryInProjectA2.Id, default);
            if (memoryOnWorkspaceB != null) await storage.Delete(memoryOnWorkspaceB.Id, default);
            if (memoryInProjectB1 != null) await storage.Delete(memoryInProjectB1.Id, default);

            await storage.DeleteProjectAsync(projectA1.Id, default);
            await storage.DeleteProjectAsync(projectA2.Id, default);
            await storage.DeleteProjectAsync(projectB1.Id, default);
            await storage.DeleteWorkspaceAsync(workspaceA.Id, default);
            await storage.DeleteWorkspaceAsync(workspaceB.Id, default);
        }
    }

    [Fact]
    public async Task SearchWithMetadataEmbedding_WithBothProjectIdAndWorkspaceId_Throws()
    {
        var storage = _services.GetRequiredService<IStorage>();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SearchWithMetadataEmbedding(
            query: "anything",
            limit: 5,
            minSimilarity: new SimilarityScore(0.5),
            filterTags: null,
            projectId: ProjectId.New(),
            includeUnassigned: false,
            includeArchived: false,
            includeSystem: false,
            workspaceId: WorkspaceId.New(),
            cancellationToken: default
        ));
    }

    [Fact]
    public async Task HybridSearch_WithBothProjectIdAndWorkspaceId_Throws()
    {
        var storage = _services.GetRequiredService<IStorage>();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.HybridSearch(
            query: "anything",
            limit: 5,
            minSimilarity: new SimilarityScore(0.5),
            filterTags: null,
            projectId: ProjectId.New(),
            includeUnassigned: false,
            includeArchived: false,
            includeSystem: false,
            workspaceId: WorkspaceId.New(),
            cancellationToken: default
        ));
    }

    #endregion
}
