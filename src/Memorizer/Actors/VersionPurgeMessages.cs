namespace Memorizer.Actors;

/// <summary>
/// Base interface for version purge actor messages
/// </summary>
public interface IVersionPurgeMessage
{
}

/// <summary>
/// Message to request purging versions older than a specified age
/// </summary>
public sealed record PurgeVersionsByAge : IVersionPurgeMessage
{
    /// <summary>
    /// Minimum age in days for versions to be deleted
    /// </summary>
    public int DaysOld { get; init; } = 30;

    /// <summary>
    /// User who initiated the request
    /// </summary>
    public required string RequestedBy { get; init; }
}

/// <summary>
/// Message indicating a version was successfully purged
/// </summary>
public sealed record VersionPurgeCompleted : IVersionPurgeMessage
{
    /// <summary>
    /// ID of the memory whose version was purged
    /// </summary>
    public required Guid MemoryId { get; init; }

    /// <summary>
    /// Version number that was purged
    /// </summary>
    public required int VersionNumber { get; init; }

    /// <summary>
    /// User who initiated the request
    /// </summary>
    public required string RequestedBy { get; init; }
}

/// <summary>
/// Message indicating a version purge failed
/// </summary>
public sealed record VersionPurgeFailed : IVersionPurgeMessage
{
    /// <summary>
    /// ID of the memory whose version failed to purge
    /// </summary>
    public required Guid MemoryId { get; init; }

    /// <summary>
    /// Version number that failed to purge
    /// </summary>
    public required int VersionNumber { get; init; }

    /// <summary>
    /// Error that occurred during purge
    /// </summary>
    public required string ErrorMessage { get; init; }

    /// <summary>
    /// User who initiated the request
    /// </summary>
    public required string RequestedBy { get; init; }

    /// <summary>
    /// The exception that caused the failure, if any
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Represents a batch version purge job was completed
/// </summary>
/// <param name="RequestedBy">User who requested the operation</param>
/// <param name="StartTime">When the operation started</param>
/// <param name="TotalVersionsPurged">Total number of versions purged</param>
/// <param name="TotalEventsPurged">Total number of audit events purged</param>
/// <param name="TotalFailed">Number of versions that failed to purge</param>
/// <param name="Duration">How long the operation took</param>
public record BatchVersionPurgeCompleted(
    string RequestedBy,
    DateTime StartTime,
    int TotalVersionsPurged,
    int TotalEventsPurged,
    int TotalFailed,
    TimeSpan Duration
);

/// <summary>
/// Request the current status of the version purge batch job
/// </summary>
public record GetVersionPurgeStatus();

/// <summary>
/// Response containing the current status of the version purge batch job
/// </summary>
public record VersionPurgeStatus(
    bool IsRunning,
    string Status,
    int? TotalProcessed = null,
    int? TotalSuccessful = null,
    int? TotalFailed = null,
    int? Outstanding = null,
    DateTime? StartTime = null,
    TimeSpan? Duration = null,
    string? RequestedBy = null
);

/// <summary>
/// Actor registry key for the VersionPurgeActor
/// Used for dependency injection with IRequiredActor<VersionPurgeActorKey>
/// </summary>
public sealed class VersionPurgeActorKey;
