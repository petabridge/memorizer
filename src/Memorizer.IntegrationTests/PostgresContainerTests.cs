using Akka.Hosting;
using Akka.Hosting.TestKit;
using Memorizer.Extensions;
using Memorizer.Models;
using Memorizer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PostgMem.Tools;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for PostgMem using TestContainers
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class IntegrationTests : TestKit
{
    private readonly IntegrationTestFixture _fixture;
    private IStorage _storage = null!;
    private IEmbeddingService _embeddingService = null!;

    public IntegrationTests(IntegrationTestFixture fixture, ITestOutputHelper output) 
        : base(output: output)
    {
        _fixture = fixture;
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        var apiUrl = new Uri(_fixture.OllamaApiUrl);

        // Add in-memory configuration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = apiUrl.ToString(),
                ["Embeddings:Model"] = "all-minilm",
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString()
            })
            .Build();
        
        services.AddSingleton<IConfiguration>(config);

        // AddPostgMem() handles all HTTP client configurations properly
        services.AddMemorizer();
        
        // Add MemoryTools for integration tests
        services.AddScoped<MemoryTools>();
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.ConfigureLoggers(configBuilder =>
        {
            configBuilder.LogLevel = Akka.Event.LogLevel.DebugLevel;
        });
    }

    [Fact]
    public async Task CanConnectAndUsePgvector()
    {
        await using var conn = new NpgsqlConnection(_fixture.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Create a vector with 384 dimensions (all zeros)
        var vector = string.Join(",", Enumerable.Repeat("0", 384));
        cmd.CommandText = $"INSERT INTO memories (id, type, content, text, source, embedding, tags, confidence, created_at, updated_at) VALUES (gen_random_uuid(), 'test', '{{}}'::jsonb, 'test text', 'test', '[{vector}]'::vector, ARRAY['tag'], 1.0, now(), now()) RETURNING id;";
        var id = await cmd.ExecuteScalarAsync();
        Assert.NotNull(id);
    }

    [Fact]
    public async Task Should_Store_And_Retrieve_Memory()
    {
        // Get services from DI
        _storage = Host.Services.GetRequiredService<IStorage>();
        _embeddingService = Host.Services.GetRequiredService<IEmbeddingService>();

        // Test storing a memory
        var memory = await _storage.StoreMemory(
            "test",
            "test content",
            "test",
            new[] { "test" },
            1.0,
            "Test Title"
        );

        // Test retrieving the memory
        var retrieved = await _storage.Get(memory.Id, CancellationToken.None);
        Assert.NotNull(retrieved);
        Assert.Equal(memory.Id, retrieved.Id);
    }

    [Fact]
    public async Task Should_Store_And_Retrieve_Memory_With_Title()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        _embeddingService = Host.Services.GetRequiredService<IEmbeddingService>();

        var title = "Test Title";
        var memory = await _storage.StoreMemory(
            "test",
            "test content",
            "test",
            new[] { "test" },
            1.0,
            title
        );

        var retrieved = await _storage.Get(memory.Id, CancellationToken.None);
        Assert.NotNull(retrieved);
        Assert.Equal(memory.Id, retrieved.Id);
        Assert.Equal(title, retrieved.Title);
    }

    [Fact]
    public async Task Should_Store_And_Retrieve_Memory_Without_Title()
    {
        // Get services from DI
        _storage = Host.Services.GetRequiredService<IStorage>();
        _embeddingService = Host.Services.GetRequiredService<IEmbeddingService>();

        // Store a memory with empty title instead of null
        var memory = await _storage.StoreMemory(
                "test",
                "test content",
                "test",
                new[] { "test" },
                1.0,
                "Default Title"
            );

        Assert.NotNull(memory);
        Assert.Equal("Default Title", memory.Title);
    }

    [Fact]
    public async Task CanSearchMemories()
    {
        // Get services from DI
        _storage = Host.Services.GetRequiredService<IStorage>();
        _embeddingService = Host.Services.GetRequiredService<IEmbeddingService>();

        // Arrange
        var memories = new[]
        {
            ("memory1", "The sky is blue", new[] { "nature" }),
            ("memory2", "Grass is green", new[] { "nature" }),
            ("memory3", "The sun is hot", new[] { "nature", "space" })
        };

        foreach (var (type, content, tags) in memories)
        {
            await _storage.StoreMemory(type, content, "test", tags, 1.0, title: type);
        }

        // Act
        var results = await _storage.Search(
            "What color is the sky?",
            limit: 1,
            minSimilarity: 0.5,
            filterTags: new[] { "nature" }
        );

        // Assert
        Assert.Single(results);
        Assert.Contains("sky", results[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CanDeleteMemory()
    {
        // Get services from DI
        _storage = Host.Services.GetRequiredService<IStorage>();
        _embeddingService = Host.Services.GetRequiredService<IEmbeddingService>();

        // Arrange
        var memory = await _storage.StoreMemory(
            "test",
            "This will be deleted",
            "test",
            new[] { "temporary" },
            1.0,
            "Test Title"
        );

        // Act
        var deleteResult = await _storage.Delete(memory.Id);
        var retrievedMemory = await _storage.Get(memory.Id);

        // Assert
        Assert.True(deleteResult);
        Assert.Null(retrievedMemory);
    }

    [Fact]
    public async Task SchemaVersionTable_IsPopulated_AfterMigration()
    {
        await using var conn = new NpgsqlConnection(_fixture.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, name, applied_at FROM schema_version ORDER BY version";
        await using var reader = await cmd.ExecuteReaderAsync();
        var found = false;
        var versions = new List<int>();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            found = true;
            var version = reader.GetInt32(0);
            var name = reader.GetString(1);
            var appliedAt = reader.GetDateTime(2);
            Assert.True(version > 0, $"Migration version should be positive, got {version}");
            Assert.False(string.IsNullOrWhiteSpace(name), "Migration name should not be empty");
            Assert.True(appliedAt <= DateTime.UtcNow);
            versions.Add(version);
            names.Add(name);
        }
        Assert.True(found, "No migrations found in schema_version table");
        // Optionally: check that version numbers are unique and increasing
        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    [Fact]
    public async Task CanGetManyMemories()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        // Store multiple memories
        var m1 = await _storage.StoreMemory("type1", "A", "src1", new[] { "tag1" }, 1.0, "Memory 1");
        var m2 = await _storage.StoreMemory("type2", "B", "src2", new[] { "tag2" }, 1.0, "Memory 2");
        var m3 = await _storage.StoreMemory("type3", "C", "src3", new[] { "tag3" }, 1.0, "Memory 3");
        // Fetch by ids
        var results = await _storage.GetMany(new[] { m1.Id, m3.Id }, CancellationToken.None);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, m => m.Id == m1.Id);
        Assert.Contains(results, m => m.Id == m3.Id);
    }

    [Fact]
    public async Task CanCreateAndGetMemoryRelationships()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        // Store two memories
        var m1 = await _storage.StoreMemory("type1", "Parent", "src1", new[] { "tag1" }, 1.0, "Parent Memory");
        var m2 = await _storage.StoreMemory("type2", "Child", "src2", new[] { "tag2" }, 1.0, "Child Memory");
        // Create relationship
        var rel = await _storage.CreateRelationship(m1.Id, m2.Id, "Parent", CancellationToken.None);
        Assert.Equal(m1.Id, rel.FromMemoryId);
        Assert.Equal(m2.Id, rel.ToMemoryId);
        Assert.Equal("Parent", rel.Type);
        // Get relationships
        var rels = await _storage.GetRelationships(m1.Id, "Parent", CancellationToken.None);
        Assert.Single(rels);
        Assert.Equal(rel.Id, rels[0].Id);
        Assert.Equal(m2.Id, rels[0].ToMemoryId);
        // New: Check related memory title/type
        Assert.Equal(m2.Title, rels[0].RelatedMemoryTitle);
        Assert.Equal(m2.Type, rels[0].RelatedMemoryType);
    }

    [Fact]
    public async Task CanStoreMemoryWithRelationship()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        // Store a related memory first
        var related = await _storage.StoreMemory("typeX", "Related", "srcX", new[] { "tagX" }, 1.0, "Related Memory");
        // Store a new memory and create a relationship in one call
        var memory = await _storage.StoreMemory("typeY", "Main", "srcY", new[] { "tagY" }, 1.0, "Main Memory", related.Id, "Reference");
        // Check the memory exists
        var retrieved = await _storage.Get(memory.Id);
        Assert.NotNull(retrieved);
        // Check the relationship exists
        var rels = await _storage.GetRelationships(memory.Id, "Reference");
        Assert.Single(rels);
        Assert.Equal(memory.Id, rels[0].FromMemoryId);
        Assert.Equal(related.Id, rels[0].ToMemoryId);
        Assert.Equal("Reference", rels[0].Type);
        // New: Check related memory title/type
        Assert.Equal(related.Title, rels[0].RelatedMemoryTitle);
        Assert.Equal(related.Type, rels[0].RelatedMemoryType);
    }

    [Fact]
    public async Task MemoryRelationship_Type_Is_Always_Present_In_Memory_Relationships()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        // Store two memories
        var m1 = await _storage.StoreMemory("typeA", "Alpha", "srcA", new[] { "tagA" }, 1.0, "Alpha Memory");
        var m2 = await _storage.StoreMemory("typeB", "Beta", "srcB", new[] { "tagB" }, 1.0, "Beta Memory");
        // Create relationship
        var rel = await _storage.CreateRelationship(m1.Id, m2.Id, "Parent", CancellationToken.None);

        // 1. Direct relationship fetch
        var rels = await _storage.GetRelationships(m1.Id, null, CancellationToken.None);
        Assert.Contains(rels, r => r.Id == rel.Id && r.Type == "Parent" && !string.IsNullOrWhiteSpace(r.Type));
        // New: Check related memory title/type
        var rel1 = rels.First(r => r.Id == rel.Id);
        Assert.Equal(m2.Title, rel1.RelatedMemoryTitle);
        Assert.Equal(m2.Type, rel1.RelatedMemoryType);

        // 2. Relationship via Memory.Relationships (Get)
        var mem = await _storage.Get(m1.Id, CancellationToken.None);
        Assert.NotNull(mem);
        Assert.NotNull(mem.Relationships);
        Assert.Contains(mem.Relationships!, r => r.Id == rel.Id && r.Type == "Parent" && !string.IsNullOrWhiteSpace(r.Type));
        // New: Check related memory title/type
        var rel2 = mem.Relationships!.First(r => r.Id == rel.Id);
        Assert.Equal(m2.Title, rel2.RelatedMemoryTitle);
        Assert.Equal(m2.Type, rel2.RelatedMemoryType);

        // 3. Relationship via Memory.Relationships (GetMany)
        var mems = await _storage.GetMany(new[] { m1.Id }, CancellationToken.None);
        Assert.Single(mems);
        Assert.NotNull(mems[0].Relationships);
        Assert.Contains(mems[0].Relationships!, r => r.Id == rel.Id && r.Type == "Parent" && !string.IsNullOrWhiteSpace(r.Type));
        // New: Check related memory title/type
        var rel3 = mems[0].Relationships!.First(r => r.Id == rel.Id);
        Assert.Equal(m2.Title, rel3.RelatedMemoryTitle);
        Assert.Equal(m2.Type, rel3.RelatedMemoryType);
    }

    [Fact]
    public async Task MemoryTools_Store_Should_Return_Fast_For_Large_Content()
    {
        // Arrange
        var memoryTools = Host.Services.GetRequiredService<MemoryTools>();
        var storage = Host.Services.GetRequiredService<IStorage>();
        
        // Create large content that should trigger chunking
        var largeContent = """
        This is a comprehensive guide to software engineering best practices.
        
        ## Code Quality
        Writing clean, maintainable code is essential for long-term project success.
        Follow consistent naming conventions, write meaningful comments, and keep functions small.
        
        ## Testing Strategy
        Implement automated tests at multiple levels: unit tests for individual components,
        integration tests for component interactions, and end-to-end tests for user workflows.
        
        ## Version Control
        Use Git effectively with meaningful commit messages, feature branches, and code reviews.
        Never commit directly to main branch without review.
        
        ## Documentation
        Maintain up-to-date documentation including API documentation, architectural decisions,
        and setup instructions for new team members.
        """;
        
        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await memoryTools.Store(
            type: "reference",
            text: largeContent,
            source: "test",
            tags: new[] { "best-practices", "software-engineering" },
            title: "Software Engineering Best Practices"
        );
        stopwatch.Stop();
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("Memory stored successfully", result);
        
        // Should complete quickly (under 5 seconds for immediate storage)
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, 
            $"Store operation took {stopwatch.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public async Task GetMany_Should_Retrieve_Related_Chunk_Memories()
    {
        // This test validates that GetMany works correctly for retrieving chunks
        // and that the relationship system properly links containers to chunks
        
        // Arrange
        var storage = Host.Services.GetRequiredService<IStorage>();
        
        // Create a container memory
        var containerMemory = await storage.StoreMemory(
            type: "reference-container",
            content: "This memory has been broken into chunks for better searchability.",
            source: "test",
            tags: new[] { "chunked-container" },
            confidence: 1.0,
            relatedTo: null,
            relationshipType: null,
            cancellationToken: CancellationToken.None,
            title: "Test Container"
        );
        
        // Create chunk memories with relationships
        var chunk1 = await storage.StoreMemory(
            type: "reference-chunk",
            content: "First chunk content about Python programming.",
            source: "test",
            tags: new[] { "chunk", "python" },
            confidence: 1.0,
            relatedTo: containerMemory.Id,
            relationshipType: "chunk-of",
            cancellationToken: CancellationToken.None,
            title: "Chunk 1 - Python"
        );
        
        var chunk2 = await storage.StoreMemory(
            type: "reference-chunk", 
            content: "Second chunk content about JavaScript development.",
            source: "test",
            tags: new[] { "chunk", "javascript" },
            confidence: 1.0,
            relatedTo: containerMemory.Id,
            relationshipType: "chunk-of", 
            cancellationToken: CancellationToken.None,
            title: "Chunk 2 - JavaScript"
        );
        
        // Act - Get the chunks and verify they have relationships to the container
        var chunk1Retrieved = await storage.Get(chunk1.Id);
        var chunk2Retrieved = await storage.Get(chunk2.Id);
        
        Assert.NotNull(chunk1Retrieved);
        Assert.NotNull(chunk2Retrieved);
        Assert.NotNull(chunk1Retrieved.Relationships);
        Assert.NotNull(chunk2Retrieved.Relationships);
        
        // Verify chunk relationships point to container
        Assert.Contains(chunk1Retrieved.Relationships, r => r.Type == "chunk-of" && r.ToMemoryId == containerMemory.Id);
        Assert.Contains(chunk2Retrieved.Relationships, r => r.Type == "chunk-of" && r.ToMemoryId == containerMemory.Id);
        
        // Use GetMany to retrieve all chunks
        var chunks = await storage.GetMany(new[] { chunk1.Id, chunk2.Id });
        
        // Assert
        Assert.Equal(2, chunks.Count);
        Assert.Contains(chunks, c => c.Id == chunk1.Id && c.Title == "Chunk 1 - Python");
        Assert.Contains(chunks, c => c.Id == chunk2.Id && c.Title == "Chunk 2 - JavaScript");
        Assert.All(chunks, chunk => 
        {
            Assert.Contains("chunk", chunk.Type);
            Assert.Contains("chunk", chunk.Tags ?? []);
            Assert.True(chunk.Text.Length > 0);
        });
    }

    [Fact]
    public async Task Stress_Test_Memory_Relationships_Should_Not_Leak_Connections()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        
        // Create a chain of interconnected memories to test for infinite recursion
        var memories = new List<Memory>();
        for (int i = 0; i < 10; i++)
        {
            var memory = await _storage.StoreMemory(
                $"type_{i}",
                $"Content for memory {i}",
                $"source_{i}",
                new[] { $"tag_{i}" },
                1.0,
                title: $"Memory {i}");
            memories.Add(memory);
        }
        
        // Create circular relationships (A->B, B->C, C->A pattern)
        for (int i = 0; i < memories.Count; i++)
        {
            var fromMemory = memories[i];
            var toMemory = memories[(i + 1) % memories.Count]; // Circular reference
            await _storage.CreateRelationship(fromMemory.Id, toMemory.Id, "references", CancellationToken.None);
        }
        
        // Add some bi-directional relationships for more complexity
        for (int i = 0; i < 5; i++)
        {
            await _storage.CreateRelationship(memories[i].Id, memories[i + 5].Id, "related", CancellationToken.None);
            await _storage.CreateRelationship(memories[i + 5].Id, memories[i].Id, "related", CancellationToken.None);
        }
        
        // Stress test: rapidly call GetMany multiple times concurrently
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            var memoryIds = memories.Take(5).Select(m => m.Id).ToArray();
            tasks.Add(Task.Run(async () =>
            {
                for (int j = 0; j < 5; j++)
                {
                    var result = await _storage.GetMany(memoryIds, CancellationToken.None);
                    Assert.Equal(5, result.Count);
                    
                    // Verify relationships are loaded without infinite recursion
                    foreach (var memory in result)
                    {
                        Assert.NotNull(memory.Relationships);
                        // Each memory should have at least one relationship
                        Assert.NotEmpty(memory.Relationships);
                        
                        // Verify relationship data is populated correctly
                        foreach (var rel in memory.Relationships)
                        {
                            Assert.NotNull(rel.Type);
                            Assert.True(rel.Type == "references" || rel.Type == "related");
                            // RelatedMemoryTitle and RelatedMemoryType should be populated
                            Assert.NotNull(rel.RelatedMemoryType);
                            Assert.StartsWith("type_", rel.RelatedMemoryType);
                        }
                    }
                }
            }));
        }
        
        // Wait for all tasks to complete - should not hang due to infinite recursion
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
        var completedTask = await Task.WhenAny(Task.WhenAll(tasks), timeoutTask);
        
        Assert.True(completedTask != timeoutTask, 
            "Stress test timed out - possible infinite recursion or connection leak");
    }

    [Fact]
    public async Task Stress_Test_Single_Memory_Relationships_Should_Be_Efficient()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        
        // Create a hub memory with many relationships
        var hubMemory = await _storage.StoreMemory("hub", "Hub memory", "test", new[] { "hub" }, 1.0, "Hub");
        
        // Create many related memories
        var relatedMemories = new List<Memory>();
        for (int i = 0; i < 50; i++)
        {
            var memory = await _storage.StoreMemory(
                $"spoke_{i}",
                $"Spoke memory {i}",
                "test",
                new[] { "spoke" },
                1.0,
                title: $"Spoke {i}");
            relatedMemories.Add(memory);
            
            // Create relationship from hub to spoke
            await _storage.CreateRelationship(hubMemory.Id, memory.Id, "contains", CancellationToken.None);
        }
        
        // Test: Get the hub memory (should load all 50 relationships efficiently)
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var retrievedHub = await _storage.Get(hubMemory.Id, CancellationToken.None);
        stopwatch.Stop();
        
        Assert.NotNull(retrievedHub);
        Assert.NotNull(retrievedHub.Relationships);
        Assert.Equal(50, retrievedHub.Relationships.Count);
        
        // Should complete quickly (under 2 seconds for 50 relationships)
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, 
            $"Loading 50 relationships took {stopwatch.ElapsedMilliseconds}ms, expected < 2000ms");
        
        // Verify all relationships have proper data
        foreach (var rel in retrievedHub.Relationships)
        {
            Assert.Equal("contains", rel.Type);
            Assert.NotNull(rel.RelatedMemoryType);
            Assert.StartsWith("spoke_", rel.RelatedMemoryType);
            Assert.NotNull(rel.RelatedMemoryTitle);
            Assert.StartsWith("Spoke", rel.RelatedMemoryTitle);
        }
    }

    [Fact]
    public async Task Stress_Test_Concurrent_GetMany_Calls_Should_Not_Deadlock()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        
        // Create memories with relationships
        var memories = new List<Memory>();
        for (int i = 0; i < 20; i++)
        {
            var memory = await _storage.StoreMemory(
                $"concurrent_test_{i}",
                $"Content {i}",
                "stress_test",
                new[] { "concurrent" },
                1.0,
                title: $"Memory {i}");
            memories.Add(memory);
        }
        
        // Create relationships between memories
        for (int i = 0; i < 19; i++)
        {
            await _storage.CreateRelationship(memories[i].Id, memories[i + 1].Id, "next", CancellationToken.None);
        }
        
        // Run many concurrent GetMany calls
        var concurrentTasks = new List<Task>();
        for (int i = 0; i < 30; i++)
        {
            var taskId = i;
            concurrentTasks.Add(Task.Run(async () =>
            {
                var randomIds = memories
                    .OrderBy(_ => Guid.NewGuid())
                    .Take(5)
                    .Select(m => m.Id)
                    .ToArray();
                
                var result = await _storage.GetMany(randomIds, CancellationToken.None);
                Assert.Equal(5, result.Count);
                
                // Verify each memory has relationships loaded correctly
                foreach (var memory in result)
                {
                    Assert.NotNull(memory.Relationships);
                    // Some might have relationships, some might not - just verify structure
                    foreach (var rel in memory.Relationships)
                    {
                        Assert.NotNull(rel.Type);
                        Assert.NotNull(rel.RelatedMemoryType);
                    }
                }
            }));
        }
        
        // All tasks should complete without deadlock
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
        var completedTask = await Task.WhenAny(Task.WhenAll(concurrentTasks), timeoutTask);
        
        Assert.True(completedTask != timeoutTask, 
            "Concurrent GetMany calls timed out - possible deadlock or connection exhaustion");
    }

    [Fact]
    public async Task Connection_Pool_Should_Remain_Stable_Under_Load()
    {
        _storage = Host.Services.GetRequiredService<IStorage>();
        
        // Create test data
        var memory1 = await _storage.StoreMemory("test", "Memory 1", "test", new[] { "load_test" }, 1.0, "Memory 1");
        var memory2 = await _storage.StoreMemory("test", "Memory 2", "test", new[] { "load_test" }, 1.0, "Memory 2");
        await _storage.CreateRelationship(memory1.Id, memory2.Id, "test_rel", CancellationToken.None);
        
        // Rapid-fire operations that used to cause connection leaks
        var operations = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            operations.Add(Task.Run(async () =>
            {
                // Mix of operations that involve relationship loading
                var memories = await _storage.GetMany(new[] { memory1.Id, memory2.Id }, CancellationToken.None);
                var relationships = await _storage.GetRelationships(memory1.Id, null, CancellationToken.None);
                var singleMemory = await _storage.Get(memory1.Id, CancellationToken.None);
                
                // Verify results
                Assert.Equal(2, memories.Count);
                Assert.NotEmpty(relationships);
                Assert.NotNull(singleMemory);
            }));
        }
        
        // Should complete without "too many clients" error
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
        var completedTask = await Task.WhenAny(Task.WhenAll(operations), timeoutTask);
        
        Assert.True(completedTask != timeoutTask, 
            "Load test timed out - possible connection pool exhaustion");
        
        // Verify we can still perform operations after the load test
        var finalCheck = await _storage.Get(memory1.Id, CancellationToken.None);
        Assert.NotNull(finalCheck);
    }
}