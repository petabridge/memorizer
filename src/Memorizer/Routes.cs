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

            // Subscribe to progress updates - actor returns a ChannelReader<ProgressEvent>
            var subscription = await titleGenActor.Ask<ProgressSubscription>(
                new SubscribeToProgress(subscriberId),
                ct);

            return TypedResults.ServerSentEvents(StreamProgress());

            async IAsyncEnumerable<SseItem<ProgressSseData>> StreamProgress()
            {
                try
                {
                    // ChannelReader can be consumed directly as IAsyncEnumerable
                    await foreach (var progress in subscription.Reader.ReadAllAsync(ct))
                    {
                        yield return new SseItem<ProgressSseData>(
                            new ProgressSseData(
                                PercentComplete: progress.PercentComplete,
                                TotalProcessed: progress.TotalProcessed,
                                TotalSuccessful: progress.TotalSuccessful,
                                TotalFailed: progress.TotalFailed,
                                Outstanding: progress.Outstanding,
                                Status: progress.Status.ToString(),
                                RequestedBy: progress.RequestedBy,
                                Duration: progress.DurationSeconds
                            ),
                            "progress")
                        {
                            EventId = Guid.NewGuid().ToString()
                        };
                    }
                }
                finally
                {
                    // Notify actor that this subscriber has disconnected
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

            // Subscribe to progress updates - actor returns a ChannelReader<ProgressEvent>
            var subscription = await metadataEmbeddingActor.Ask<ProgressSubscription>(
                new SubscribeToProgress(subscriberId),
                ct);

            async IAsyncEnumerable<SseItem<ProgressSseData>> StreamProgress()
            {
                try
                {
                    // ChannelReader can be consumed directly as IAsyncEnumerable
                    await foreach (var progress in subscription.Reader.ReadAllAsync(ct))
                    {
                        yield return new SseItem<ProgressSseData>(
                            new ProgressSseData(
                                PercentComplete: progress.PercentComplete,
                                TotalProcessed: progress.TotalProcessed,
                                TotalSuccessful: progress.TotalSuccessful,
                                TotalFailed: progress.TotalFailed,
                                Outstanding: progress.Outstanding,
                                Status: progress.Status.ToString(),
                                RequestedBy: progress.RequestedBy,
                                Duration: progress.DurationSeconds
                            ),
                            "progress")
                        {
                            EventId = Guid.NewGuid().ToString()
                        };
                    }
                }
                finally
                {
                    // Notify actor that this subscriber has disconnected
                    metadataEmbeddingActor.Tell(new UnsubscribeFromProgress(subscriberId), ActorRefs.NoSender);
                }
            }

            return TypedResults.ServerSentEvents(StreamProgress());
        });

        return app;
    }
}
