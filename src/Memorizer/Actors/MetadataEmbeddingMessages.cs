namespace Memorizer.Actors;

/// <summary>
/// Request to regenerate metadata embeddings for all memories using pagination
/// </summary>
public record RegenerateAllMetadataEmbeddings(
    int PageSize = 100,
    string RequestedBy = "system"
);

/// <summary>
/// Request to generate metadata embedding for a specific memory
/// </summary>
public record GenerateMetadataEmbeddingForMemory(
    Guid MemoryId,
    string Title,
    string[] Tags,
    string RequestedBy
);

/// <summary>
/// Notification that metadata embedding generation completed for a memory
/// </summary>
public record MetadataEmbeddingCompleted(
    Guid MemoryId,
    string RequestedBy
);

/// <summary>
/// Notification that metadata embedding generation failed for a memory
/// </summary>
public record MetadataEmbeddingFailed(
    Guid MemoryId,
    string ErrorMessage,
    string RequestedBy,
    Exception? Exception = null
);

/// <summary>
/// Notification that a batch of metadata embedding operations completed
/// </summary>
public record BatchMetadataEmbeddingCompleted(
    string RequestedBy,
    DateTime StartTime,
    int TotalProcessed,
    int TotalSuccessful,
    List<Guid> FailedMemoryIds,
    TimeSpan Duration
);

/// <summary>
/// Request the current status of the metadata embedding batch job
/// </summary>
public record GetMetadataEmbeddingStatus();

/// <summary>
/// Response containing the current status of the metadata embedding batch job
/// </summary>
public record MetadataEmbeddingStatus(
    bool IsRunning,
    string Status,
    int? TotalProcessed = null,
    int? TotalSuccessful = null,
    int? TotalFailed = null,
    int? Outstanding = null,
    List<Guid>? FailedMemoryIds = null,
    DateTime? StartTime = null,
    TimeSpan? Duration = null,
    string? RequestedBy = null
);

/// <summary>
/// Actor key for MetadataEmbeddingActor dependency injection
/// Used for dependency injection with IRequiredActor
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class MetadataEmbeddingActorKey;