using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Memorizer.Actors;

/// <summary>
/// Job status enum - typed status for progress events
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    /// <summary>No job is currently running</summary>
    Idle,

    /// <summary>Job is actively processing items</summary>
    Running,

    /// <summary>Job completed successfully</summary>
    CompletedSuccess,

    /// <summary>Job completed but some items failed</summary>
    CompletedWithErrors,

    /// <summary>Job started but found nothing to process</summary>
    NoWorkToDo,

    /// <summary>Job failed entirely</summary>
    Failed
}

/// <summary>
/// Unified progress event used by all background job actors.
/// This single type replaces TitleGenerationProgressEvent and MetadataEmbeddingProgressEvent.
/// Can be serialized directly to SSE without an intermediate type.
/// </summary>
public sealed record ProgressEvent(
    /// <summary>Total number of items in this job (set when job starts - "job sizing")</summary>
    int TotalItems,

    /// <summary>Total number of items processed so far</summary>
    int TotalProcessed,

    /// <summary>Number of successful operations</summary>
    int TotalSuccessful,

    /// <summary>Number of failed operations</summary>
    int TotalFailed,

    /// <summary>Number of items still to process</summary>
    int Outstanding,

    /// <summary>Typed job status</summary>
    JobStatus Status,

    /// <summary>Who initiated this operation</summary>
    string RequestedBy,

    /// <summary>How long the operation has been running</summary>
    TimeSpan? Duration = null,

    /// <summary>IDs of memories that failed processing</summary>
    List<Guid>? FailedIds = null,

    /// <summary>Optional status message for detailed progress updates</summary>
    string? Message = null
)
{
    /// <summary>
    /// Calculated percent complete (0-100)
    /// </summary>
    public int PercentComplete => TotalItems > 0 ? (int)((TotalProcessed / (double)TotalItems) * 100) : 0;

    /// <summary>
    /// Duration in seconds - JSON-friendly computed property
    /// </summary>
    public double? DurationSeconds => Duration?.TotalSeconds;

    /// <summary>
    /// Check if this event represents a completed or terminal job state (used by AutoCompleteOnFinished stage).
    /// Idle is included because when returned to a subscriber, it means "no job running, you're done".
    /// </summary>
    public bool IsCompleted => Status is JobStatus.Idle
        or JobStatus.CompletedSuccess
        or JobStatus.CompletedWithErrors
        or JobStatus.NoWorkToDo
        or JobStatus.Failed;
}

/// <summary>
/// Request to subscribe to progress updates.
/// Sent by SSE endpoint to actor via Ask.
/// </summary>
public sealed record SubscribeToProgress(string SubscriberId);

/// <summary>
/// Response containing the channel reader for progress events.
/// The channel reader can be consumed as IAsyncEnumerable for SSE streaming.
/// </summary>
public sealed record ProgressSubscription(string SubscriberId, ChannelReader<ProgressEvent> Reader);

/// <summary>
/// Request to unsubscribe from progress updates.
/// Sent when client disconnects (via CancellationToken).
/// </summary>
public sealed record UnsubscribeFromProgress(string SubscriberId);

