using Akka.Actor;
using Akka.Hosting;
using Memorizer.Actors;
using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;

namespace Memorizer.Controllers;

[Route("tools")]
public class ToolsController : Controller
{
    private readonly IActorRef _titleGenerationActor;
    private readonly IActorRef _embeddingRegenerationActor;
    private readonly IActorRef _versionPurgeActor;
    private readonly IActorRef _dimensionMigrationActor;
    private readonly IActorRef _markdownExportActor;
    private readonly IOptionsSnapshot<LlmSettings> _llmSettingsSnapshot;
    private readonly IEmbeddingDimensionService _dimensionService;
    private readonly IDimensionMismatchState _mismatchState;
    private readonly MarkdownExportSettings _markdownExportSettings;
    private readonly ILogger<ToolsController> _logger;

    // Convenience property to access current settings
    private LlmSettings LlmSettingsValue => _llmSettingsSnapshot.Value;

    public ToolsController(
        IRequiredActor<TitleGenerationActorKey> titleGenerationActor,
        IRequiredActor<EmbeddingRegenerationActorKey> embeddingRegenerationActor,
        IRequiredActor<VersionPurgeActorKey> versionPurgeActor,
        IRequiredActor<DimensionMigrationActorKey> dimensionMigrationActor,
        IRequiredActor<MarkdownExportActorKey> markdownExportActor,
        IOptionsSnapshot<LlmSettings> llmSettingsSnapshot,
        IEmbeddingDimensionService dimensionService,
        IDimensionMismatchState mismatchState,
        MarkdownExportSettings markdownExportSettings,
        ILogger<ToolsController> logger)
    {
        _titleGenerationActor = titleGenerationActor.ActorRef;
        _embeddingRegenerationActor = embeddingRegenerationActor.ActorRef;
        _versionPurgeActor = versionPurgeActor.ActorRef;
        _dimensionMigrationActor = dimensionMigrationActor.ActorRef;
        _markdownExportActor = markdownExportActor.ActorRef;
        _llmSettingsSnapshot = llmSettingsSnapshot;
        _dimensionService = dimensionService;
        _mismatchState = mismatchState;
        _markdownExportSettings = markdownExportSettings;
        _logger = logger;
    }

    /// <summary>
    /// Display the tools main page
    /// </summary>
    [HttpGet]
    [Route("")]
    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Display the title generation tool page
    /// </summary>
    [HttpGet]
    [Route("title-generation")]
    public IActionResult TitleGeneration()
    {
        return View();
    }

    /// <summary>
    /// Display the embedding regeneration tool page
    /// </summary>
    [HttpGet]
    [Route("embedding-regeneration")]
    public IActionResult EmbeddingRegeneration()
    {
        return View();
    }

    /// <summary>
    /// Start title generation for untitled memories
    /// </summary>
    [HttpPost]
    [Route("start-title-generation")]
    public async Task<IActionResult> StartTitleGeneration(int batchSize = 50)
    {
        try
        {
            // Check if LLM is properly configured before starting
            var configStatus = CheckLlmConfiguration();
            if (!configStatus.IsConfigured)
            {
                _logger.LogWarning("Title generation requested but LLM not configured: {Reason}", configStatus.Reason);
                return Json(new {
                    success = false,
                    message = $"LLM not configured: {configStatus.Reason}",
                    requiresConfiguration = true
                });
            }

            _logger.LogInformation("Starting title generation tool for batch size {BatchSize}", batchSize);

            var generateMessage = new GenerateTitlesForUntitled
            {
                BatchSize = batchSize,
                RequestedBy = User.Identity?.Name ?? "Anonymous"
            };

            // Use Ask to wait for the actor to start the job - this ensures the SSE subscription
            // will see the job as Running (not Idle) when it connects
            var startStatus = await _titleGenerationActor.Ask<TitleGenerationStatus>(
                generateMessage, TimeSpan.FromSeconds(30));

            return Json(new {
                success = true,
                message = $"Title generation started for up to {batchSize} memories using model '{LlmSettingsValue.Model}'",
                totalItems = startStatus.Outstanding + startStatus.TotalProcessed,
                isRunning = startStatus.IsRunning
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting title generation: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Get the status of title generation operations
    /// </summary>
    [HttpGet]
    [Route("title-generation-status")]
    public IActionResult GetTitleGenerationStatus()
    {
        try
        {
            // TODO: Implement status checking via persistent actor state or event stream
            // For now, return a placeholder response
            return Json(new { 
                success = true, 
                status = "idle", 
                message = "No active title generation jobs" 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting title generation status: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Check LLM configuration status
    /// </summary>
    [HttpGet]
    [Route("llm-status")]
    public IActionResult GetLlmStatus()
    {
        try
        {
            var configStatus = CheckLlmConfiguration();
            return Json(new {
                success = true,
                isConfigured = configStatus.IsConfigured,
                reason = configStatus.Reason,
                model = LlmSettingsValue.Model,
                apiUrl = LlmSettingsValue.ApiUrl.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking LLM status: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    private (bool IsConfigured, string Reason) CheckLlmConfiguration()
    {
        var llmSettings = LlmSettingsValue;

        // Check if we have basic LLM settings
        if (llmSettings == null)
        {
            return (false, "LLM settings not found in configuration");
        }

        // Check if model is configured
        if (string.IsNullOrWhiteSpace(llmSettings.Model))
        {
            return (false, "No LLM model configured. Please configure the 'LLM:Model' setting.");
        }

        // Check if API URL is configured
        if (llmSettings.ApiUrl == null)
        {
            return (false, "LLM service URL not configured. Please configure 'LLM:ApiUrl' setting.");
        }

        return (true, "LLM is properly configured");
    }

    /// <summary>
    /// Get the status of embedding regeneration operations
    /// </summary>
    [HttpGet]
    [Route("embedding-regeneration-status")]
    public async Task<IActionResult> GetEmbeddingRegenerationStatus()
    {
        try
        {
            var status = await _embeddingRegenerationActor.Ask<EmbeddingRegenerationStatus>(new GetEmbeddingRegenerationStatus(), TimeSpan.FromSeconds(5));
            return Json(new {
                success = true,
                status = status.Status,
                isRunning = status.IsRunning,
                outstanding = status.Outstanding,
                totalProcessed = status.TotalProcessed,
                totalSuccessful = status.TotalSuccessful,
                totalFailed = status.TotalFailed,
                requestedBy = status.RequestedBy,
                startTime = status.StartTime,
                duration = status.Duration?.TotalSeconds,
                failedMemoryIds = status.FailedMemoryIds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting embedding regeneration status: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Start embedding regeneration for ALL memories using pagination.
    /// Regenerates both content embeddings and metadata embeddings.
    /// </summary>
    [HttpPost]
    [Route("start-embedding-regeneration")]
    public async Task<IActionResult> StartEmbeddingRegeneration(int pageSize = 100)
    {
        try
        {
            // Check if a batch is already running
            var status = await _embeddingRegenerationActor.Ask<EmbeddingRegenerationStatus>(new GetEmbeddingRegenerationStatus(), TimeSpan.FromSeconds(5));
            if (status.IsRunning)
            {
                return Json(new {
                    success = false,
                    message = "An embedding regeneration batch job is already in progress. Please wait for it to complete before starting a new one.",
                    status = "running",
                    outstanding = status.Outstanding,
                    totalProcessed = status.TotalProcessed,
                    totalSuccessful = status.TotalSuccessful,
                    totalFailed = status.TotalFailed,
                    requestedBy = status.RequestedBy
                });
            }

            _logger.LogInformation("Starting embedding regeneration for ALL memories with page size {PageSize}", pageSize);

            var generateMessage = new RegenerateAllEmbeddings(
                PageSize: pageSize,
                RequestedBy: User.Identity?.Name ?? "Anonymous"
            );

            // Use Ask to wait for the actor to start the job - this ensures the SSE subscription
            // will see the job as Running (not Idle) when it connects
            var startStatus = await _embeddingRegenerationActor.Ask<EmbeddingRegenerationStatus>(
                generateMessage, TimeSpan.FromSeconds(30));

            return Json(new {
                success = true,
                message = $"Embedding regeneration started for ALL memories with page size {pageSize}",
                totalItems = startStatus.Outstanding + startStatus.TotalProcessed,
                isRunning = startStatus.IsRunning
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting embedding regeneration: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Display the version purge tool page
    /// </summary>
    [HttpGet]
    [Route("version-purge")]
    public IActionResult VersionPurge()
    {
        return View();
    }

    /// <summary>
    /// Start version purge for versions older than the specified number of days
    /// </summary>
    [HttpPost]
    [Route("start-version-purge")]
    public async Task<IActionResult> StartVersionPurge(int daysOld = 30)
    {
        try
        {
            // Check if a purge is already running
            var status = await _versionPurgeActor.Ask<VersionPurgeStatus>(new GetVersionPurgeStatus(), TimeSpan.FromSeconds(5));
            if (status.IsRunning)
            {
                return Json(new {
                    success = false,
                    message = "A version purge operation is already in progress. Please wait for it to complete before starting a new one.",
                    status = "running",
                    outstanding = status.Outstanding,
                    totalProcessed = status.TotalProcessed,
                    totalSuccessful = status.TotalSuccessful,
                    totalFailed = status.TotalFailed,
                    requestedBy = status.RequestedBy
                });
            }

            _logger.LogInformation("Starting version purge for versions older than {DaysOld} days", daysOld);

            var purgeMessage = new PurgeVersionsByAge
            {
                DaysOld = daysOld,
                RequestedBy = User.Identity?.Name ?? "Anonymous"
            };

            // Use Ask to wait for the actor to start the job - this ensures the SSE subscription
            // will see the job as Running (not Idle) when it connects
            var startStatus = await _versionPurgeActor.Ask<VersionPurgeStatus>(
                purgeMessage, TimeSpan.FromSeconds(30));

            return Json(new {
                success = true,
                message = $"Version purge started for versions older than {daysOld} days",
                isRunning = startStatus.IsRunning
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting version purge: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Get the status of version purge operations
    /// </summary>
    [HttpGet]
    [Route("version-purge-status")]
    public async Task<IActionResult> GetVersionPurgeStatus()
    {
        try
        {
            var status = await _versionPurgeActor.Ask<VersionPurgeStatus>(new GetVersionPurgeStatus(), TimeSpan.FromSeconds(5));
            return Json(new {
                success = true,
                status = status.Status,
                isRunning = status.IsRunning,
                outstanding = status.Outstanding,
                totalProcessed = status.TotalProcessed,
                totalSuccessful = status.TotalSuccessful,
                totalFailed = status.TotalFailed,
                requestedBy = status.RequestedBy,
                startTime = status.StartTime,
                duration = status.Duration?.TotalSeconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting version purge status: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    #region Dimension Migration

    /// <summary>
    /// Display the dimension migration tool page
    /// </summary>
    [HttpGet]
    [Route("dimension-migration")]
    public IActionResult DimensionMigration()
    {
        return View();
    }

    /// <summary>
    /// Get the current dimension validation status
    /// </summary>
    [HttpGet]
    [Route("dimension-status")]
    public async Task<IActionResult> GetDimensionStatus()
    {
        try
        {
            // Re-validate to get fresh status
            var validation = await _dimensionService.ValidateAsync();
            _mismatchState.Update(validation);

            return Json(new {
                success = true,
                configuredModel = validation.ConfiguredModel,
                detectedDimensions = validation.DetectedModelDimensions,
                storedDimensions = validation.StoredDimensions,
                storedModel = validation.StoredModel,
                schemaDimensions = validation.DatabaseSchemaDimensions,
                hasMismatch = validation.HasMismatch,
                mismatchDescription = validation.MismatchDescription,
                requiresMigration = validation.RequiresMigration,
                embeddingApiAvailable = validation.EmbeddingApiAvailable
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dimension status: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Get the status of dimension migration operations
    /// </summary>
    [HttpGet]
    [Route("dimension-migration-status")]
    public async Task<IActionResult> GetDimensionMigrationStatus()
    {
        try
        {
            var status = await _dimensionMigrationActor.Ask<DimensionMigrationStatus>(
                new GetDimensionMigrationStatus(), TimeSpan.FromSeconds(5));

            return Json(new {
                success = true,
                status = status.Status,
                isRunning = status.IsRunning,
                oldDimensions = status.OldDimensions,
                newDimensions = status.NewDimensions,
                oldModel = status.OldModel,
                newModel = status.NewModel,
                totalMemories = status.TotalMemories,
                processed = status.Processed,
                successful = status.Successful,
                failed = status.Failed,
                startTime = status.StartTime,
                duration = status.Duration?.TotalSeconds,
                migrationId = status.MigrationId,
                requestedBy = status.RequestedBy,
                errorMessage = status.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dimension migration status: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Start a dimension migration to update the embedding schema and regenerate all embeddings
    /// </summary>
    [HttpPost]
    [Route("start-dimension-migration")]
    public async Task<IActionResult> StartDimensionMigration()
    {
        try
        {
            // First check if there's actually a mismatch
            var validation = await _dimensionService.ValidateAsync();

            if (!validation.RequiresMigration)
            {
                return Json(new {
                    success = false,
                    message = "No dimension migration required. Configured model dimensions match the database schema.",
                    hasMismatch = false
                });
            }

            if (!validation.EmbeddingApiAvailable)
            {
                return Json(new {
                    success = false,
                    message = "Embedding API is not available. Cannot detect model dimensions to perform migration.",
                    embeddingApiAvailable = false
                });
            }

            _logger.LogInformation(
                "Starting dimension migration: {Description}",
                validation.MismatchDescription);

            var startMessage = new StartDimensionMigration(
                RequestedBy: User.Identity?.Name ?? "Anonymous"
            );

            // Use Ask to wait for the actor to start the job
            var status = await _dimensionMigrationActor.Ask<DimensionMigrationStatus>(
                startMessage, TimeSpan.FromSeconds(30));

            return Json(new {
                success = status.IsRunning || status.Status == "No migration required",
                message = status.IsRunning
                    ? $"Dimension migration started: {validation.StoredDimensions ?? validation.DatabaseSchemaDimensions} → {validation.DetectedModelDimensions} dimensions"
                    : status.ErrorMessage ?? status.Status,
                isRunning = status.IsRunning,
                migrationId = status.MigrationId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting dimension migration: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Resume an interrupted dimension migration
    /// </summary>
    [HttpPost]
    [Route("resume-dimension-migration")]
    public async Task<IActionResult> ResumeDimensionMigration([FromBody] ResumeMigrationRequest request)
    {
        try
        {
            if (request.MigrationId == Guid.Empty)
            {
                return Json(new { success = false, message = "Migration ID is required" });
            }

            _logger.LogInformation("Resuming dimension migration {MigrationId}", request.MigrationId);

            var resumeMessage = new ResumeDimensionMigration(
                MigrationId: request.MigrationId,
                RequestedBy: User.Identity?.Name ?? "Anonymous"
            );

            var status = await _dimensionMigrationActor.Ask<DimensionMigrationStatus>(
                resumeMessage, TimeSpan.FromSeconds(30));

            return Json(new {
                success = status.IsRunning,
                message = status.IsRunning
                    ? $"Dimension migration resumed"
                    : status.ErrorMessage ?? status.Status,
                isRunning = status.IsRunning,
                migrationId = status.MigrationId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming dimension migration: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    public class ResumeMigrationRequest
    {
        public Guid MigrationId { get; set; }
    }

    #endregion

    #region Markdown Export

    [HttpGet]
    [Route("markdown-export")]
    public IActionResult MarkdownExport()
    {
        ViewData["IsEnabled"] = !string.IsNullOrWhiteSpace(_markdownExportSettings.RootPath);
        ViewData["RootPath"] = _markdownExportSettings.RootPath ?? "(not configured)";
        return View();
    }

    [HttpPost]
    [Route("start-markdown-export")]
    public async Task<IActionResult> StartMarkdownExport(Guid? workspaceId = null, Guid? projectId = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_markdownExportSettings.RootPath))
            {
                return Json(new { success = false, message = "Markdown export is not configured. Set MarkdownExport:RootPath in configuration." });
            }

            var status = await _markdownExportActor.Ask<MarkdownExportStatus>(new GetMarkdownExportStatus(), TimeSpan.FromSeconds(5));
            if (status.IsRunning)
            {
                return Json(new {
                    success = false,
                    message = "A markdown export is already in progress.",
                    isRunning = true
                });
            }

            var startMessage = new StartMarkdownExport
            {
                RequestedBy = User.Identity?.Name ?? "Anonymous",
                WorkspaceFilter = workspaceId.HasValue ? new WorkspaceId(workspaceId.Value) : null,
                ProjectFilter = projectId.HasValue ? new ProjectId(projectId.Value) : null
            };

            var startStatus = await _markdownExportActor.Ask<MarkdownExportStatus>(
                startMessage, TimeSpan.FromSeconds(30));

            return Json(new {
                success = true,
                message = "Markdown export started",
                isRunning = startStatus.IsRunning
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting markdown export: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    [HttpGet]
    [Route("markdown-export-status")]
    public async Task<IActionResult> GetMarkdownExportStatus()
    {
        try
        {
            var status = await _markdownExportActor.Ask<MarkdownExportStatus>(new GetMarkdownExportStatus(), TimeSpan.FromSeconds(5));
            return Json(new {
                success = true,
                status = status.Status,
                isRunning = status.IsRunning,
                outstanding = status.Outstanding,
                totalProcessed = status.TotalProcessed,
                totalSuccessful = status.TotalSuccessful,
                totalFailed = status.TotalFailed,
                requestedBy = status.RequestedBy,
                startTime = status.StartTime,
                duration = status.Duration?.TotalSeconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting markdown export status: {Error}", ex.Message);
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    #endregion
} 