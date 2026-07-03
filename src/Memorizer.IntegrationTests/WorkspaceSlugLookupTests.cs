using Memorizer.Extensions;
using Memorizer.IntegrationTests.Logging;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PostgMem.Tools;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// End-to-end tests for resolving workspaces by slug through the real
/// <see cref="WorkspaceTools.GetWorkspace"/> MCP tool against a live PostgreSQL database.
/// Exercises the tool logic, slug normalization, and the underlying SQL.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class WorkspaceSlugLookupTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly IServiceProvider _services;

    public WorkspaceSlugLookupTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _services = CreateServices();
    }

    public void Dispose() => (_services as IDisposable)?.Dispose();

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

    private WorkspaceTools CreateTools()
    {
        var storage = _services.GetRequiredService<IStorage>();
        var urlService = _services.GetRequiredService<ICanonicalUrlService>();
        return new WorkspaceTools(storage, NullLogger<WorkspaceTools>.Instance, urlService);
    }

    [Fact]
    public async Task GetWorkspace_BySlug_ResolvesWorkspaceEndToEnd()
    {
        var storage = _services.GetRequiredService<IStorage>();
        var tools = CreateTools();

        var created = await storage.CreateWorkspaceAsync("Engineering", "Root workspace", cancellationToken: default);
        _output.WriteLine($"Created workspace {created.Id.Value} with slug '{created.Slug}'");

        try
        {
            var result = await tools.GetWorkspace(slug: created.Slug);

            Assert.Contains("Workspace: Engineering", result);
            Assert.Contains($"Slug: {created.Slug}", result);
            Assert.Contains(created.Id.Value.ToString(), result);
        }
        finally
        {
            await storage.DeleteWorkspaceAsync(created.Id, default);
        }
    }

    [Fact]
    public async Task GetWorkspace_BySlug_IsCaseInsensitive()
    {
        var storage = _services.GetRequiredService<IStorage>();
        var tools = CreateTools();

        var created = await storage.CreateWorkspaceAsync("Platform Team", cancellationToken: default);
        _output.WriteLine($"Created workspace with slug '{created.Slug}'");

        try
        {
            // Caller passes an upper-cased slug; tool normalizes to the stored lowercase form.
            var result = await tools.GetWorkspace(slug: created.Slug.ToUpperInvariant());

            Assert.Contains("Workspace: Platform Team", result);
            Assert.Contains($"Slug: {created.Slug}", result);
        }
        finally
        {
            await storage.DeleteWorkspaceAsync(created.Id, default);
        }
    }

    [Fact]
    public async Task GetWorkspace_BySlug_ScopesToParent()
    {
        var storage = _services.GetRequiredService<IStorage>();
        var tools = CreateTools();

        // Same slug at root and nested under a parent — parentWorkspaceId disambiguates.
        var parent = await storage.CreateWorkspaceAsync("Platform", cancellationToken: default);
        var rootDupe = await storage.CreateWorkspaceAsync("Backend", "root scope", cancellationToken: default);
        var childDupe = await storage.CreateWorkspaceAsync("Backend", "child scope", parentId: parent.Id, cancellationToken: default);

        Assert.Equal(rootDupe.Slug, childDupe.Slug);
        _output.WriteLine($"Root '{rootDupe.Id.Value}' and child '{childDupe.Id.Value}' share slug '{rootDupe.Slug}'");

        try
        {
            var rootResult = await tools.GetWorkspace(slug: rootDupe.Slug);
            var childResult = await tools.GetWorkspace(slug: childDupe.Slug, parentWorkspaceId: parent.Id.Value.ToString());

            Assert.Contains(rootDupe.Id.Value.ToString(), rootResult);
            Assert.DoesNotContain(childDupe.Id.Value.ToString(), rootResult);

            Assert.Contains(childDupe.Id.Value.ToString(), childResult);
            Assert.DoesNotContain(rootDupe.Id.Value.ToString(), childResult);
        }
        finally
        {
            await storage.DeleteWorkspaceAsync(childDupe.Id, default);
            await storage.DeleteWorkspaceAsync(rootDupe.Id, default);
            await storage.DeleteWorkspaceAsync(parent.Id, default);
        }
    }

    [Fact]
    public async Task GetWorkspace_BySlug_WhenMissing_ReturnsNotFound()
    {
        var tools = CreateTools();

        var result = await tools.GetWorkspace(slug: "definitely-does-not-exist");

        Assert.Contains("Workspace with slug 'definitely-does-not-exist' not found among root workspaces.", result);
    }
}
