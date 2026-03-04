using Akka.Hosting;
using Memorizer.Actors;
using Memorizer.Services;
using Memorizer.Services.Providers;
using Memorizer.Settings;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Registrator.Net;

namespace Memorizer.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemorizer(
        this IServiceCollection services, bool initialize = true)
    {
        services.AddEmbeddings();
        services.AddStorage(); // Must come before AddLlmServices since provider factory needs IStorage
        services.AddLlmServices();
        services.AddActorSystem();
        services.AddServerSettings();
        services.AddCorsSettings();
        services.AddVersioningSettings();
        services.AddSimilaritySettings();
        services.AddSearchSettings();
        services.AddCanonicalUrlService();
        services.AddMarkdownExportSettings();
        services.AddMarkdownExportService();
        if(initialize)
            services.AddHostedService<InitializationService>();
        services.AutoRegisterTypesInAssemblies(typeof(Storage).Assembly);
        return services;
    }

    public static IServiceCollection AddEmbeddings(
        this IServiceCollection services)
    {
        // Register EmbeddingSettings with IOptions pattern for reloadable configuration
        services.AddOptions<EmbeddingSettings>()
            .BindConfiguration("Embeddings")
            .ValidateOnStart();

        // Register EmbeddingService as Scoped so it gets fresh settings on each request
        // Note: HttpClient is configured with base address in the service constructor
        services.AddHttpClient<IEmbeddingService, EmbeddingService>();

        // Register EmbeddingDimensionService with its own HttpClient (singleton is fine here)
        services.AddHttpClient<IEmbeddingDimensionService, EmbeddingDimensionService>();

        // Register dimension mismatch state holder for UI warnings
        services.AddSingleton<IDimensionMismatchState, DimensionMismatchState>();

        return services;
    }

    public static IServiceCollection AddLlmServices(
        this IServiceCollection services)
    {
        // Register LlmSettings with IOptions pattern for reloadable configuration
        services.AddOptions<LlmSettings>()
            .BindConfiguration("LLM")
            .ValidateOnStart();

        // Register IMemorizerAgentProvider as Scoped so it gets fresh settings on each request
        // Note: HttpClient is configured with base address in the service constructor
        services.AddHttpClient<IMemorizerAgentProvider, OllamaMemorizerAgentProvider>();

        return services;
    }

    public static IServiceCollection AddActorSystem(
        this IServiceCollection services)
    {
        services.AddAkka("Memorizer", (builder, provider) =>
        {
            builder.ConfigureLoggers(logger =>
            {
                logger.ClearLoggers(); // clear the default console logger
                logger.LogLevel = Akka.Event.LogLevel.InfoLevel;
                logger.AddLoggerFactory();
            });

            // Register actors with the ActorRegistry
            builder.WithActors((system, registry, resolver) =>
            {
                // Create and register the TitleGenerationActor
                var titleGenerationActorProps = resolver.Props<TitleGenerationActor>();
                var titleGenerationActor = system.ActorOf(titleGenerationActorProps, "title-generation");
                registry.Register<TitleGenerationActorKey>(titleGenerationActor);

                // Create and register the EmbeddingRegenerationActor
                var embeddingRegenerationActorProps = resolver.Props<EmbeddingRegenerationActor>();
                var embeddingRegenerationActor = system.ActorOf(embeddingRegenerationActorProps, "embedding-regeneration");
                registry.Register<EmbeddingRegenerationActorKey>(embeddingRegenerationActor);

                // Create and register the VersionPurgeActor
                var versionPurgeActorProps = resolver.Props<VersionPurgeActor>();
                var versionPurgeActor = system.ActorOf(versionPurgeActorProps, "version-purge");
                registry.Register<VersionPurgeActorKey>(versionPurgeActor);

                // Create and register the DimensionMigrationActor
                var dimensionMigrationActorProps = resolver.Props<DimensionMigrationActor>();
                var dimensionMigrationActor = system.ActorOf(dimensionMigrationActorProps, "dimension-migration");
                registry.Register<DimensionMigrationActorKey>(dimensionMigrationActor);

                // Create and register the MarkdownExportActor
                var markdownExportActorProps = resolver.Props<MarkdownExportActor>();
                var markdownExportActor = system.ActorOf(markdownExportActorProps, "markdown-export");
                registry.Register<MarkdownExportActorKey>(markdownExportActor);
            });

            // TODO: Configure Akka.Persistence.Sql with PostgreSQL
            // This will be added once we have the proper configuration for tool performance tracking
        });

        return services;
    }

    public static IServiceCollection AddStorage(
        this IServiceCollection services)
    {
        services
            .AddSingleton(sp =>
            {
                string connectionString =
                    sp.GetRequiredService<IConfiguration>().GetConnectionString("Storage") ??
                    throw new ArgumentNullException("Storage Connection String");
                NpgsqlDataSourceBuilder sourceBuilder = new(connectionString);
                sourceBuilder.UseVector();
                return sourceBuilder.Build();
            });
        return services;
    }

    public static IServiceCollection AddServerSettings(
        this IServiceCollection services)
    {
        services.AddSingleton<ServerSettings>(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("Server").Get<ServerSettings>() ??
            new ServerSettings());

        return services;
    }

    public static IServiceCollection AddCorsSettings(
        this IServiceCollection services)
    {
        services.AddSingleton<CorsSettings>(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("Cors").Get<CorsSettings>() ??
            new CorsSettings());

        return services;
    }

    public static IServiceCollection AddVersioningSettings(
        this IServiceCollection services)
    {
        services.AddSingleton<VersioningSettings>(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("Versioning").Get<VersioningSettings>() ??
            new VersioningSettings());

        return services;
    }

    public static IServiceCollection AddSimilaritySettings(
        this IServiceCollection services)
    {
        services.AddSingleton<SimilaritySettings>(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("Similarity").Get<SimilaritySettings>() ??
            new SimilaritySettings());

        return services;
    }

    public static IServiceCollection AddSearchSettings(
        this IServiceCollection services)
    {
        services.AddSingleton<SearchSettings>(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("Search").Get<SearchSettings>() ??
            new SearchSettings());

        return services;
    }

    public static IServiceCollection AddCanonicalUrlService(
        this IServiceCollection services)
    {
        services.AddSingleton<ICanonicalUrlService, CanonicalUrlService>();
        return services;
    }

    public static IServiceCollection AddMarkdownExportSettings(
        this IServiceCollection services)
    {
        services.AddSingleton<MarkdownExportSettings>(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("MarkdownExport").Get<MarkdownExportSettings>() ??
            new MarkdownExportSettings());

        return services;
    }

    public static IServiceCollection AddMarkdownExportService(
        this IServiceCollection services)
    {
        services.AddScoped<IMarkdownExportService, MarkdownExportService>();
        return services;
    }

    public static IServiceCollection AddHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Storage") ??
                                  throw new ArgumentNullException("Storage Connection String");

        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy())
            .AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

        return services;
    }
}