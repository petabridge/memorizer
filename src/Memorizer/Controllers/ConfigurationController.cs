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
    private readonly SearchSettings _searchSettings;
    private readonly IConfiguration _configuration;

    public ConfigurationController(
        EmbeddingSettings embeddingSettings,
        LlmSettings llmSettings,
        ServerSettings serverSettings,
        SearchSettings searchSettings,
        IConfiguration configuration)
    {
        _embeddingSettings = embeddingSettings;
        _llmSettings = llmSettings;
        _serverSettings = serverSettings;
        _searchSettings = searchSettings;
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
            EmbeddingSettings = new EmbeddingConfigDto
            {
                ApiConfigured = _embeddingSettings.ApiUrl != null,
                Model = _embeddingSettings.Model,
                Timeout = _embeddingSettings.Timeout.ToString()
            },
            ServerSettings = new ServerConfigDto
            {
                CanonicalUrlConfigured = !string.IsNullOrEmpty(_serverSettings.CanonicalUrl),
                CanonicalUrl = _serverSettings.CanonicalUrl
            },
            SearchSettings = new SearchConfigDto
            {
                ReturnFullContent = _searchSettings.ReturnFullContent
            },
            ConnectionStrings = new ConnectionStringsConfigDto
            {
                StorageConfigured = !string.IsNullOrEmpty(_configuration.GetConnectionString("Storage"))
            },
            Environment = new EnvironmentConfigDto
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
    public EmbeddingConfigDto EmbeddingSettings { get; set; } = null!;
    public ServerConfigDto ServerSettings { get; set; } = null!;
    public SearchConfigDto SearchSettings { get; set; } = null!;
    public ConnectionStringsConfigDto ConnectionStrings { get; set; } = null!;
    public EnvironmentConfigDto Environment { get; set; } = null!;
}

public class EmbeddingConfigDto
{
    public bool ApiConfigured { get; set; }
    public string Model { get; set; } = string.Empty;
    public string Timeout { get; set; } = string.Empty;
}

public class ServerConfigDto
{
    public bool CanonicalUrlConfigured { get; set; }
    public string? CanonicalUrl { get; set; }
}

public class SearchConfigDto
{
    public bool ReturnFullContent { get; set; }
}

public class ConnectionStringsConfigDto
{
    public bool StorageConfigured { get; set; }
}

public class EnvironmentConfigDto
{
    public string? AspNetCoreEnvironment { get; set; }
    public bool OtelConfigured { get; set; }
    public string? OtelServiceName { get; set; }
} 