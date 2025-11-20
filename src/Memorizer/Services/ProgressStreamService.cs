using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Akka.Streams;
using Akka.Streams.Dsl;
using Memorizer.Actors;

namespace Memorizer.Services;

/// <summary>
/// Represents a progress update event for SSE streaming
/// </summary>
public record ProgressUpdate(
    int PercentComplete,
    int TotalProcessed,
    int TotalSuccessful,
    int TotalFailed,
    int Outstanding,
    string Status,
    string RequestedBy,
    TimeSpan? Duration = null
);

/// <summary>
/// Service that provides IAsyncEnumerable streams of progress updates
/// using Akka.Streams to bridge actor events to Server-Sent Events
/// </summary>
public sealed class ProgressStreamService
{
    private readonly ActorSystem _actorSystem;
    private readonly IMaterializer _materializer;
    private readonly IRequiredActor<TitleGenerationActorKey> _titleGenerationActor;
    private readonly IRequiredActor<MetadataEmbeddingActorKey> _metadataEmbeddingActor;

    public ProgressStreamService(
        ActorSystem actorSystem,
        IRequiredActor<TitleGenerationActorKey> titleGenerationActor,
        IRequiredActor<MetadataEmbeddingActorKey> metadataEmbeddingActor)
    {
        _actorSystem = actorSystem;
        _materializer = actorSystem.Materializer();
        _titleGenerationActor = titleGenerationActor;
        _metadataEmbeddingActor = metadataEmbeddingActor;
    }

    /// <summary>
    /// Stream title generation progress updates as IAsyncEnumerable for SSE
    /// </summary>
    public async IAsyncEnumerable<ProgressUpdate> GetTitleGenerationProgressStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Use periodic polling to get status updates
        var statusPollSource = Source.Tick(
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500),
            "poll");

        var progressStream = statusPollSource
            .SelectAsync(1, async _ =>
            {
                // Poll for current status
                var status = await _titleGenerationActor.ActorRef
                    .Ask<TitleGenerationStatus>(new GetTitleGenerationStatus(), TimeSpan.FromSeconds(2));
                return status;
            })
            .Where(status => status.IsRunning) // Only emit when actually running
            .Select(status => ConvertToProgressUpdate(status))
            .RunAsAsyncEnumerable(_materializer);

        await foreach (var progress in progressStream.WithCancellation(ct))
        {
            yield return progress;
        }
    }

    /// <summary>
    /// Stream metadata embedding progress updates as IAsyncEnumerable for SSE
    /// </summary>
    public async IAsyncEnumerable<ProgressUpdate> GetMetadataEmbeddingProgressStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Use periodic polling to get status updates
        var statusPollSource = Source.Tick(
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500),
            "poll");

        var progressStream = statusPollSource
            .SelectAsync(1, async _ =>
            {
                // Poll for current status
                var status = await _metadataEmbeddingActor.ActorRef
                    .Ask<MetadataEmbeddingStatus>(new GetMetadataEmbeddingStatus(), TimeSpan.FromSeconds(2));
                return status;
            })
            .Where(status => status.IsRunning) // Only emit when actually running
            .Select(status => ConvertToProgressUpdate(status))
            .RunAsAsyncEnumerable(_materializer);

        await foreach (var progress in progressStream.WithCancellation(ct))
        {
            yield return progress;
        }
    }

    private static ProgressUpdate ConvertToProgressUpdate(TitleGenerationStatus status)
    {
        var total = (status.TotalProcessed ?? 0) + (status.Outstanding ?? 0);
        var percentComplete = total > 0 ? (int)((status.TotalProcessed ?? 0) * 100.0 / total) : 0;

        return new ProgressUpdate(
            PercentComplete: percentComplete,
            TotalProcessed: status.TotalProcessed ?? 0,
            TotalSuccessful: status.TotalSuccessful ?? 0,
            TotalFailed: status.TotalFailed ?? 0,
            Outstanding: status.Outstanding ?? 0,
            Status: status.Status,
            RequestedBy: status.RequestedBy ?? "unknown",
            Duration: status.Duration
        );
    }

    private static ProgressUpdate ConvertToProgressUpdate(MetadataEmbeddingStatus status)
    {
        var total = (status.TotalProcessed ?? 0) + (status.Outstanding ?? 0);
        var percentComplete = total > 0 ? (int)((status.TotalProcessed ?? 0) * 100.0 / total) : 0;

        return new ProgressUpdate(
            PercentComplete: percentComplete,
            TotalProcessed: status.TotalProcessed ?? 0,
            TotalSuccessful: status.TotalSuccessful ?? 0,
            TotalFailed: status.TotalFailed ?? 0,
            Outstanding: status.Outstanding ?? 0,
            Status: status.Status,
            RequestedBy: status.RequestedBy ?? "unknown",
            Duration: status.Duration
        );
    }
}
