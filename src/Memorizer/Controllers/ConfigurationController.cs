using Memorizer.Settings;
using Microsoft.AspNetCore.Mvc;

namespace Memorizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigurationController : ControllerBase
{
    private readonly EmbeddingSettings _embeddingSettings;
    private readonly LlmSettings _llmSettings;
    private readonly ServerSettings _serverSettings;
    private readonly IConfiguration _configuration;

    public ConfigurationController(
        EmbeddingSettings embeddingSettings,
        LlmSettings llmSettings,
        ServerSettings serverSettings,
        IConfiguration configuration)
    {
        _embeddingSettings = embeddingSettings;
        _llmSettings = llmSettings;
        _serverSettings = serverSettings;
        _configuration = configuration;
    }

    /// <summary>
    /// Get system configuration for debugging
    /// </summary>
    [HttpGet]
    public ActionResult<SystemConfiguration> GetConfiguration()
    {
        return Ok(new SystemConfiguration
        {
            EmbeddingSettings = new
            {
                ApiConfigured = _embeddingSettings.ApiUrl != null,
                Model = _embeddingSettings.Model,
                Timeout = _embeddingSettings.Timeout.ToString()
            },
            ServerSettings = new
            {
                CanonicalUrlConfigured = !string.IsNullOrEmpty(_serverSettings.CanonicalUrl),
                CanonicalUrl = _serverSettings.CanonicalUrl
            },
            ConnectionStrings = new
            {
                StorageConfigured = !string.IsNullOrEmpty(_configuration.GetConnectionString("Storage"))
            },
            Environment = new
            {
                AspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                OtelConfigured = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")),
                OtelServiceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            }
        });
    }
}

public class SystemConfiguration
{
    public object EmbeddingSettings { get; set; } = null!;
    public object ServerSettings { get; set; } = null!;
    public object ConnectionStrings { get; set; } = null!;
    public object Environment { get; set; } = null!;
} 