using Configuration.Extensions.EnvironmentFile;
using Memorizer.Extensions;
using Memorizer.Services;
using Memorizer.Settings;
using Memorizer.Telemetry;
using PostgMem.Tools;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Net.ServerSentEvents;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder
    .Configuration
    .AddEnvironmentFile() 
    .AddEnvironmentVariables("MEMORIZER_");

var resourceBuilder = ResourceBuilder.CreateDefault();
resourceBuilder
    .AddEnvironmentVariableDetector()
    .AddTelemetrySdk()
    .AddServiceVersionDetector();


builder
    .Logging
    .AddConsole(options => { options.LogToStandardErrorThreshold = LogLevel.Trace; });

// Get connection string
var connectionString = builder.Configuration.GetConnectionString("Storage") ?? 
    throw new InvalidOperationException("Missing Storage connection string");

// Debug OTEL configuration
var otelLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("OTEL-Debug");
otelLogger.LogInformation("OTEL_EXPORTER_OTLP_ENDPOINT: {Endpoint}", Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
otelLogger.LogInformation("OTEL_SERVICE_NAME: {ServiceName}", Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME"));
otelLogger.LogInformation("OTEL_RESOURCE_ATTRIBUTES: {ResourceAttributes}", Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES"));

// Add services
builder.Services.AddMemorizer();
builder.Services.AddMemorizerOtel();
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<MemoryTools>();

// Add MVC support for web UI
builder.Services.AddControllersWithViews();

// Configure CORS for MCP SSE endpoint - always enabled with configurable settings
var corsSettings = builder.Configuration.GetSection("Cors").Get<CorsSettings>() ?? new CorsSettings();
corsSettings.ApplyDefaults();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // Configure origins
        if (corsSettings.AllowedOrigins.Contains("*"))
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            policy.WithOrigins(corsSettings.AllowedOrigins);
        }

        // Configure methods
        if (corsSettings.AllowedMethods.Contains("*"))
        {
            policy.AllowAnyMethod();
        }
        else
        {
            policy.WithMethods(corsSettings.AllowedMethods);
        }

        // Configure headers
        if (corsSettings.AllowedHeaders.Contains("*"))
        {
            policy.AllowAnyHeader();
        }
        else
        {
            policy.WithHeaders(corsSettings.AllowedHeaders);
        }

        // Configure credentials (only if not using AllowAnyOrigin)
        if (corsSettings.AllowCredentials && !corsSettings.AllowedOrigins.Contains("*"))
        {
            policy.AllowCredentials();
        }
    });
});

// Configure routing options for lowercase URLs
builder.Services.Configure<RouteOptions>(options =>
{
    options.LowercaseUrls = true;
});

// Add health checks
builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionString,
        name: "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["db", "postgres", "required"]);

WebApplication app = builder.Build();

app.UseStaticFiles();

// Enable CORS for MCP SSE endpoint
app.UseCors();

app.MapMcp();

// Configure health check endpoints
app.MapHealthChecks("/healthz");

// Add OTEL test endpoint
app.MapGet("/otel-test", () =>
{
    using var activity = TelemetryConfig.ActivitySource.StartActivity("test-activity");
    activity?.SetTag("test.tag", "test-value");
    activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);

    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OtelTest");
    logger.LogInformation("OTEL test endpoint called - this should appear in collector logs");

    return Results.Ok(new { message = "OTEL test completed", activityId = activity?.Id });
});

// Add SSE endpoint for title generation progress
app.MapGet("/ui/tools/title-generation-progress",
    (ProgressStreamService progressService, CancellationToken ct) =>
{
    async IAsyncEnumerable<SseItem<object>> StreamProgress()
    {
        await foreach (var progress in progressService.GetTitleGenerationProgressStream(ct))
        {
            yield return new SseItem<object>(
                new {
                    percentComplete = progress.PercentComplete,
                    totalProcessed = progress.TotalProcessed,
                    totalSuccessful = progress.TotalSuccessful,
                    totalFailed = progress.TotalFailed,
                    outstanding = progress.Outstanding,
                    status = progress.Status,
                    requestedBy = progress.RequestedBy,
                    duration = progress.Duration?.TotalSeconds
                },
                "progress")
            {
                EventId = Guid.NewGuid().ToString()
            };
        }
    }

    return TypedResults.ServerSentEvents(StreamProgress());
});

// Add SSE endpoint for metadata embedding progress
app.MapGet("/ui/tools/metadata-embedding-progress",
    (ProgressStreamService progressService, CancellationToken ct) =>
{
    async IAsyncEnumerable<SseItem<object>> StreamProgress()
    {
        await foreach (var progress in progressService.GetMetadataEmbeddingProgressStream(ct))
        {
            yield return new SseItem<object>(
                new {
                    percentComplete = progress.PercentComplete,
                    totalProcessed = progress.TotalProcessed,
                    totalSuccessful = progress.TotalSuccessful,
                    totalFailed = progress.TotalFailed,
                    outstanding = progress.Outstanding,
                    status = progress.Status,
                    requestedBy = progress.RequestedBy,
                    duration = progress.Duration?.TotalSeconds
                },
                "progress")
            {
                EventId = Guid.NewGuid().ToString()
            };
        }
    }

    return TypedResults.ServerSentEvents(StreamProgress());
});

// Configure default MVC routing
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();