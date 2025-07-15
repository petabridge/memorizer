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
public class MetadataEmbeddingActorTests : TestKit
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MetadataEmbeddingActorTests(IntegrationTestFixture fixture, ITestOutputHelper output)
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
            await storage.StoreMemory("test", "A", "test", new[] { "x" }, 1.0, "A"),
            await storage.StoreMemory("test", "B", "test", new[] { "y" }, 1.0, "B"),
            await storage.StoreMemory("test", "C", "test", new[] { "z" }, 1.0, "C")
        };

        var actor = Sys.ActorOf(Props.Create(() => new MetadataEmbeddingActor(storage, embeddingService)));

        // Act: Start the embedding job
        actor.Tell(new RegenerateAllMetadataEmbeddings(PageSize: 2, RequestedBy: "test"), ActorRefs.NoSender);

        // Poll for status until job is complete
        MetadataEmbeddingStatus? status = null;
        for (int i = 0; i < 20; i++) // Wait up to 10 seconds
        {
            await Task.Delay(500);
            status = await actor.Ask<MetadataEmbeddingStatus>(new GetMetadataEmbeddingStatus(), TimeSpan.FromSeconds(2));
            if (status.Status == "completed")
                break;
        }

        Assert.NotNull(status);
        Assert.Equal("completed", status.Status.ToLowerInvariant());
        var expectedProcessed = initialCount + testMemories.Length;
        Assert.Equal(expectedProcessed, status.TotalProcessed);
        Assert.Equal(expectedProcessed, status.TotalSuccessful);
        Assert.Equal(0, status.TotalFailed);
        Assert.Empty(status.FailedMemoryIds ?? new());

        // Clean up test data
        foreach (var memory in testMemories)
            await storage.Delete(memory.Id);
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