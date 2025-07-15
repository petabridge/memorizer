using System.Text;
using System.Text.Json;
using Memorizer.Models;
using Memorizer.Settings;
using OllamaSharp;

namespace Memorizer.Services;

/// <summary>
/// Service for LLM-based text analysis using OllamaSharp
/// </summary>
public sealed class LlmService : ILlmService
{
    private readonly OllamaApiClient _ollamaClient;
    private readonly LlmSettings _settings;
    private readonly ILogger<LlmService> _logger;

    public LlmService(
        HttpClient httpClient,
        LlmSettings settings,
        ILogger<LlmService> logger)
    {
        _settings = settings;
        _logger = logger;
        
        // Create OllamaSharp client with the configured HttpClient
        _ollamaClient = new OllamaApiClient(httpClient)
        {
            SelectedModel = _settings.Model
        };
    }

    public async Task<string> GenerateTitle(
        string content,
        string contentType,
        string[]? existingTags = null,
        int maxTitleLength = 80,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Generating title for content: length={Length}, type={Type}", 
                content.Length, contentType);

            var prompt = CreateTitleGenerationPrompt(content, contentType, existingTags, maxTitleLength);
            
            _logger.LogDebug("Sending title generation request to LLM model {Model}", _settings.Model);
            
            var response = await SendLlmRequest(prompt, cancellationToken);
            var title = ParseTitleResponse(response, maxTitleLength);
            
            _logger.LogDebug("Title generation complete: '{Title}'", title);

            return title;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during title generation: {ErrorMessage}", ex.Message);
            
            // Fallback: create a simple title
            var fallbackTitle = $"{contentType} - {DateTime.UtcNow:yyyy-MM-dd}";
            if (fallbackTitle.Length > maxTitleLength)
            {
                fallbackTitle = fallbackTitle[..(maxTitleLength - 3)] + "...";
            }
            
            return fallbackTitle;
        }
    }

    public async Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            _logger.LogDebug("Checking LLM health for model {Model} at {ApiUrl}", _settings.Model, _settings.ApiUrl);

            // Try a simple test request to verify connectivity and model availability
            var testRequest = new OllamaSharp.Models.GenerateRequest
            {
                Model = _settings.Model,
                Prompt = "Test",
                Stream = false,
                Options = new OllamaSharp.Models.RequestOptions
                {
                    NumPredict = 1 // Only generate 1 token for quick test
                }
            };

            // For health check, we just need to know if the service responds
            var responseStream = _ollamaClient.GenerateAsync(testRequest, cancellationToken);
            OllamaSharp.Models.GenerateResponseStream? firstResponse = null;
            
            await foreach (var chunk in responseStream)
            {
                firstResponse = chunk;
                break; // Just need the first response to confirm connectivity
            }
            
            stopwatch.Stop();

            if (firstResponse != null)
            {
                _logger.LogDebug("LLM health check successful in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
                return new LlmHealthResult
                {
                    IsHealthy = true,
                    Message = $"LLM service is available and responding",
                    ModelName = _settings.Model,
                    ResponseTime = stopwatch.Elapsed
                };
            }
            else
            {
                _logger.LogWarning("LLM health check returned null response");
                return new LlmHealthResult
                {
                    IsHealthy = false,
                    Message = "LLM service returned empty response",
                    ModelName = _settings.Model,
                    ResponseTime = stopwatch.Elapsed,
                    ErrorDetails = "Null response from LLM service"
                };
            }
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "LLM health check failed - connection issue: {Error}", ex.Message);
            return new LlmHealthResult
            {
                IsHealthy = false,
                Message = $"Cannot connect to LLM service at {_settings.ApiUrl}",
                ModelName = _settings.Model,
                ResponseTime = stopwatch.Elapsed,
                ErrorDetails = ex.Message
            };
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "LLM health check timed out after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            return new LlmHealthResult
            {
                IsHealthy = false,
                Message = "LLM service request timed out",
                ModelName = _settings.Model,
                ResponseTime = stopwatch.Elapsed,
                ErrorDetails = $"Request timed out after {stopwatch.Elapsed.TotalSeconds:F1} seconds"
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "LLM health check failed with unexpected error: {Error}", ex.Message);
            return new LlmHealthResult
            {
                IsHealthy = false,
                Message = "LLM service health check failed",
                ModelName = _settings.Model,
                ResponseTime = stopwatch.Elapsed,
                ErrorDetails = ex.Message
            };
        }
    }

    private async Task<string> SendLlmRequest(string prompt, CancellationToken cancellationToken = default)
    {
        // Use OllamaSharp simple API to generate completion
        var request = new OllamaSharp.Models.GenerateRequest
        {
            Model = _settings.Model,
            Prompt = prompt,
            Stream = true,
            Format = "json"
        };
        
        var responseStream = _ollamaClient.GenerateAsync(request, cancellationToken);
        
        var responseBuilder = new StringBuilder();
        await foreach (var responseChunk in responseStream)
        {
            responseBuilder.Append(responseChunk?.Response);
        }
        
        var response = responseBuilder.ToString();

        if (string.IsNullOrEmpty(response))
        {
            throw new InvalidOperationException("Empty response from LLM service");
        }

        return response;
    }

    private static string CreateTitleGenerationPrompt(
        string content,
        string contentType,
        string[]? existingTags,
        int maxTitleLength)
    {
        var prompt = new StringBuilder();
        
        prompt.AppendLine("You are an expert at creating concise, descriptive titles for various types of content.");
        prompt.AppendLine();
        prompt.AppendLine("TASK: Generate a clear, descriptive title for the provided content that captures its main topic and purpose.");
        prompt.AppendLine();
        prompt.AppendLine("GUIDELINES:");
        prompt.AppendLine($"- Maximum title length: {maxTitleLength} characters");
        prompt.AppendLine("- Make it descriptive and searchable");
        prompt.AppendLine("- Capture the main topic or purpose");
        prompt.AppendLine("- Use natural language, avoid generic phrases");
        prompt.AppendLine("- Consider the content type and existing tags for context");
        prompt.AppendLine();
        prompt.AppendLine("CONTENT DETAILS:");
        prompt.AppendLine($"- Type: {contentType}");
        if (existingTags?.Length > 0)
        {
            prompt.AppendLine($"- Tags: {string.Join(", ", existingTags)}");
        }
        prompt.AppendLine($"- Length: {content.Length} characters");
        prompt.AppendLine();
        prompt.AppendLine("CONTENT TO ANALYZE:");
        prompt.AppendLine("```");
        // Truncate content if it's very long to avoid token limits
        var truncatedContent = content.Length > 2000 ? content[..2000] + "..." : content;
        prompt.AppendLine(truncatedContent);
        prompt.AppendLine("```");
        prompt.AppendLine();
        prompt.AppendLine("RESPOND WITH VALID JSON in this exact format:");
        prompt.AppendLine("""
        {
          "title": "Generated title here",
          "reasoning": "Brief explanation of why this title was chosen"
        }
        """);

        return prompt.ToString();
    }

    private static string ParseTitleResponse(string response, int maxTitleLength)
    {
        try
        {
            var jsonDoc = JsonDocument.Parse(response);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("title", out var titleProp))
            {
                var title = titleProp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    // Ensure title doesn't exceed max length
                    if (title.Length > maxTitleLength)
                    {
                        title = title[..(maxTitleLength - 3)] + "...";
                    }
                    return title;
                }
            }

            throw new InvalidOperationException("No valid title found in LLM response");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse LLM title response: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        _ollamaClient?.Dispose();
    }
} 