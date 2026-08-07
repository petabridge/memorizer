using Memorizer.Extensions;
using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for the tag cloud service (ITagCloudService).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class TagCloudIntegrationTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly IServiceProvider _services;

    public void Dispose()
    {
        (_services as IDisposable)?.Dispose();
    }

    public TagCloudIntegrationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
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
        services.AddLogging();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task WorkspaceSubtreeTagCounts_AggregateProjectsAndNestedWorkspaces()
    {
        // Arrange
        var storage = _services.GetRequiredService<IStorage>();
        var tagCloud = _services.GetRequiredService<ITagCloudService>();

        var workspace = await storage.CreateWorkspaceAsync("TagCloud Root", "root");
        var project = await storage.CreateProjectAsync(workspace.Id, "TagCloud Project", "p");
        var nested = await storage.CreateWorkspaceAsync("TagCloud Nested", "n", parentId: workspace.Id);

        var created = new List<MemoryId>();
        try
        {
            created.Add((await storage.StoreMemory(
                "reference", "workspace direct memory", "test", new[] { "alpha" },
                new Confidence(1.0), "Workspace Memory", owner: MemoryOwner.ForWorkspace(workspace.Id))).Id);
            created.Add((await storage.StoreMemory(
                "reference", "project memory", "test", new[] { "beta" },
                new Confidence(1.0), "Project Memory", owner: MemoryOwner.ForProject(project.Id))).Id);
            created.Add((await storage.StoreMemory(
                "reference", "nested workspace memory", "test", new[] { "alpha", "gamma" },
                new Confidence(1.0), "Nested Memory", owner: MemoryOwner.ForWorkspace(nested.Id))).Id);

            // Act - subtree covers direct + project + nested workspace tags
            var subtreeCounts = await tagCloud.GetWorkspaceSubtreeTagCountsAsync(workspace.Id);

            // Assert
            Assert.Contains(subtreeCounts, x => x.Tag == "alpha" && x.Count == 2);
            Assert.Contains(subtreeCounts, x => x.Tag == "beta" && x.Count == 1);
            Assert.Contains(subtreeCounts, x => x.Tag == "gamma" && x.Count == 1);
        }
        finally
        {
            foreach (var id in created)
                await storage.Delete(id);
            await storage.DeleteProjectAsync(project.Id);
            await storage.DeleteWorkspaceAsync(nested.Id);
            await storage.DeleteWorkspaceAsync(workspace.Id);
        }
    }

    [Fact]
    public async Task ProjectTagCounts_OnlyIncludeProjectMemories()
    {
        // Arrange
        var storage = _services.GetRequiredService<IStorage>();
        var tagCloud = _services.GetRequiredService<ITagCloudService>();

        var workspace = await storage.CreateWorkspaceAsync("TagCloud Root", "root");
        var project = await storage.CreateProjectAsync(workspace.Id, "TagCloud Project", "p");

        var created = new List<MemoryId>();
        try
        {
            created.Add((await storage.StoreMemory(
                "reference", "workspace memory", "test", new[] { "alpha" },
                new Confidence(1.0), "Workspace Memory", owner: MemoryOwner.ForWorkspace(workspace.Id))).Id);
            created.Add((await storage.StoreMemory(
                "reference", "project memory", "test", new[] { "beta" },
                new Confidence(1.0), "Project Memory", owner: MemoryOwner.ForProject(project.Id))).Id);

            // Act
            var projectCounts = await tagCloud.GetProjectTagCountsAsync(project.Id);

            // Assert - only the project's own tag, not the workspace's
            var beta = Assert.Single(projectCounts);
            Assert.Equal("beta", beta.Tag);
            Assert.Equal(1, beta.Count);
        }
        finally
        {
            foreach (var id in created)
                await storage.Delete(id);
            await storage.DeleteProjectAsync(project.Id);
            await storage.DeleteWorkspaceAsync(workspace.Id);
        }
    }
}
