using System.Diagnostics;
using System.Text.Json;
using Akka.Actor;
using Akka.Hosting;
using Memorizer.Actors;
using Memorizer.Models;
using Memorizer.Models.ValueTypes;
using Memorizer.Settings;

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

        // Seed provider settings from environment configuration
        // This ensures backwards compatibility with V1 configurations and fixes
        // beta1 installations that had incorrect default values
        await SeedProviderSettingsFromConfigAsync(stoppingToken);

        // Validate embedding dimensions and set mismatch state for UI warnings
        await ValidateEmbeddingDimensions(stoppingToken);

        // Check and trigger metadata embedding migration if needed
        await CheckAndTriggerEmbeddingMigration(stoppingToken);

        // Then add default prompt
        await AddDefaultPrompt(stoppingToken);

        // Ensure all projects and workspaces have system memories for semantic search
        await SeedProjectAndWorkspaceSystemMemoriesAsync(stoppingToken);

        // Seed sample data for local development if enabled
        await SeedSampleDataAsync(stoppingToken);

        // Auto-sync markdown export if enabled
        await AutoSyncMarkdownExportAsync(stoppingToken);
    }

    private async Task SeedSampleDataAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _services.CreateScope();
            var seedDataService = scope.ServiceProvider.GetRequiredService<ISeedDataService>();
            await seedDataService.SeedAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed sample data: {Message}", ex.Message);
            // Don't fail startup - seeding is optional for development
        }
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

    /// <summary>
    /// Synchronizes provider settings between environment configuration and database.
    ///
    /// Flow:
    /// 1. If no database settings exist: seed from environment config
    /// 2. If database settings exist with localhost defaults AND environment has different config:
    ///    update database to use environment config (fixes beta1 upgrade path)
    /// 3. Load final database settings back into IConfiguration so IOptionsSnapshot picks them up
    ///
    /// This ensures backwards compatibility when upgrading from V1 or from beta1
    /// which had incorrect default values seeded in the database.
    /// </summary>
    private async Task SeedProviderSettingsFromConfigAsync(CancellationToken ct = default)
    {
        try
        {
            // Create a scope to resolve scoped services (IStorage depends on IEmbeddingService which uses IOptionsSnapshot)
            using var scope = _services.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IStorage>();
            var config = _services.GetRequiredService<IConfiguration>();

            // Get environment configuration
            var embeddingSettings = config.GetSection("Embeddings").Get<EmbeddingSettings>() ?? new EmbeddingSettings();
            var llmSettings = config.GetSection("LLM").Get<LlmSettings>() ?? new LlmSettings();

            // Step 1 & 2: Seed/update database from environment config if needed
            await SeedOrUpdateEmbeddingProviderAsync(storage, embeddingSettings, ct);
            await SeedOrUpdateAgentProviderAsync(storage, llmSettings, ct);

            // Step 3: Load database settings back into configuration
            // This ensures IOptionsSnapshot picks up the authoritative database values
            await LoadDatabaseSettingsIntoConfigurationAsync(storage, config, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync provider settings: {Message}", ex.Message);
            // Don't fail startup - the system can still work, user can configure via UI
        }
    }

    /// <summary>
    /// Loads provider settings from the database and updates the in-memory configuration.
    /// This allows IOptionsSnapshot to pick up database-configured values.
    /// </summary>
    private async Task LoadDatabaseSettingsIntoConfigurationAsync(IStorage storage, IConfiguration config, CancellationToken ct)
    {
        try
        {
            // Load embedding provider settings from database
            var embeddingProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding, ct);
            if (embeddingProvider != null)
            {
                var providerConfig = embeddingProvider.Config.RootElement;

                if (providerConfig.TryGetProperty("apiUrl", out var apiUrlProp))
                {
                    config["Embeddings:ApiUrl"] = apiUrlProp.GetString();
                }
                if (providerConfig.TryGetProperty("model", out var modelProp))
                {
                    config["Embeddings:Model"] = modelProp.GetString();
                }

                _logger.LogInformation(
                    "Loaded embedding settings from database: ApiUrl={ApiUrl}, Model={Model}",
                    config["Embeddings:ApiUrl"], config["Embeddings:Model"]);
            }

            // Load LLM/Agent provider settings from database
            var agentProvider = await storage.GetActiveProviderAsync(ProviderTypes.MemorizerAgent, ct);
            if (agentProvider != null)
            {
                var providerConfig = agentProvider.Config.RootElement;

                if (providerConfig.TryGetProperty("apiUrl", out var apiUrlProp))
                {
                    config["LLM:ApiUrl"] = apiUrlProp.GetString();
                }
                if (providerConfig.TryGetProperty("model", out var modelProp))
                {
                    config["LLM:Model"] = modelProp.GetString();
                }
                if (providerConfig.TryGetProperty("timeout", out var timeoutProp))
                {
                    config["LLM:Timeout"] = timeoutProp.GetString();
                }

                _logger.LogInformation(
                    "Loaded LLM settings from database: ApiUrl={ApiUrl}, Model={Model}",
                    config["LLM:ApiUrl"], config["LLM:Model"]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load database settings into configuration: {Message}", ex.Message);
            // Continue with environment config values
        }
    }

    private async Task SeedOrUpdateEmbeddingProviderAsync(IStorage storage, EmbeddingSettings settings, CancellationToken ct)
    {
        var existingProviders = await storage.GetAllProvidersAsync(ProviderTypes.Embedding, ct);
        var activeProvider = existingProviders.FirstOrDefault(p => p.IsActive);

        var configApiUrl = settings.ApiUrl.ToString().TrimEnd('/');
        var configModel = settings.Model;

        if (activeProvider == null)
        {
            // No provider exists - create one from config
            _logger.LogInformation(
                "No embedding provider configured - seeding from environment: ApiUrl={ApiUrl}, Model={Model}",
                configApiUrl, configModel);

            var newProvider = new ProviderSettings
            {
                Id = (ProviderSettingsId)Guid.NewGuid(),
                ProviderType = ProviderTypes.Embedding,
                ProviderName = ProviderNames.Ollama,
                DisplayName = "Ollama Embeddings",
                Config = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    apiUrl = configApiUrl,
                    model = configModel
                })),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await storage.SaveProviderSettingsAsync(newProvider, ct);
            _logger.LogInformation("Embedding provider seeded successfully from environment configuration");
        }
        else
        {
            // Provider exists - check if it needs updating (beta1 fix)
            var existingConfig = activeProvider.Config.RootElement;
            var existingApiUrl = existingConfig.TryGetProperty("apiUrl", out var urlProp)
                ? urlProp.GetString() ?? ""
                : "";
            var existingModel = existingConfig.TryGetProperty("model", out var modelProp)
                ? modelProp.GetString() ?? ""
                : "";

            // Check if existing config has localhost defaults but environment has different values
            var hasLocalhostDefault = existingApiUrl.Contains("localhost:11434") || existingApiUrl.Contains("127.0.0.1:11434");
            var envHasDifferentConfig = !configApiUrl.Contains("localhost:11434") && !configApiUrl.Contains("127.0.0.1:11434");

            if (hasLocalhostDefault && envHasDifferentConfig)
            {
                _logger.LogInformation(
                    "Updating embedding provider from localhost defaults to environment config: " +
                    "ApiUrl={OldUrl}->{NewUrl}, Model={OldModel}->{NewModel}",
                    existingApiUrl, configApiUrl, existingModel, configModel);

                var updatedProvider = new ProviderSettings
                {
                    Id = activeProvider.Id,
                    ProviderType = ProviderTypes.Embedding,
                    ProviderName = activeProvider.ProviderName,
                    DisplayName = activeProvider.DisplayName,
                    Config = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        apiUrl = configApiUrl,
                        model = configModel
                    })),
                    IsActive = true,
                    CreatedAt = activeProvider.CreatedAt,
                    UpdatedAt = DateTime.UtcNow
                };

                await storage.SaveProviderSettingsAsync(updatedProvider, ct);
                _logger.LogInformation("Embedding provider updated to use environment configuration");
            }
            else
            {
                _logger.LogDebug(
                    "Embedding provider already configured: ApiUrl={ApiUrl}, Model={Model}",
                    existingApiUrl, existingModel);
            }
        }
    }

    private async Task SeedOrUpdateAgentProviderAsync(IStorage storage, LlmSettings settings, CancellationToken ct)
    {
        var existingProviders = await storage.GetAllProvidersAsync(ProviderTypes.MemorizerAgent, ct);
        var activeProvider = existingProviders.FirstOrDefault(p => p.IsActive);

        var configApiUrl = settings.ApiUrl.ToString().TrimEnd('/');
        var configModel = settings.Model;
        var configTimeout = settings.Timeout.ToString();

        if (activeProvider == null)
        {
            // No provider exists - create one from config
            _logger.LogInformation(
                "No memorizer agent provider configured - seeding from environment: ApiUrl={ApiUrl}, Model={Model}",
                configApiUrl, configModel);

            var newProvider = new ProviderSettings
            {
                Id = (ProviderSettingsId)Guid.NewGuid(),
                ProviderType = ProviderTypes.MemorizerAgent,
                ProviderName = ProviderNames.Ollama,
                DisplayName = "Ollama (Local LLM)",
                Config = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    apiUrl = configApiUrl,
                    model = configModel,
                    timeout = configTimeout
                })),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await storage.SaveProviderSettingsAsync(newProvider, ct);
            _logger.LogInformation("Memorizer agent provider seeded successfully from environment configuration");
        }
        else
        {
            // Provider exists - check if it needs updating (beta1 fix)
            var existingConfig = activeProvider.Config.RootElement;
            var existingApiUrl = existingConfig.TryGetProperty("apiUrl", out var urlProp)
                ? urlProp.GetString() ?? ""
                : "";
            var existingModel = existingConfig.TryGetProperty("model", out var modelProp)
                ? modelProp.GetString() ?? ""
                : "";

            // Check if existing config has localhost defaults but environment has different values
            var hasLocalhostDefault = existingApiUrl.Contains("localhost:11434") || existingApiUrl.Contains("127.0.0.1:11434");
            var envHasDifferentConfig = !configApiUrl.Contains("localhost:11434") && !configApiUrl.Contains("127.0.0.1:11434");

            if (hasLocalhostDefault && envHasDifferentConfig)
            {
                _logger.LogInformation(
                    "Updating memorizer agent provider from localhost defaults to environment config: " +
                    "ApiUrl={OldUrl}->{NewUrl}, Model={OldModel}->{NewModel}",
                    existingApiUrl, configApiUrl, existingModel, configModel);

                var updatedProvider = new ProviderSettings
                {
                    Id = activeProvider.Id,
                    ProviderType = ProviderTypes.MemorizerAgent,
                    ProviderName = activeProvider.ProviderName,
                    DisplayName = activeProvider.DisplayName,
                    Config = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        apiUrl = configApiUrl,
                        model = configModel,
                        timeout = configTimeout
                    })),
                    IsActive = true,
                    CreatedAt = activeProvider.CreatedAt,
                    UpdatedAt = DateTime.UtcNow
                };

                await storage.SaveProviderSettingsAsync(updatedProvider, ct);
                _logger.LogInformation("Memorizer agent provider updated to use environment configuration");
            }
            else
            {
                _logger.LogDebug(
                    "Memorizer agent provider already configured: ApiUrl={ApiUrl}, Model={Model}",
                    existingApiUrl, existingModel);
            }
        }
    }

    private async Task AddDefaultPrompt(CancellationToken ct = default)
    {
        // Add default system memory after schema migration
        try
        {
            // Create a scope to resolve scoped services (IOptionsSnapshot requires a scope)
            using var scope = _services.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IStorage>();
            var statsService = scope.ServiceProvider.GetRequiredService<IMemoryStatsService>();

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
                confidence: new Confidence(1.0), cancellationToken: ct);

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
                confidence: new Confidence(1.0), cancellationToken: ct);

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
            // Create a scope to resolve scoped services (IOptionsSnapshot requires a scope)
            using var scope = _services.CreateScope();
            var dimensionService = scope.ServiceProvider.GetRequiredService<IEmbeddingDimensionService>();
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
                    "ACTION REQUIRED: Navigate to /tools/dimension-migration to run the migration tool");
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
            // Create a scope to resolve scoped services (IOptionsSnapshot requires a scope)
            using var scope = _services.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IStorage>();
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

    private async Task AutoSyncMarkdownExportAsync(CancellationToken ct = default)
    {
        try
        {
            var markdownSettings = _services.GetRequiredService<MarkdownExportSettings>();
            if (!markdownSettings.AutoSyncOnStartup || string.IsNullOrWhiteSpace(markdownSettings.RootPath))
            {
                _logger.LogDebug("Markdown export auto-sync disabled or not configured, skipping");
                return;
            }

            _logger.LogInformation("Auto-syncing markdown export to {RootPath}", markdownSettings.RootPath);

            using var scope = _services.CreateScope();
            var exportService = scope.ServiceProvider.GetRequiredService<IMarkdownExportService>();

            var result = await exportService.ExportAllAsync(ct: ct);

            _logger.LogInformation(
                "Markdown export auto-sync completed: {Exported} exported, {Failed} failed, {Skipped} skipped",
                result.TotalExported, result.TotalFailed, result.TotalSkipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-sync markdown export: {Message}", ex.Message);
            // Don't fail startup
        }
    }

    /// <summary>
    /// Runs one-time data migration to seed system memories for existing projects and workspaces.
    /// Uses the data_migrations table to track whether this migration has already been executed.
    /// This enables semantic search on project/workspace metadata for items that existed
    /// before this feature was implemented.
    /// </summary>
    private async Task SeedProjectAndWorkspaceSystemMemoriesAsync(CancellationToken ct = default)
    {
        const string migrationName = "seed_project_workspace_system_memories_v1";
        const string migrationDescription = "Seeds system memories for existing projects and workspaces to enable semantic search on their metadata";

        try
        {
            using var scope = _services.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IStorage>();

            // Use the one-time flag system to ensure this migration only runs once
            var wasExecuted = await storage.ExecuteDataMigrationIfNeededAsync(
                migrationName,
                migrationDescription,
                async (token) =>
                {
                    var (projectsSeeded, workspacesSeeded) = await storage.SeedProjectAndWorkspaceSystemMemoriesAsync(token);

                    _logger.LogInformation(
                        "Data migration '{MigrationName}' completed: seeded {Projects} projects, {Workspaces} workspaces",
                        migrationName, projectsSeeded, workspacesSeeded);
                },
                ct);

            if (!wasExecuted)
            {
                _logger.LogDebug("Data migration '{MigrationName}' already executed, skipping", migrationName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run data migration '{MigrationName}': {Message}", migrationName, ex.Message);
            // Don't fail startup - search will fall back to ILIKE for items without system memories
        }
    }
}