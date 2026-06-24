using Memorizer.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Memorizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigurationController : ControllerBase
{
    private readonly IOptionsSnapshot<EmbeddingSettings> _embeddingSettingsSnapshot;
    private readonly IOptionsSnapshot<LlmSettings> _llmSettingsSnapshot;
    private readonly ServerSettings _serverSettings;
    private readonly SearchSettings _searchSettings;
    private readonly IConfiguration _configuration;

    // Convenience properties to access current settings
    private EmbeddingSettings EmbeddingSettingsValue => _embeddingSettingsSnapshot.Value;
    private LlmSettings LlmSettingsValue => _llmSettingsSnapshot.Value;

    public ConfigurationController(
        IOptionsSnapshot<EmbeddingSettings> embeddingSettingsSnapshot,
        IOptionsSnapshot<LlmSettings> llmSettingsSnapshot,
        ServerSettings serverSettings,
        SearchSettings searchSettings,
        IConfiguration configuration)
    {
        _embeddingSettingsSnapshot = embeddingSettingsSnapshot;
        _llmSettingsSnapshot = llmSettingsSnapshot;
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
                Provider = EmbeddingSettingsValue.Provider,
                ApiUrl = EmbeddingSettingsValue.ApiUrl?.ToString(),
                ApiConfigured = EmbeddingSettingsValue.ApiUrl != null,
                ApiKeyConfigured = !string.IsNullOrWhiteSpace(EmbeddingSettingsValue.ApiKey),
                Model = EmbeddingSettingsValue.Model,
                Timeout = EmbeddingSettingsValue.Timeout.ToString()
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
    public string Provider { get; set; } = string.Empty;
    public string? ApiUrl { get; set; }
    public bool ApiConfigured { get; set; }
    public bool ApiKeyConfigured { get; set; }
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