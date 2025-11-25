namespace Memorizer.Actors;

/// <summary>
/// Request to regenerate ALL embeddings (content + metadata) for all memories using pagination.
/// Used when embedding dimensions or model changes.
/// </summary>
public record RegenerateAllEmbeddings(
    int PageSize = 100,
    string RequestedBy = "system"
);

/// <summary>
/// Request to regenerate embeddings for a specific memory
/// </summary>
public record RegenerateEmbeddingsForMemory(
    Guid MemoryId,
    string Title,
    string Text,
    string[] Tags,
    string RequestedBy
);

/// <summary>
/// Notification that embedding regeneration completed for a memory
/// </summary>
public record EmbeddingRegenerationCompleted(
    Guid MemoryId,
    string RequestedBy
);

/// <summary>
/// Notification that embedding regeneration failed for a memory
/// </summary>
public record EmbeddingRegenerationFailed(
    Guid MemoryId,
    string ErrorMessage,
    string RequestedBy,
    Exception? Exception = null
);

/// <summary>
/// Notification that a batch of embedding regeneration operations completed
/// </summary>
public record BatchEmbeddingRegenerationCompleted(
    string RequestedBy,
    DateTime StartTime,
    int TotalProcessed,
    int TotalSuccessful,
    List<Guid> FailedMemoryIds,
    TimeSpan Duration
);

/// <summary>
/// Request the current status of the embedding regeneration batch job
/// </summary>
public record GetEmbeddingRegenerationStatus();

/// <summary>
/// Response containing the current status of the embedding regeneration batch job
/// </summary>
public record EmbeddingRegenerationStatus(
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
/// Actor key for EmbeddingRegenerationActor dependency injection
/// Used for dependency injection with IRequiredActor
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class EmbeddingRegenerationActorKey;
