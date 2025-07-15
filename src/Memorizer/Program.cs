using Configuration.Extensions.EnvironmentFile;
using Memorizer.Extensions;
using Memorizer.Services;
using Memorizer.Telemetry;
using PostgMem.Tools;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

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
builder.Services.AddMcpServer().WithHttpTransport().WithTools<MemoryTools>();

// Add MVC support for web UI
builder.Services.AddControllersWithViews();

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

// Configure default MVC routing
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();