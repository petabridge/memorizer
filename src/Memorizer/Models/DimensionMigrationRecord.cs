namespace Memorizer.Models;

/// <summary>
/// Represents a dimension migration run, including in-progress and completed migrations.
/// Used for audit trail and resumability of interrupted migrations.
/// </summary>
public class DimensionMigrationRecord
{
    /// <summary>
    /// Unique identifier for this migration run
    /// </summary>
    public Guid MigrationId { get; init; }

    /// <summary>
    /// When the migration started
    /// </summary>
    public DateTime StartedAt { get; init; }

    /// <summary>
    /// When the migration completed (null if still running or failed)
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// The model name before migration
    /// </summary>
    public required string OldModel { get; init; }

    /// <summary>
    /// The dimensions before migration
    /// </summary>
    public int OldDimensions { get; init; }

    /// <summary>
    /// The model name after migration
    /// </summary>
    public required string NewModel { get; init; }

    /// <summary>
    /// The target dimensions for migration
    /// </summary>
    public int NewDimensions { get; init; }

    /// <summary>
    /// Current status: 'running', 'schema_changed', 'regenerating', 'completed', 'failed'
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Error message if migration failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Total number of memories to process
    /// </summary>
    public int TotalMemories { get; init; }

    /// <summary>
    /// Number of memories processed so far
    /// </summary>
    public int MemoriesProcessed { get; init; }

    /// <summary>
    /// Number of memories successfully migrated
    /// </summary>
    public int MemoriesSuccessful { get; init; }

    /// <summary>
    /// Number of memories that failed to migrate
    /// </summary>
    public int MemoriesFailed { get; init; }

    /// <summary>
    /// ID of the last successfully processed memory (for resumability)
    /// </summary>
    public Guid? LastProcessedMemoryId { get; init; }

    /// <summary>
    /// IDs of memories that failed to migrate
    /// </summary>
    public Guid[] FailedMemoryIds { get; init; } = [];

    /// <summary>
    /// Who/what requested this migration
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Computed duration of the migration
    /// </summary>
    public TimeSpan? Duration => CompletedAt.HasValue
        ? CompletedAt.Value - StartedAt
        : DateTime.UtcNow - StartedAt;

    /// <summary>
    /// Whether this migration can be resumed (running or schema_changed status)
    /// </summary>
    public bool CanResume => Status is "running" or "schema_changed" or "regenerating";

    /// <summary>
    /// Whether this migration is actively running
    /// </summary>
    public bool IsRunning => Status is "running" or "schema_changed" or "regenerating";
}
