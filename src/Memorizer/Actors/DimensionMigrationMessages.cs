using System.Threading.Channels;
using Akka.Streams;
using Memorizer.Models;

namespace Memorizer.Actors;

/// <summary>
/// Request to start a dimension migration.
/// This will ALTER the database schema and then trigger embedding regeneration.
/// </summary>
public sealed record StartDimensionMigration(
    string RequestedBy = "user"
);

/// <summary>
/// Request to resume an interrupted dimension migration.
/// </summary>
public sealed record ResumeDimensionMigration(
    Guid MigrationId,
    string RequestedBy
);

/// <summary>
/// Request to get the current status of dimension migration.
/// </summary>
public sealed record GetDimensionMigrationStatus;

/// <summary>
/// Response with current dimension migration status.
/// </summary>
public sealed record DimensionMigrationStatus(
    bool IsRunning,
    string Status,
    int? OldDimensions = null,
    int? NewDimensions = null,
    string? OldModel = null,
    string? NewModel = null,
    int TotalMemories = 0,
    int Processed = 0,
    int Successful = 0,
    int Failed = 0,
    DateTime? StartTime = null,
    TimeSpan? Duration = null,
    Guid? MigrationId = null,
    string? RequestedBy = null,
    string? ErrorMessage = null
);

/// <summary>
/// Subscribe to dimension migration progress updates via SSE.
/// </summary>
public sealed record SubscribeToDimensionMigrationProgress(string SubscriberId);

/// <summary>
/// Unsubscribe from dimension migration progress updates.
/// </summary>
public sealed record UnsubscribeFromDimensionMigrationProgress(string SubscriberId);

/// <summary>
/// Response with subscription channel for progress updates.
/// </summary>
public sealed record DimensionMigrationProgressSubscription(
    string SubscriberId,
    ChannelReader<ProgressEvent> Reader
);

/// <summary>
/// Internal message: Schema change completed, start regeneration.
/// </summary>
internal sealed record SchemaChangeCompleted(
    Guid MigrationId,
    int NewDimensions
);

/// <summary>
/// Internal message: Embedding regeneration phase of dimension migration completed.
/// </summary>
internal sealed record DimensionMigrationRegenerationCompleted(
    Guid MigrationId,
    int TotalProcessed,
    int Successful,
    int Failed
);

/// <summary>
/// Event published when dimension migration completes.
/// </summary>
public sealed record DimensionMigrationCompleted(
    Guid MigrationId,
    string OldModel,
    int OldDimensions,
    string NewModel,
    int NewDimensions,
    int TotalProcessed,
    int Successful,
    int Failed,
    TimeSpan Duration,
    string RequestedBy
);

/// <summary>
/// Event published when dimension migration fails.
/// </summary>
public sealed record DimensionMigrationFailed(
    Guid MigrationId,
    string ErrorMessage,
    string RequestedBy
);

/// <summary>
/// Actor registry key for DimensionMigrationActor.
/// </summary>
public sealed class DimensionMigrationActorKey;
