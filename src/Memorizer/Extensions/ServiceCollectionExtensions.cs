using Akka.Hosting;
using Memorizer.Actors;
using Memorizer.Services;
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
        services.AddLlmServices();
        services.AddActorSystem();
        services.AddStorage();
        services.AddServerSettings();
        services.AddCorsSettings();
        services.AddVersioningSettings();
        if(initialize)
            services.AddHostedService<InitializationService>();
        services.AutoRegisterTypesInAssemblies(typeof(Storage).Assembly);
        return services;
    }

    public static IServiceCollection AddEmbeddings(
        this IServiceCollection services)
    {
        services
            .AddSingleton<EmbeddingSettings>(sp =>
                sp.GetRequiredService<IConfiguration>().GetSection("Embeddings").Get<EmbeddingSettings>() ??
                throw new ArgumentNullException("Embeddings Settings"))
            .AddHttpClient<IEmbeddingService, EmbeddingService>((sp, client) =>
            {
                EmbeddingSettings settings = sp.GetRequiredService<EmbeddingSettings>();
                client.BaseAddress = settings.ApiUrl;
                client.Timeout = settings.Timeout;
            });

        return services;
    }

    public static IServiceCollection AddLlmServices(
        this IServiceCollection services)
    {
        services
            .AddSingleton<LlmSettings>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var llmSettings = config.GetSection("LLM").Get<LlmSettings>();
                
                // If no settings, create default settings
                if (llmSettings == null)
                {
                    llmSettings = new LlmSettings
                    {
                        ApiUrl = new Uri("http://localhost:11434"), // Default Ollama URL
                        Model = "llama3"
                    };
                }
                
                return llmSettings;
            })
            .AddHttpClient<ILlmService, LlmService>((sp, client) =>
            {
                LlmSettings settings = sp.GetRequiredService<LlmSettings>();
                client.BaseAddress = settings.ApiUrl;
                client.Timeout = settings.Timeout;
            });

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