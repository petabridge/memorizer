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

        // Validate embedding dimensions and set mismatch state for UI warnings
        await ValidateEmbeddingDimensions(stoppingToken);

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

            // Create a sample memory with Mermaid diagrams to demonstrate the feature
            string mermaidDemoContent = @"# Memorizer Architecture Overview

This document demonstrates Mermaid diagram support in Memorizer.

## System Architecture

The following diagram shows the high-level architecture of the Memorizer system:

```mermaid
flowchart TB
    subgraph Client[""Client Layer""]
        UI[Web UI]
        MCP[MCP Server]
        API[REST API]
    end

    subgraph Core[""Core Services""]
        Storage[IStorage]
        Embedding[EmbeddingService]
        LLM[LlmService]
    end

    subgraph Actors[""Akka.NET Actors""]
        TitleGen[TitleGenerationActor]
        EmbedRegen[EmbeddingRegenerationActor]
    end

    subgraph External[""External Services""]
        Ollama[Ollama]
        PG[(PostgreSQL + pgvector)]
    end

    UI --> API
    MCP --> Storage
    API --> Storage
    Storage --> PG
    Storage --> Embedding
    Embedding --> Ollama
    LLM --> Ollama
    TitleGen --> LLM
    EmbedRegen --> Embedding
```

## Request Flow

Here's how a typical memory storage request flows through the system:

```mermaid
sequenceDiagram
    participant C as Client
    participant A as API Controller
    participant S as IStorage
    participant E as EmbeddingService
    participant O as Ollama
    participant DB as PostgreSQL

    C->>A: POST /api/memory
    A->>S: StoreMemory()
    S->>E: GetEmbedding(content)
    E->>O: Generate embedding
    O-->>E: Vector [384 dims]
    E-->>S: Embedding result
    S->>E: GetEmbedding(metadata)
    E->>O: Generate embedding
    O-->>E: Vector [384 dims]
    E-->>S: Embedding result
    S->>DB: INSERT memory + embeddings
    DB-->>S: Success
    S-->>A: Memory ID
    A-->>C: 201 Created
```

## Memory States

Memories can be in different states during their lifecycle:

```mermaid
stateDiagram-v2
    [*] --> Created: Store memory
    Created --> Indexed: Embeddings generated
    Indexed --> Updated: Edit content
    Updated --> Indexed: Re-embed
    Indexed --> Deleted: Delete request
    Deleted --> [*]

    note right of Created
        Memory stored but
        may need embedding
    end note

    note right of Indexed
        Fully searchable
        via vector similarity
    end note
```

## Component Dependencies

```mermaid
graph LR
    A[Memorizer.Web] --> B[Memorizer.Core]
    B --> C[Npgsql]
    B --> D[Pgvector]
    B --> E[Akka.NET]
    A --> F[Bootstrap 5]
    A --> G[Mermaid.js]
    A --> H[Prism.js]
```

This demonstrates how Mermaid diagrams render in both light and dark themes!";

            await storage.StoreMemory(
                type: "reference",
                content: mermaidDemoContent,
                source: "system",
                title: "Memorizer Architecture with Mermaid Diagrams",
                tags: ["architecture", "mermaid", "diagrams", "documentation", "demo"],
                confidence: 1.0, cancellationToken: ct);

            _logger.LogInformation("Mermaid diagram demo memory created successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create system memory: {Message}", ex.Message);
        }
    }

    private async Task ValidateEmbeddingDimensions(CancellationToken ct = default)
    {
        try
        {
            var dimensionService = _services.GetRequiredService<IEmbeddingDimensionService>();
            var mismatchState = _services.GetRequiredService<IDimensionMismatchState>();

            _logger.LogInformation("Validating embedding dimensions...");

            var validation = await dimensionService.ValidateAsync(ct);
            mismatchState.Update(validation);

            if (validation.HasMismatch)
            {
                // Log as ERROR to ensure visibility in production logs
                _logger.LogError(
                    "╔══════════════════════════════════════════════════════════════════╗");
                _logger.LogError(
                    "║  EMBEDDING DIMENSION MISMATCH DETECTED                           ║");
                _logger.LogError(
                    "╚══════════════════════════════════════════════════════════════════╝");
                _logger.LogError(
                    "Mismatch Details: {Description}", validation.MismatchDescription);
                _logger.LogError(
                    "  - Configured Model: {Model}", validation.ConfiguredModel);
                _logger.LogError(
                    "  - Detected Dimensions: {Detected}", validation.DetectedModelDimensions?.ToString() ?? "Unknown (API unavailable)");
                _logger.LogError(
                    "  - Stored Dimensions: {Stored}", validation.StoredDimensions?.ToString() ?? "None");
                _logger.LogError(
                    "  - Schema Dimensions: VECTOR({Schema})", validation.DatabaseSchemaDimensions?.ToString() ?? "Unknown");
                _logger.LogError(
                    "ACTION REQUIRED: Navigate to /ui/tools/dimension-migration to run the migration tool");
                _logger.LogError(
                    "DOCUMENTATION: https://github.com/petabridge/memorizer-v1/blob/dev/docs/embedding-models.md");
            }
            else if (!validation.EmbeddingApiAvailable)
            {
                _logger.LogWarning(
                    "Embedding API unavailable - unable to detect model dimensions. " +
                    "Trusting stored/schema configuration.");
            }
            else
            {
                _logger.LogInformation(
                    "Embedding dimensions validated: model={Model}, dimensions={Dimensions}",
                    validation.ConfiguredModel,
                    validation.DetectedModelDimensions ?? validation.StoredDimensions ?? validation.DatabaseSchemaDimensions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate embedding dimensions: {Message}", ex.Message);
            // Don't fail startup - just log the warning
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

                // Get the EmbeddingRegenerationActor using IRequiredActor
                var embeddingActor = _services.GetRequiredService<IRequiredActor<EmbeddingRegenerationActorKey>>();

                var message = new RegenerateAllEmbeddings(
                    PageSize: 50, // Smaller batches during migration
                    RequestedBy: "startup-migration"
                );

                embeddingActor.ActorRef.Tell(message, ActorRefs.NoSender);
                _logger.LogInformation("Embedding regeneration migration background task started");
            }
            else
            {
                _logger.LogDebug("All memories have metadata embeddings, no migration needed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check or trigger embedding migration: {Message}", ex.Message);
        }
    }
}