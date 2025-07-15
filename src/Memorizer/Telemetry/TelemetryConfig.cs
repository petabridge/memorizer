using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Memorizer.Telemetry;

public static class TelemetryConfig
{
    public static readonly ActivitySource ActivitySource = new("PostgMem");
    
    public static IServiceCollection AddMemorizerOtel(this IServiceCollection services)
    {
        services
            .AddOpenTelemetry()
            .ConfigureResource(builder =>
            {
                builder
                    .AddEnvironmentVariableDetector()
                    .AddTelemetrySdk()
                    .AddServiceVersionDetector();
            })
            .UseOtlpExporter()
            .WithLogging(builder =>
            {
                // Logging will be handled by UseOtlpExporter automatically
            })
            .WithMetrics(builder =>
            {
                builder
                    .AddHttpClientInstrumentation()
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(builder =>
            {
                builder
                    .AddHttpClientInstrumentation()
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.EnrichWithHttpRequest = (activity, request) =>
                        {
                            activity.SetTag("http.request.method", request.Method);
                            activity.SetTag("http.request.url", request.Path);
                        };
                        options.EnrichWithHttpResponse = (activity, response) =>
                        {
                            activity.SetTag("http.response.status_code", response.StatusCode);
                        };
                    })
                    .AddSource("PostgMem");
            });
        
        return services;
    }
}