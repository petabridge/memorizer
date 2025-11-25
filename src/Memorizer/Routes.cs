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
        app.MapGet("/ui/tools/title-generation-progress",
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

        // SSE endpoint for metadata embedding progress
        app.MapGet("/ui/tools/metadata-embedding-progress",
            async (IActorRegistry actorRegistry, CancellationToken ct) =>
        {
            var metadataEmbeddingActor = await actorRegistry.GetAsync<MetadataEmbeddingActorKey>(ct);
            var subscriberId = Guid.NewGuid().ToString();

            var subscription = await metadataEmbeddingActor.Ask<ProgressSubscription>(
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
                    metadataEmbeddingActor.Tell(new UnsubscribeFromProgress(subscriberId), ActorRefs.NoSender);
                }
            }
        });

        return app;
    }
}
