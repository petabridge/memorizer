using System.Net.ServerSentEvents;
using Akka.Actor;
using Akka.Hosting;
using Memorizer.Actors;

namespace Memorizer;

/// <summary>
/// Extension methods for registering application routes
/// </summary>
public static class Routes
{
    /// <summary>
    /// Maps SSE progress endpoints for background job monitoring
    /// </summary>
    public static WebApplication MapProgressEndpoints(this WebApplication app)
    {
        // SSE endpoint for title generation progress
        app.MapGet("/tools/title-generation-progress",
            async (IActorRegistry actorRegistry, CancellationToken ct) =>
        {
            var titleGenActor = await actorRegistry.GetAsync<TitleGenerationActorKey>(ct);
            var subscriberId = Guid.NewGuid().ToString();

            var subscription = await titleGenActor.Ask<ProgressSubscription>(
                new SubscribeToProgress(subscriberId),
                ct);

            return TypedResults.ServerSentEvents(StreamProgress());

            async IAsyncEnumerable<SseItem<ProgressEvent>> StreamProgress()
            {
                try
                {
                    await foreach (var progress in subscription.Reader.ReadAllAsync(ct))
                    {
                        yield return new SseItem<ProgressEvent>(progress, "progress")
                        {
                            EventId = Guid.NewGuid().ToString()
                        };
                    }
                }
                finally
                {
                    titleGenActor.Tell(new UnsubscribeFromProgress(subscriberId), ActorRefs.NoSender);
                }
            }
        });

        // SSE endpoint for embedding regeneration progress
        app.MapGet("/tools/embedding-regeneration-progress",
            async (IActorRegistry actorRegistry, CancellationToken ct) =>
        {
            var embeddingRegenerationActor = await actorRegistry.GetAsync<EmbeddingRegenerationActorKey>(ct);
            var subscriberId = Guid.NewGuid().ToString();

            var subscription = await embeddingRegenerationActor.Ask<ProgressSubscription>(
                new SubscribeToProgress(subscriberId),
                ct);

            return TypedResults.ServerSentEvents(StreamProgress());

            async IAsyncEnumerable<SseItem<ProgressEvent>> StreamProgress()
            {
                try
                {
                    await foreach (var progress in subscription.Reader.ReadAllAsync(ct))
                    {
                        yield return new SseItem<ProgressEvent>(progress, "progress")
                        {
                            EventId = Guid.NewGuid().ToString()
                        };
                    }
                }
                finally
                {
                    embeddingRegenerationActor.Tell(new UnsubscribeFromProgress(subscriberId), ActorRefs.NoSender);
                }
            }
        });

        // SSE endpoint for version purge progress
        app.MapGet("/tools/version-purge-progress",
            async (IActorRegistry actorRegistry, CancellationToken ct) =>
        {
            var versionPurgeActor = await actorRegistry.GetAsync<VersionPurgeActorKey>(ct);
            var subscriberId = Guid.NewGuid().ToString();

            var subscription = await versionPurgeActor.Ask<ProgressSubscription>(
                new SubscribeToProgress(subscriberId),
                ct);

            return TypedResults.ServerSentEvents(StreamProgress());

            async IAsyncEnumerable<SseItem<ProgressEvent>> StreamProgress()
            {
                try
                {
                    await foreach (var progress in subscription.Reader.ReadAllAsync(ct))
                    {
                        yield return new SseItem<ProgressEvent>(progress, "progress")
                        {
                            EventId = Guid.NewGuid().ToString()
                        };
                    }
                }
                finally
                {
                    versionPurgeActor.Tell(new UnsubscribeFromProgress(subscriberId), ActorRefs.NoSender);
                }
            }
        });

        // SSE endpoint for dimension migration progress
        app.MapGet("/tools/dimension-migration-progress",
            async (IActorRegistry actorRegistry, CancellationToken ct) =>
        {
            var dimensionMigrationActor = await actorRegistry.GetAsync<DimensionMigrationActorKey>(ct);
            var subscriberId = Guid.NewGuid().ToString();

            var subscription = await dimensionMigrationActor.Ask<DimensionMigrationProgressSubscription>(
                new SubscribeToDimensionMigrationProgress(subscriberId),
                ct);

            return TypedResults.ServerSentEvents(StreamProgress());

            async IAsyncEnumerable<SseItem<ProgressEvent>> StreamProgress()
            {
                try
                {
                    await foreach (var progress in subscription.Reader.ReadAllAsync(ct))
                    {
                        yield return new SseItem<ProgressEvent>(progress, "progress")
                        {
                            EventId = Guid.NewGuid().ToString()
                        };
                    }
                }
                finally
                {
                    dimensionMigrationActor.Tell(new UnsubscribeFromDimensionMigrationProgress(subscriberId), ActorRefs.NoSender);
                }
            }
        });

        // SSE endpoint for markdown export progress
        app.MapGet("/tools/markdown-export-progress",
            async (IActorRegistry actorRegistry, CancellationToken ct) =>
        {
            var markdownExportActor = await actorRegistry.GetAsync<MarkdownExportActorKey>(ct);
            var subscriberId = Guid.NewGuid().ToString();

            var subscription = await markdownExportActor.Ask<ProgressSubscription>(
                new SubscribeToProgress(subscriberId),
                ct);

            return TypedResults.ServerSentEvents(StreamProgress());

            async IAsyncEnumerable<SseItem<ProgressEvent>> StreamProgress()
            {
                try
                {
                    await foreach (var progress in subscription.Reader.ReadAllAsync(ct))
                    {
                        yield return new SseItem<ProgressEvent>(progress, "progress")
                        {
                            EventId = Guid.NewGuid().ToString()
                        };
                    }
                }
                finally
                {
                    markdownExportActor.Tell(new UnsubscribeFromProgress(subscriberId), ActorRefs.NoSender);
                }
            }
        });

        return app;
    }
}
