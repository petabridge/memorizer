namespace Memorizer.Settings;

/// <summary>
/// Configuration settings for CORS (Cross-Origin Resource Sharing) policy.
/// CORS is always enabled for MCP SSE endpoints to work with Claude Code and other clients.
/// By default, uses permissive settings. Lock down in production by specifying exact origins.
/// </summary>
public class CorsSettings
{
    /// <summary>
    /// Allowed origins for CORS requests. Default is "*" (all origins).
    /// For production, specify exact origins like ["https://app.example.com", "https://api.example.com"]
    /// </summary>
    public string[] AllowedOrigins { get; set; } = ["*"];

    /// <summary>
    /// Whether to allow credentials (cookies, authorization headers) in CORS requests.
    /// Cannot be true when AllowedOrigins contains "*".
    /// </summary>
    public bool AllowCredentials { get; set; } = false;

    /// <summary>
    /// Allowed HTTP methods. Default is "*" (all methods).
    /// </summary>
    public string[] AllowedMethods { get; set; } = ["*"];

    /// <summary>
    /// Allowed HTTP headers. Default is "*" (all headers).
    /// </summary>
    public string[] AllowedHeaders { get; set; } = ["*"];
}
