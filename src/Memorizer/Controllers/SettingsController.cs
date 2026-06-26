using System.Net.Http.Headers;
using System.Net.Http.Json;
using Memorizer.Models;
using Memorizer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;

namespace Memorizer.Controllers;

/// <summary>
/// Controller for system settings management
/// </summary>
[Route("settings")]
public class SettingsController : Controller
{
    private readonly IStorage _storage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        IStorage storage,
        IHttpClientFactory httpClientFactory,
        ILogger<SettingsController> logger)
    {
        _storage = storage;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Provider settings page - manage embedding and Memorizer Agent providers
    /// </summary>
    [HttpGet("providers")]
    public IActionResult Providers()
    {
        return View();
    }

    /// <summary>
    /// API endpoint to get all providers
    /// </summary>
    [HttpGet("api/providers")]
    public async Task<IActionResult> GetProviders(CancellationToken cancellationToken)
    {
        var embeddingProviders = await _storage.GetAllProvidersAsync(ProviderTypes.Embedding, cancellationToken);
        var agentProviders = await _storage.GetAllProvidersAsync(ProviderTypes.MemorizerAgent, cancellationToken);

        return Json(new
        {
            embeddingProviders = embeddingProviders.Select(p => new
            {
                p.Id,
                p.ProviderType,
                p.ProviderName,
                p.DisplayName,
                p.IsActive,
                config = ProviderConfigSanitizer.RedactForDisplay(p.Config),
                p.CreatedAt,
                p.UpdatedAt
            }),
            agentProviders = agentProviders.Select(p => new
            {
                p.Id,
                p.ProviderType,
                p.ProviderName,
                p.DisplayName,
                p.IsActive,
                config = ProviderConfigSanitizer.RedactForDisplay(p.Config),
                p.CreatedAt,
                p.UpdatedAt
            })
        });
    }

    /// <summary>
    /// API endpoint to get active providers
    /// </summary>
    [HttpGet("api/providers/active")]
    public async Task<IActionResult> GetActiveProviders(CancellationToken cancellationToken)
    {
        var embeddingProvider = await _storage.GetActiveProviderAsync(ProviderTypes.Embedding, cancellationToken);
        var agentProvider = await _storage.GetActiveProviderAsync(ProviderTypes.MemorizerAgent, cancellationToken);

        return Json(new
        {
            embedding = embeddingProvider != null ? new
            {
                embeddingProvider.ProviderName,
                embeddingProvider.DisplayName,
                config = ProviderConfigSanitizer.RedactForDisplay(embeddingProvider.Config)
            } : null,
            memorizerAgent = agentProvider != null ? new
            {
                agentProvider.ProviderName,
                agentProvider.DisplayName,
                config = ProviderConfigSanitizer.RedactForDisplay(agentProvider.Config)
            } : null
        });
    }

    /// <summary>
    /// API endpoint to save provider settings
    /// </summary>
    [HttpPost("api/providers")]
    public async Task<IActionResult> SaveProvider([FromBody] SaveProviderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var existingProviders = await _storage.GetAllProvidersAsync(request.ProviderType, cancellationToken);

            // Check if embedding model is changing (to detect re-embedding need)
            string? previousModel = null;
            bool modelChanged = false;

            if (request.ProviderType == ProviderTypes.Embedding && request.IsActive)
            {
                var previousProvider = existingProviders.FirstOrDefault(p => p.IsActive);
                if (previousProvider != null)
                {
                    var prevConfig = previousProvider.Config.RootElement;
                    if (prevConfig.TryGetProperty("model", out var prevModelProp))
                    {
                        previousModel = prevModelProp.GetString();
                    }
                }

                // Get new model from request
                if (request.Config.TryGetValue("model", out var newModelObj))
                {
                    var newModel = newModelObj?.ToString();
                    modelChanged = previousModel != null && newModel != null && previousModel != newModel;
                }
            }

            var settings = new ProviderSettings
            {
                ProviderType = request.ProviderType,
                ProviderName = request.ProviderName,
                DisplayName = request.DisplayName,
                Config = ProviderConfigSanitizer.CleanForStorage(request.Config),
                IsActive = request.IsActive
            };

            var saved = await _storage.SaveProviderSettingsAsync(settings, cancellationToken);

            _logger.LogInformation("Saved provider settings: {ProviderType}/{ProviderName}",
                request.ProviderType, request.ProviderName);

            // If this is an active embedding provider, trigger dimension validation
            DimensionValidationResult? dimensionValidation = null;
            if (request.ProviderType == ProviderTypes.Embedding && saved.IsActive)
            {
                var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                ApplyEmbeddingProviderConfiguration(config, saved);
                dimensionValidation = await ValidateEmbeddingDimensionsAsync(cancellationToken);
            }

            // If this is an active Memorizer Agent provider, update configuration
            if (request.ProviderType == ProviderTypes.MemorizerAgent && saved.IsActive)
            {
                var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                var savedConfig = saved.Config.RootElement;
                if (savedConfig.TryGetProperty("apiUrl", out var apiUrlObj))
                {
                    config["LLM:ApiUrl"] = apiUrlObj.GetString();
                }
                if (savedConfig.TryGetProperty("model", out var modelObj))
                {
                    config["LLM:Model"] = modelObj.GetString();
                }
                if (savedConfig.TryGetProperty("timeout", out var timeoutObj))
                {
                    config["LLM:Timeout"] = timeoutObj.GetString();
                }
            }

            // Log warning if embedding model changed - user should regenerate embeddings
            if (modelChanged)
            {
                var newModel = request.Config.TryGetValue("model", out var m) ? m?.ToString() : "unknown";
                _logger.LogWarning(
                    "Embedding model changed from '{OldModel}' to '{NewModel}'. " +
                    "Existing embeddings may be incompatible - consider regenerating embeddings via the dimension migration tool.",
                    previousModel, newModel);
            }

            return Json(new
            {
                success = true,
                provider = new
                {
                    saved.Id,
                    saved.ProviderType,
                    saved.ProviderName,
                    saved.DisplayName,
                    saved.IsActive,
                    saved.UpdatedAt
                },
                dimensionValidation = dimensionValidation != null ? new
                {
                    hasMismatch = dimensionValidation.HasMismatch,
                    description = dimensionValidation.MismatchDescription,
                    detectedDimensions = dimensionValidation.DetectedModelDimensions,
                    storedDimensions = dimensionValidation.StoredDimensions,
                    schemaDimensions = dimensionValidation.DatabaseSchemaDimensions
                } : null,
                modelChanged,
                previousModel = modelChanged ? previousModel : null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving provider settings");
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// API endpoint to set active provider
    /// </summary>
    [HttpPost("api/providers/activate")]
    public async Task<IActionResult> ActivateProvider([FromBody] ActivateProviderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.SetActiveProviderAsync(request.ProviderType, request.ProviderName, cancellationToken);

            _logger.LogInformation("Activated provider: {ProviderType}/{ProviderName}",
                request.ProviderType, request.ProviderName);

            // If activating an embedding provider, trigger dimension validation
            DimensionValidationResult? dimensionValidation = null;
            if (request.ProviderType == ProviderTypes.Embedding)
            {
                var activeProvider = await _storage.GetActiveProviderAsync(ProviderTypes.Embedding, cancellationToken);
                if (activeProvider != null)
                {
                    var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                    ApplyEmbeddingProviderConfiguration(config, activeProvider);
                }

                dimensionValidation = await ValidateEmbeddingDimensionsAsync(cancellationToken);
            }

            return Json(new
            {
                success = true,
                dimensionValidation = dimensionValidation != null ? new
                {
                    hasMismatch = dimensionValidation.HasMismatch,
                    description = dimensionValidation.MismatchDescription,
                    detectedDimensions = dimensionValidation.DetectedModelDimensions,
                    storedDimensions = dimensionValidation.StoredDimensions,
                    schemaDimensions = dimensionValidation.DatabaseSchemaDimensions
                } : null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating provider");
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// API endpoint to test provider connection
    /// </summary>
    [HttpPost("api/providers/test")]
    public async Task<IActionResult> TestConnection([FromBody] TestConnectionRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (request.ProviderName == ProviderNames.Ollama)
            {
                var httpClient = _httpClientFactory.CreateClient();
                httpClient.BaseAddress = new Uri(request.ApiUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var ollamaClient = new OllamaApiClient(httpClient);

                if (!string.IsNullOrEmpty(request.Model))
                {
                    ollamaClient.SelectedModel = request.Model;

                    // Use appropriate test based on provider type
                    if (request.ProviderType == ProviderTypes.Embedding)
                    {
                        // Test embedding model by generating an embedding
                        var embeddings = await ollamaClient.EmbedAsync(
                            new OllamaSharp.Models.EmbedRequest
                            {
                                Model = request.Model,
                                Input = ["test"]
                            },
                            cancellationToken);

                        stopwatch.Stop();
                        var dimensions = embeddings.Embeddings?.FirstOrDefault()?.Length ?? 0;
                        return Json(new
                        {
                            success = true,
                            message = $"Successfully connected to Ollama and verified embedding model '{request.Model}' ({dimensions} dimensions)",
                            responseTimeMs = stopwatch.ElapsedMilliseconds
                        });
                    }
                    else
                    {
                        // Test generation model by generating a single token
                        var testRequest = new OllamaSharp.Models.GenerateRequest
                        {
                            Model = request.Model,
                            Prompt = "Hi",
                            Stream = false,
                            Options = new OllamaSharp.Models.RequestOptions { NumPredict = 1 }
                        };

                        await foreach (var _ in ollamaClient.GenerateAsync(testRequest, cancellationToken))
                        {
                            break; // Just need first response
                        }

                        stopwatch.Stop();
                        return Json(new
                        {
                            success = true,
                            message = $"Successfully connected to Ollama and verified model '{request.Model}'",
                            responseTimeMs = stopwatch.ElapsedMilliseconds
                        });
                    }
                }
                else
                {
                    // Just test connectivity by listing models
                    var models = await ollamaClient.ListLocalModelsAsync(cancellationToken);
                    stopwatch.Stop();

                    return Json(new
                    {
                        success = true,
                        message = $"Successfully connected to Ollama. Found {models.Count()} models.",
                        responseTimeMs = stopwatch.ElapsedMilliseconds
                    });
                }
            }

            if (request.ProviderName == ProviderNames.OpenAI && request.ProviderType == ProviderTypes.Embedding)
            {
                if (string.IsNullOrWhiteSpace(request.Model))
                {
                    return Json(new
                    {
                        success = false,
                        error = "Model is required to test OpenAI-compatible embeddings",
                        responseTimeMs = stopwatch.ElapsedMilliseconds
                    });
                }

                var httpClient = _httpClientFactory.CreateClient();
                httpClient.BaseAddress = new Uri(request.ApiUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                var apiKey = config["Embeddings:ApiKey"];

                var testRequest = new OpenAIEmbeddingRequest
                {
                    Model = request.Model,
                    Input = "test"
                };
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/embeddings")
                {
                    Content = JsonContent.Create(testRequest)
                };

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var response = await httpClient.SendAsync(httpRequest, cancellationToken);
                response.EnsureSuccessStatusCode();

                var embeddings = await response.Content.ReadFromJsonAsync<OpenAIEmbeddingResponse>(cancellationToken: cancellationToken);
                var dimensions = embeddings?.Data.FirstOrDefault()?.Embedding.Length ?? 0;
                if (dimensions == 0)
                {
                    throw new InvalidOperationException("Empty embedding response from OpenAI-compatible API");
                }

                stopwatch.Stop();
                return Json(new
                {
                    success = true,
                    message = $"Successfully connected to OpenAI-compatible embeddings API and verified model '{request.Model}' ({dimensions} dimensions)",
                    responseTimeMs = stopwatch.ElapsedMilliseconds
                });
            }

            return BadRequest(new { success = false, error = $"Unknown provider: {request.ProviderName}" });
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Connection test failed for {Provider} at {ApiUrl}", request.ProviderName, request.ApiUrl);
            return Json(new
            {
                success = false,
                error = $"Cannot connect to {request.ApiUrl}: {ex.Message}",
                responseTimeMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Connection test failed for {Provider}", request.ProviderName);
            return Json(new
            {
                success = false,
                error = ex.Message,
                responseTimeMs = stopwatch.ElapsedMilliseconds
            });
        }
    }

    /// <summary>
    /// API endpoint to list available models from a provider
    /// </summary>
    [HttpPost("api/providers/models")]
    public async Task<IActionResult> ListModels([FromBody] ListModelsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.ProviderName == ProviderNames.Ollama)
            {
                var httpClient = _httpClientFactory.CreateClient();
                httpClient.BaseAddress = new Uri(request.ApiUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var ollamaClient = new OllamaApiClient(httpClient);
                var models = await ollamaClient.ListLocalModelsAsync(cancellationToken);

                // Filter models based on provider type using common naming patterns
                var modelList = models.Select(m => new
                {
                    name = m.Name,
                    size = m.Size,
                    modifiedAt = m.ModifiedAt,
                    sizeFormatted = FormatBytes(m.Size),
                    isLikelyEmbedding = IsLikelyEmbeddingModel(m.Name)
                });

                // If provider type is specified, filter to show relevant models first
                if (request.ProviderType == ProviderTypes.Embedding)
                {
                    // For embedding providers, show likely embedding models first, then others
                    modelList = modelList.OrderByDescending(m => m.isLikelyEmbedding).ThenBy(m => m.name);
                }
                else if (request.ProviderType == ProviderTypes.MemorizerAgent)
                {
                    // For agent providers, show likely generation models first (non-embedding)
                    modelList = modelList.OrderBy(m => m.isLikelyEmbedding).ThenBy(m => m.name);
                }
                else
                {
                    modelList = modelList.OrderBy(m => m.name);
                }

                return Json(new
                {
                    success = true,
                    models = modelList
                });
            }

            // For future providers like Anthropic/OpenAI, return hardcoded lists
            if (request.ProviderName == ProviderNames.Anthropic)
            {
                return Json(new
                {
                    success = true,
                    models = new[]
                    {
                        new { name = "claude-sonnet-4-20250514", size = 0L, modifiedAt = (DateTimeOffset?)null, sizeFormatted = "" },
                        new { name = "claude-opus-4-20250514", size = 0L, modifiedAt = (DateTimeOffset?)null, sizeFormatted = "" },
                        new { name = "claude-3-5-haiku-20241022", size = 0L, modifiedAt = (DateTimeOffset?)null, sizeFormatted = "" }
                    }
                });
            }

            if (request.ProviderName == ProviderNames.OpenAI)
            {
                return Json(new
                {
                    success = true,
                    models = new[]
                    {
                        new { name = "text-embedding-3-small", size = 0L, modifiedAt = (DateTimeOffset?)null, sizeFormatted = "", isLikelyEmbedding = true },
                        new { name = "text-embedding-3-large", size = 0L, modifiedAt = (DateTimeOffset?)null, sizeFormatted = "", isLikelyEmbedding = true },
                        new { name = "text-embedding-ada-002", size = 0L, modifiedAt = (DateTimeOffset?)null, sizeFormatted = "", isLikelyEmbedding = true }
                    }
                });
            }

            return BadRequest(new { success = false, error = $"Unknown provider: {request.ProviderName}" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to list models for {Provider} at {ApiUrl}", request.ProviderName, request.ApiUrl);
            return Json(new
            {
                success = false,
                error = $"Cannot connect to {request.ApiUrl}: {ex.Message}",
                models = Array.Empty<object>()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list models for {Provider}", request.ProviderName);
            return Json(new
            {
                success = false,
                error = ex.Message,
                models = Array.Empty<object>()
            });
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Heuristic to determine if a model is likely an embedding model based on naming patterns.
    /// Ollama doesn't expose this metadata directly, so we use common naming conventions.
    /// </summary>
    private static bool IsLikelyEmbeddingModel(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return false;

        var name = modelName.ToLowerInvariant();

        // Common embedding model patterns
        string[] embeddingPatterns =
        [
            "embed",       // nomic-embed, mxbai-embed, etc.
            "minilm",      // all-minilm
            "bge",         // bge-small, bge-large, etc.
            "e5",          // e5-small, e5-large, etc.
            "gte",         // gte-small, gte-large, etc.
            "sentence",    // sentence-transformers models
            "instructor",  // instructor embeddings
            "mpnet",       // all-mpnet
            "distilbert",  // distilbert embeddings
            "roberta"      // roberta-based embeddings
        ];

        return embeddingPatterns.Any(pattern => name.Contains(pattern));
    }

    /// <summary>
    /// Validates embedding dimensions and updates the mismatch state.
    /// Called when embedding provider settings are saved or activated.
    /// </summary>
    private async Task<DimensionValidationResult> ValidateEmbeddingDimensionsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Validating embedding dimensions after provider change...");

        using var scope = HttpContext.RequestServices.CreateScope();
        var dimensionService = scope.ServiceProvider.GetRequiredService<IEmbeddingDimensionService>();
        var mismatchState = scope.ServiceProvider.GetRequiredService<IDimensionMismatchState>();

        var validation = await dimensionService.ValidateAsync(cancellationToken);
        mismatchState.Update(validation);

        if (validation.HasMismatch)
        {
            _logger.LogWarning(
                "Embedding dimension mismatch detected: {Description}. " +
                "Detected={Detected}, Stored={Stored}, Schema={Schema}",
                validation.MismatchDescription,
                validation.DetectedModelDimensions,
                validation.StoredDimensions,
                validation.DatabaseSchemaDimensions);
        }
        else
        {
            _logger.LogInformation(
                "Embedding dimensions validated successfully: {Dimensions} dimensions",
                validation.DetectedModelDimensions ?? validation.StoredDimensions ?? validation.DatabaseSchemaDimensions);
        }

        return validation;
    }

    private static void ApplyEmbeddingProviderConfiguration(IConfiguration config, ProviderSettings provider)
    {
        config["Embeddings:Provider"] = provider.ProviderName;

        var providerConfig = provider.Config.RootElement;
        config["Embeddings:ApiUrl"] = providerConfig.TryGetProperty("apiUrl", out var apiUrlProp)
            ? apiUrlProp.GetString()
            : null;
        config["Embeddings:Model"] = providerConfig.TryGetProperty("model", out var modelProp)
            ? modelProp.GetString()
            : null;
    }
}

public class SaveProviderRequest
{
    public string ProviderType { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public Dictionary<string, object> Config { get; set; } = new();
    public bool IsActive { get; set; }
}

public class ActivateProviderRequest
{
    public string ProviderType { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
}

public class TestConnectionRequest
{
    public string ProviderType { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = string.Empty;
    public string? Model { get; set; }
}

public class ListModelsRequest
{
    public string ProviderType { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = string.Empty;
}
