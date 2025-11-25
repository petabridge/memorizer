using Akka.Actor;
using Akka.Hosting;
using Memorizer.Actors;
using Memorizer.Settings;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace Memorizer.Controllers;

[Route("ui/tools")]
public class ToolsController : Controller
{
    private readonly IActorRef _titleGenerationActor;
    private readonly IActorRef _embeddingRegenerationActor;
    private readonly LlmSettings _llmSettings;
    private readonly ILogger<ToolsController> _logger;

    public ToolsController(
        IRequiredActor<TitleGenerationActorKey> titleGenerationActor,
        IRequiredActor<EmbeddingRegenerationActorKey> embeddingRegenerationActor,
        LlmSettings llmSettings,
        ILogger<ToolsController> logger)
    {
        _titleGenerationActor = titleGenerationActor.ActorRef;
        _embeddingRegenerationActor = embeddingRegenerationActor.ActorRef;
        _llmSettings = llmSettings;
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
                message = $"Title generation started for up to {batchSize} memories using model '{_llmSettings.Model}'",
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
                model = _llmSettings.Model,
                apiUrl = _llmSettings.ApiUrl.ToString()
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
        // Check if we have basic LLM settings
        if (_llmSettings == null)
        {
            return (false, "LLM settings not found in configuration");
        }

        // Check if model is configured
        if (string.IsNullOrWhiteSpace(_llmSettings.Model))
        {
            return (false, "No LLM model configured. Please configure the 'LLM:Model' setting.");
        }

        // Check if API URL is configured
        if (_llmSettings.ApiUrl == null)
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
} 