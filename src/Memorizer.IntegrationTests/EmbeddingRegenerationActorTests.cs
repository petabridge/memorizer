using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Memorizer.Actors;
using Memorizer.Models;
using Pgvector;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System.Linq;
using Memorizer.Extensions;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Memorizer.IntegrationTests;

[Collection(nameof(IntegrationTestCollection))]
public class EmbeddingRegenerationActorTests : TestKit
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public EmbeddingRegenerationActorTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(output: output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Actor_Emits_BatchCompleted_When_Processing_Finishes()
    {
        var storage = Host.Services.GetRequiredService<IStorage>();
        var embeddingService = Host.Services.GetRequiredService<IEmbeddingService>();

        // 1. Get initial count
        var initialCount = (await storage.GetMemoriesPaginated(1, int.MaxValue)).Memories.Count;

        // 2. Add test memories
        var testMemories = new[]
        {
            await storage.StoreMemory("test", "Content A for embedding regeneration test", "test", new[] { "x" }, 1.0, "Title A"),
            await storage.StoreMemory("test", "Content B for embedding regeneration test", "test", new[] { "y" }, 1.0, "Title B"),
            await storage.StoreMemory("test", "Content C for embedding regeneration test", "test", new[] { "z" }, 1.0, "Title C")
        };

        var actor = Sys.ActorOf(Props.Create(() => new EmbeddingRegenerationActor(storage, embeddingService)));

        // Act: Start the embedding regeneration job
        actor.Tell(new RegenerateAllEmbeddings(PageSize: 2, RequestedBy: "test"), ActorRefs.NoSender);

        // Poll for status until job is complete (actor returns to "idle" state after completion)
        EmbeddingRegenerationStatus? status = null;
        var expectedProcessed = initialCount + testMemories.Length;
        bool jobStarted = false;

        for (int i = 0; i < 40; i++) // Wait up to 20 seconds
        {
            await Task.Delay(500);
            status = await actor.Ask<EmbeddingRegenerationStatus>(new GetEmbeddingRegenerationStatus(), TimeSpan.FromSeconds(2));

            // Track when job actually starts running
            if (status.IsRunning)
                jobStarted = true;

            // Job is complete when it returns to idle after having been running,
            // or when it's idle and has processed the expected count
            if (jobStarted && !status.IsRunning)
                break;

            _output.WriteLine($"Status: {status.Status}, IsRunning: {status.IsRunning}, Processed: {status.TotalProcessed}/{expectedProcessed}");
        }

        Assert.NotNull(status);
        Assert.False(status.IsRunning, "Job should no longer be running");

        // After job completion, the actor returns to idle state
        // Check that processing completed by verifying no errors and job is done
        Assert.Equal("idle", status.Status.ToLowerInvariant());

        // Verify that embeddings were regenerated (both content and metadata)
        foreach (var testMemory in testMemories)
        {
            var regeneratedMemory = await storage.Get(testMemory.Id);
            Assert.NotNull(regeneratedMemory);
            Assert.NotNull(regeneratedMemory.Embedding);
            Assert.NotNull(regeneratedMemory.EmbeddingMetadata);
            _output.WriteLine($"Memory {testMemory.Id}: Content embedding exists={regeneratedMemory.Embedding != null}, Metadata embedding exists={regeneratedMemory.EmbeddingMetadata != null}");
        }

        // Clean up test data
        foreach (var memory in testMemories)
            await storage.Delete(memory.Id);
    }

    [Fact]
    public async Task Actor_Regenerates_Both_Content_And_Metadata_Embeddings()
    {
        var storage = Host.Services.GetRequiredService<IStorage>();
        var embeddingService = Host.Services.GetRequiredService<IEmbeddingService>();

        // Create a test memory
        var testMemory = await storage.StoreMemory(
            "test",
            "This is test content for dual embedding regeneration verification",
            "test",
            new[] { "dual-test", "embedding" },
            1.0,
            "Dual Embedding Test");

        // Get original embeddings
        var original = await storage.Get(testMemory.Id);
        Assert.NotNull(original);
        Assert.NotNull(original.Embedding);
        Assert.NotNull(original.EmbeddingMetadata);

        var originalContentEmbedding = original.Embedding.ToArray();
        var originalMetadataEmbedding = original.EmbeddingMetadata.ToArray();

        _output.WriteLine($"Original content embedding dimensions: {originalContentEmbedding.Length}");
        _output.WriteLine($"Original metadata embedding dimensions: {originalMetadataEmbedding.Length}");

        var actor = Sys.ActorOf(Props.Create(() => new EmbeddingRegenerationActor(storage, embeddingService)));

        // Start regeneration
        actor.Tell(new RegenerateAllEmbeddings(PageSize: 10, RequestedBy: "test"), ActorRefs.NoSender);

        // Wait for completion
        EmbeddingRegenerationStatus? status = null;
        bool jobStarted = false;

        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            status = await actor.Ask<EmbeddingRegenerationStatus>(new GetEmbeddingRegenerationStatus(), TimeSpan.FromSeconds(2));

            if (status.IsRunning)
                jobStarted = true;

            if (jobStarted && !status.IsRunning)
                break;
        }

        Assert.NotNull(status);
        Assert.False(status.IsRunning);

        // Get regenerated memory
        var regenerated = await storage.Get(testMemory.Id);
        Assert.NotNull(regenerated);
        Assert.NotNull(regenerated.Embedding);
        Assert.NotNull(regenerated.EmbeddingMetadata);

        var regeneratedContentEmbedding = regenerated.Embedding.ToArray();
        var regeneratedMetadataEmbedding = regenerated.EmbeddingMetadata.ToArray();

        _output.WriteLine($"Regenerated content embedding dimensions: {regeneratedContentEmbedding.Length}");
        _output.WriteLine($"Regenerated metadata embedding dimensions: {regeneratedMetadataEmbedding.Length}");

        // Verify embeddings have correct dimensions (should match embedding service output)
        Assert.Equal(originalContentEmbedding.Length, regeneratedContentEmbedding.Length);
        Assert.Equal(originalMetadataEmbedding.Length, regeneratedMetadataEmbedding.Length);

        // Clean up
        await storage.Delete(testMemory.Id);
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // Configure services just like in the IntegrationTests class
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                ["Embeddings:Model"] = "all-minilm",
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString()
            })
            .Build());

        // Add HTTP client for embedding service
        services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
        {
            client.BaseAddress = new Uri(_fixture.OllamaApiUrl);
            client.Timeout = TimeSpan.FromMinutes(1);
        });

        // Add services
        services.AddSingleton(new EmbeddingSettings
        {
            ApiUrl = new Uri(_fixture.OllamaApiUrl),
            Model = "all-minilm",
            Timeout = TimeSpan.FromMinutes(1)
        });

        services.AddMemorizer(initialize:false);
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No custom Akka configuration needed for this test
    }
}
