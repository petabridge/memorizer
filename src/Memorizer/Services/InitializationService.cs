using System.Diagnostics;
using Akka.Actor;
using Akka.Hosting;
using Memorizer.Actors;

namespace Memorizer.Services;

public sealed class InitializationService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<InitializationService> _logger;
    private readonly IHostApplicationLifetime _appLifetime;

    public InitializationService(IServiceProvider serviceProvider, ILogger<InitializationService> logger, IHostApplicationLifetime appLifetime)
    {
        _services = serviceProvider;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run migrations first as they are critical
        await RunMigrationsAsync(stoppingToken);
        
        // Check and trigger metadata embedding migration if needed
        await CheckAndTriggerEmbeddingMigration(stoppingToken);
        
        // Then add default prompt
        await AddDefaultPrompt(stoppingToken);
    }
    
    private async Task RunMigrationsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Running schema migration at startup");

        try
        {
            var connectionString = _services.GetRequiredService<IConfiguration>().GetConnectionString("Storage");
            Debug.Assert(connectionString != null, nameof(connectionString) + " != null");
            await SchemaMigrator.MigrateAsync(connectionString, ct);
            _logger.LogInformation("Schema migration completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema migration failed: {Message}", ex.Message);
            _appLifetime.StopApplication();
            throw;
        }
    }

    private async Task AddDefaultPrompt(CancellationToken ct = default)
    {
        // Add default system memory after schema migration
        try
        {
            var storage = _services.GetRequiredService<IStorage>();
            var embeddingService = _services.GetRequiredService<IEmbeddingService>();
            var statsService = _services.GetRequiredService<IMemoryStatsService>();

            var currentStats = await statsService.GetStatsAsync(ct);
            if (currentStats.TotalMemories > 0)
            {
                _logger.LogInformation("System memory already exists, skipping creation");
                return;
            }

            _logger.LogInformation("Creating/updating system description memory");

            // Create a markdown string describing the system
            string systemDescription = @"# PostgMem Memory Service

PostgMem is a PostgreSQL-based memory service for AI agents that enables persistent knowledge storage and retrieval using vector embeddings.

## Usage

Agents can use this service to store and retrieve knowledge across sessions, building up a persistent memory bank that can be searched semantically.

## Capabilities

- **Store memories** with types, plain text content, titles, tags, and confidence scores
- **Search for memories** using semantic similarity to find relevant knowledge
- **Retrieve specific memories** by their unique ID
- **Delete memories** when they are no longer needed
- **Create relationships** between related memories for knowledge graphs

## API Reference

### Store
Store new memories with:
- `type`: Category of memory (e.g., 'reference', 'how-to', 'conversation')
- `text`: Plain text content (markdown, code, prose)
- `source`: Origin of the memory (e.g., 'user', 'system', 'LLM')
- `title`: Optional descriptive title
- `tags`: Optional array of tags for categorization
- `confidence`: Confidence score (0.0 to 1.0)

### Search
Find similar memories using vector similarity search:
- `query`: Natural language search query
- `limit`: Maximum results to return
- `minSimilarity`: Minimum similarity threshold
- `filterTags`: Optional tag filters

### Get
Retrieve a specific memory by its ID

### Delete
Remove a memory by its ID";

            // Store the system description directly using IStorage
            await storage.StoreMemory(
                type: "system",
                content: systemDescription,
                source: "system",
                title: "PostgMem System Documentation",
                tags: ["system", "documentation", "help", "reference"],
                confidence: 1.0, cancellationToken: ct);

            _logger.LogInformation("System description memory created/updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create system memory: {Message}", ex.Message);
        }
    }

    private async Task CheckAndTriggerEmbeddingMigration(CancellationToken ct = default)
    {
        try
        {
            var storage = _services.GetRequiredService<IStorage>();
            var countWithoutMetadata = await storage.CountMemoriesWithoutMetadataEmbeddings(ct);
            
            if (countWithoutMetadata > 0)
            {
                _logger.LogInformation("Found {Count} memories without metadata embeddings, triggering background processing", countWithoutMetadata);
                
                // Get the MetadataEmbeddingActor using IRequiredActor
                var metadataActor = _services.GetRequiredService<IRequiredActor<MetadataEmbeddingActorKey>>();
                
                var message = new RegenerateAllMetadataEmbeddings(
                    PageSize: 50, // Smaller batches during migration
                    RequestedBy: "startup-migration"
                );
                
                metadataActor.ActorRef.Tell(message, ActorRefs.NoSender);
                _logger.LogInformation("Metadata embeddings migration background task started");
            }
            else
            {
                _logger.LogDebug("All memories have metadata embeddings, no migration needed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check or trigger metadata embedding migration: {Message}", ex.Message);
        }
    }
}