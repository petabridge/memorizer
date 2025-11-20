using Akka;
using Akka.Streams;
using Akka.Streams.Dsl;

namespace Memorizer.Actors;

/// <summary>
/// Base interface for all progress events.
/// Since title generation and metadata embedding both track memory operations,
/// they can share this common structure.
/// </summary>
public interface IProgressEvent
{
    /// <summary>
    /// Total number of items processed so far
    /// </summary>
    int TotalProcessed { get; }

    /// <summary>
    /// Number of successful operations
    /// </summary>
    int TotalSuccessful { get; }

    /// <summary>
    /// Number of failed operations
    /// </summary>
    int TotalFailed { get; }

    /// <summary>
    /// Number of items still to process
    /// </summary>
    int Outstanding { get; }

    /// <summary>
    /// Current status description
    /// </summary>
    string Status { get; }

    /// <summary>
    /// Who initiated this operation
    /// </summary>
    string RequestedBy { get; }

    /// <summary>
    /// How long the operation has been running
    /// </summary>
    TimeSpan? Duration { get; }
}

/// <summary>
/// Progress event for title generation operations
/// </summary>
public sealed record TitleGenerationProgressEvent(
    int TotalProcessed,
    int TotalSuccessful,
    int TotalFailed,
    int Outstanding,
    string Status,
    string RequestedBy,
    TimeSpan? Duration = null,
    List<Guid>? FailedMemoryIds = null
) : IProgressEvent;

/// <summary>
/// Progress event for metadata embedding operations
/// </summary>
public sealed record MetadataEmbeddingProgressEvent(
    int TotalProcessed,
    int TotalSuccessful,
    int TotalFailed,
    int Outstanding,
    string Status,
    string RequestedBy,
    TimeSpan? Duration = null,
    List<Guid>? FailedMemoryIds = null
) : IProgressEvent;

/// <summary>
/// Generic request to get a progress source from an actor.
/// The actor returns a Source that emits typed progress events.
/// </summary>
/// <typeparam name="TEvent">The type of progress event this source emits</typeparam>
public sealed record GetProgressSource<TEvent>() where TEvent : IProgressEvent;

/// <summary>
/// Response containing a typed Akka.Streams Source
/// </summary>
/// <typeparam name="TEvent">The type of progress event this source emits</typeparam>
public sealed record ProgressSource<TEvent>(Source<TEvent, NotUsed> Source) where TEvent : IProgressEvent;

/// <summary>
/// SSE-compatible progress data sent to clients
/// </summary>
public sealed record ProgressSseData(
    int PercentComplete,
    int TotalProcessed,
    int TotalSuccessful,
    int TotalFailed,
    int Outstanding,
    string Status,
    string RequestedBy,
    double? Duration
);
