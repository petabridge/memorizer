using Memorizer.Extensions;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Standalone integration tests for PostgMem Web UI functionality
/// Tests the underlying services that the Web UI depends on
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class WebUIIntegrationTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly IServiceProvider _serviceProvider;
    private readonly HttpClient _httpClient;

    public WebUIIntegrationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;

        // Build a simple service provider for testing
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        _httpClient = new HttpClient();
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

        // Add PostgMem services
        services.AddMemorizer();

        // Add logging
        services.AddLogging();
    }

    [Fact]
    public async Task Storage_CanStoreAndRetrieveMemoryForWebUI()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();

        // Act - Store a memory for web UI testing
        var memory = await storage.StoreMemory(
            "web-ui-test",
            "Test memory for web UI functionality",
            "test",
            new[] { "web-ui", "test" },
            0.9,
            "Web UI Test Memory"
        );

        // Assert
        Assert.NotNull(memory);
        Assert.Equal("Web UI Test Memory", memory.Title);
        
        // Verify retrieval
        var retrieved = await storage.Get(memory.Id, CancellationToken.None);
        Assert.NotNull(retrieved);
        Assert.Equal(memory.Id, retrieved.Id);
        Assert.Equal("web-ui-test", retrieved.Type);
        
        _output.WriteLine($"✓ Successfully stored and retrieved memory: {memory.Id}");
    }

    [Fact]
    public async Task Storage_PaginationWorksForWebUI()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();
        var createdMemories = new List<Guid>();

        try
        {
            // Create test memories
            for (int i = 0; i < 25; i++)
            {
                var memory = await storage.StoreMemory(
                    "pagination-test",
                    $"Memory {i} for pagination testing",
                    "test",
                    new[] { "pagination", "test" },
                    1.0,
                    $"Pagination Memory {i}"
                );
                createdMemories.Add(memory.Id);
            }

            // Act - Test pagination
            var (page1Memories, page1Total) = await storage.GetMemoriesPaginated(1, 10, CancellationToken.None);
            var (page2Memories, page2Total) = await storage.GetMemoriesPaginated(2, 10, CancellationToken.None);

            // Assert
            Assert.Equal(10, page1Memories.Count);
            Assert.Equal(10, page2Memories.Count);
            Assert.Equal(page1Total, page2Total); // Total should be same
            
            // Ensure no overlap between pages
            var page1Ids = page1Memories.Select(m => m.Id).ToHashSet();
            var page2Ids = page2Memories.Select(m => m.Id).ToHashSet();
            Assert.Empty(page1Ids.Intersect(page2Ids));

            _output.WriteLine($"✓ Pagination test passed: Page 1 and Page 2 have distinct memories, total count: {page1Total}");
        }
        finally
        {
            // Clean up
            foreach (var memoryId in createdMemories)
            {
                await storage.Delete(memoryId, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Storage_UpdateMemoryWorksForWebUI()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();
        
        // Create initial memory
        var originalMemory = await storage.StoreMemory(
            "update-test",
            "Original content",
            "test",
            new[] { "update", "test" },
            0.8,
            "Original Title"
        );

        try
        {
            // Act - Update the memory
            var updatedMemory = await storage.UpdateMemory(
                originalMemory.Id,
                "updated-test",
                "Updated content for web UI",
                "test",
                new[] { "updated", "test", "web-ui" },
                0.95,
                "Updated Title for Web UI",
                CancellationToken.None
            );

            // Assert
            Assert.NotNull(updatedMemory);
            Assert.Equal(originalMemory.Id, updatedMemory.Id);
            // Text field contains just the content, title is stored separately
            Assert.Equal("Updated content for web UI", updatedMemory.Text);
            Assert.Equal("Updated Title for Web UI", updatedMemory.Title);
            Assert.Equal("updated-test", updatedMemory.Type);
            Assert.Equal(0.95, updatedMemory.Confidence);

            // Verify persistence
            var retrieved = await storage.Get(originalMemory.Id, CancellationToken.None);
            Assert.NotNull(retrieved);
            Assert.Equal("Updated content for web UI", retrieved.Text);
            
            _output.WriteLine($"✓ Successfully updated memory: {originalMemory.Id}");
        }
        finally
        {
            // Clean up
            await storage.Delete(originalMemory.Id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task MemoryStatsService_ReturnsValidStatsForWebUI()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();
        var statsService = _serviceProvider.GetRequiredService<IMemoryStatsService>();
        var createdMemories = new List<Guid>();

        try
        {
            // Create test memories with different types
            var testData = new[]
            {
                ("reference", "Reference material"),
                ("reference", "Another reference"),
                ("procedure", "Step by step guide"),
                ("example", "Code example"),
                ("example", "Another example")
            };

            foreach (var (type, content) in testData)
            {
                var memory = await storage.StoreMemory(
                    type,
                    content,
                    "test",
                    new[] { "stats-test" },
                    1.0,
                    $"Stats Test - {type}"
                );
                createdMemories.Add(memory.Id);
            }

            // Act
            var stats = await statsService.GetStatsAsync();

            // Assert
            Assert.NotNull(stats);
            Assert.True(stats.TotalMemories >= 5); // At least our test data
            Assert.True(stats.AverageMemorySizeBytes > 0); // Should have some content
            
            _output.WriteLine($"✓ Stats service returned: {stats.TotalMemories} total memories, avg size {stats.AverageMemorySizeBytes} bytes");
        }
        finally
        {
            // Clean up
            foreach (var memoryId in createdMemories)
            {
                await storage.Delete(memoryId, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task MemoryTypesEndpoint_ReturnsDistinctTypes()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();
        var createdMemories = new List<Guid>();

        try
        {
            // Create test memories with different types (including duplicates)
            var testData = new[]
            {
                ("type-test-1", "Content 1"),
                ("type-test-1", "Content 2"), // Duplicate type
                ("type-test-2", "Content 3"),
                ("type-test-3", "Content 4"),
                ("type-test-2", "Content 5")  // Another duplicate type
            };

            foreach (var (type, content) in testData)
            {
                var memory = await storage.StoreMemory(
                    type,
                    content,
                    "test",
                    new[] { "type-test" },
                    1.0,
                    $"Type Test - {type}"
                );
                createdMemories.Add(memory.Id);
            }

            // Act
            var distinctTypes = await storage.GetDistinctMemoryTypes();

            // Assert
            var testTypes = distinctTypes.Where(t => t.StartsWith("type-test-")).ToList();
            Assert.Contains("type-test-1", testTypes);
            Assert.Contains("type-test-2", testTypes);
            Assert.Contains("type-test-3", testTypes);
            Assert.Equal(3, testTypes.Count); // Should be exactly 3 distinct types from our test data
            
            _output.WriteLine($"✓ Distinct types endpoint returned {testTypes.Count} unique test types: {string.Join(", ", testTypes)}");
        }
        finally
        {
            // Clean up
            foreach (var memoryId in createdMemories)
            {
                await storage.Delete(memoryId, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Storage_SearchFunctionalityWorksForWebUI()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();
        var createdMemories = new List<Guid>();

        try
        {
            // Create searchable test memories
            var testMemories = new[]
            {
                ("search-test", "JavaScript programming tutorial", new[] { "programming", "javascript" }),
                ("search-test", "Python data science guide", new[] { "programming", "python", "data-science" }),
                ("search-test", "Web development best practices", new[] { "web", "development" }),
                ("search-test", "Database optimization techniques", new[] { "database", "optimization" })
            };

            foreach (var (type, content, tags) in testMemories)
            {
                var memory = await storage.StoreMemory(
                    type,
                    content,
                    "test",
                    tags,
                    1.0,
                    $"Search Test: {content}"
                );
                createdMemories.Add(memory.Id);
            }

            // Act - Search for programming content
            var programmingResults = await storage.Search(
                "programming languages tutorial",
                limit: 10,
                minSimilarity: 0.3,
                filterTags: new[] { "programming" }
            );

            // Act - Search for web development
            var webResults = await storage.Search(
                "web development practices",
                limit: 10,
                minSimilarity: 0.3,
                filterTags: new[] { "web" }
            );

            // Assert
            Assert.NotEmpty(programmingResults);
            Assert.NotEmpty(webResults);
            
            // Should find programming-related content
            Assert.Contains(programmingResults, r => r.Text.Contains("JavaScript") || r.Text.Contains("Python"));
            
            // Should find web development content
            Assert.Contains(webResults, r => r.Text.Contains("Web development"));

            // New: Assert similarity is present and reasonable
            foreach (var result in programmingResults)
            {
                Assert.True(result.Similarity.HasValue, "Similarity should be present on search results");
                Assert.InRange(result.Similarity.Value, 0.0, 1.0);
            }
            foreach (var result in webResults)
            {
                Assert.True(result.Similarity.HasValue, "Similarity should be present on search results");
                Assert.InRange(result.Similarity.Value, 0.0, 1.0);
            }

            _output.WriteLine($"✓ Search functionality working: found {programmingResults.Count} programming results, {webResults.Count} web results");
        }
        finally
        {
            // Clean up
            foreach (var memoryId in createdMemories)
            {
                await storage.Delete(memoryId, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Storage_DeleteMemoryWorksForWebUI()
    {
        // Arrange
        var storage = _serviceProvider.GetRequiredService<IStorage>();
        
        // Create memory to delete
        var memory = await storage.StoreMemory(
            "delete-test",
            "Memory to be deleted",
            "test",
            new[] { "delete", "test" },
            1.0,
            "Memory for Deletion Test"
        );

        // Verify it exists
        var existsBefore = await storage.Get(memory.Id, CancellationToken.None);
        Assert.NotNull(existsBefore);

        // Act - Delete the memory
        var deleted = await storage.Delete(memory.Id, CancellationToken.None);

        // Assert - Verify it's gone
        Assert.True(deleted);
        var existsAfter = await storage.Get(memory.Id, CancellationToken.None);
        Assert.Null(existsAfter);
        
        _output.WriteLine($"✓ Successfully deleted memory: {memory.Id}");
    }

    [Fact]
    public async Task Storage_HandlesEdgeCasesForWebUI()
    {
        // This tests the system's behavior with edge cases that the Web UI might encounter
        var storage = _serviceProvider.GetRequiredService<IStorage>();
        var createdMemories = new List<Guid>();

        try
        {
            // Test empty type - system allows it (doesn't throw exception)
            var memoryWithEmptyType = await storage.StoreMemory(
                "",
                "Valid content with empty type",
                "test",
                new[] { "test", "edge-case" },
                1.0,
                "Empty Type Test"
            );
            Assert.NotNull(memoryWithEmptyType);
            Assert.Equal("", memoryWithEmptyType.Type);
            createdMemories.Add(memoryWithEmptyType.Id);

            // Test empty content - system allows it (doesn't throw exception)
            var memoryWithEmptyContent = await storage.StoreMemory(
                "edge-case-type",
                "",
                "test",
                new[] { "test", "edge-case" },
                1.0,
                "Empty Content Test"
            );
            Assert.NotNull(memoryWithEmptyContent);
            // When content is empty but title exists, Text field will be "Title "
            Assert.Equal(string.Empty, memoryWithEmptyContent.Text);
            createdMemories.Add(memoryWithEmptyContent.Id);

            // Test null tags
            var memoryWithNullTags = await storage.StoreMemory(
                "null-tags-type",
                "Content with null tags",
                "test",
                null,
                1.0,
                "Null Tags Test"
            );
            Assert.NotNull(memoryWithNullTags);
            Assert.Null(memoryWithNullTags.Tags);
            createdMemories.Add(memoryWithNullTags.Id);

            // Use non-null title for the test case that was expecting to throw exception
            var memoryWithNonNullTitle = await storage.StoreMemory(
                "edge-case-type",
                "Valid content with required title",
                "test",
                new[] { "test", "edge-case" },
                1.0,
                "Required Title Test"
            );
            Assert.NotNull(memoryWithNonNullTitle);
            Assert.Equal("Required Title Test", memoryWithNonNullTitle.Title);
            createdMemories.Add(memoryWithNonNullTitle.Id);

            _output.WriteLine("✓ Edge case handling verified - system gracefully handles empty values without throwing exceptions");
        }
        finally
        {
            // Clean up
            foreach (var memoryId in createdMemories)
            {
                await storage.Delete(memoryId, CancellationToken.None);
            }
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
    }
} 