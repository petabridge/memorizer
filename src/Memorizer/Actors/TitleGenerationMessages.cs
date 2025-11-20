namespace Memorizer.Actors;

/// <summary>
/// Base interface for title generation actor messages
/// </summary>
public interface ITitleGenerationMessage
{
}

/// <summary>
/// Message to request title generation for memories without titles
/// </summary>
public sealed record GenerateTitlesForUntitled : ITitleGenerationMessage
{
    /// <summary>
    /// Maximum number of memories to process in this batch
    /// </summary>
    public int BatchSize { get; init; } = 50;
    
    /// <summary>
    /// User who initiated the request
    /// </summary>
    public required string RequestedBy { get; init; }
}

/// <summary>
/// Message to generate a title for a specific memory
/// </summary>
public sealed record GenerateTitleForMemory : ITitleGenerationMessage
{
    /// <summary>
    /// ID of the memory to generate a title for
    /// </summary>
    public required Guid MemoryId { get; init; }
    
    /// <summary>
    /// Content of the memory (for performance, avoid re-fetch)
    /// </summary>
    public required string Content { get; init; }
    
    /// <summary>
    /// Type of the memory
    /// </summary>
    public required string Type { get; init; }
    
    /// <summary>
    /// Existing tags for context
    /// </summary>
    public string[]? Tags { get; init; }
    
    /// <summary>
    /// User who initiated the request
    /// </summary>
    public required string RequestedBy { get; init; }
}

/// <summary>
/// Message indicating title generation completed successfully
/// </summary>
public sealed record TitleGenerationCompleted : ITitleGenerationMessage
{
    /// <summary>
    /// ID of the memory that received a title
    /// </summary>
    public required Guid MemoryId { get; init; }
    
    /// <summary>
    /// The generated title
    /// </summary>
    public required string GeneratedTitle { get; init; }
    
    /// <summary>
    /// User who initiated the request
    /// </summary>
    public required string RequestedBy { get; init; }
}

/// <summary>
/// Message indicating title generation failed
/// </summary>
public sealed record TitleGenerationFailed : ITitleGenerationMessage
{
    /// <summary>
    /// ID of the memory that failed title generation
    /// </summary>
    public required Guid MemoryId { get; init; }
    
    /// <summary>
    /// Error that occurred during title generation
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
/// Represents a batch title generation job was completed
/// </summary>
/// <param name="RequestedBy">User who requested the operation</param>
/// <param name="StartTime">When the operation started</param>
/// <param name="TotalProcessed">Total number of memories processed</param>
/// <param name="TotalSuccessful">Number of memories that successfully got titles</param>
/// <param name="FailedMemoryIds">IDs of memories that failed to get titles</param>
/// <param name="Duration">How long the operation took</param>
public record BatchTitleGenerationCompleted(
    string RequestedBy,
    DateTime StartTime,
    int TotalProcessed,
    int TotalSuccessful,
    List<Guid> FailedMemoryIds,
    TimeSpan Duration
);

/// <summary>
/// Request the current status of the title generation batch job
/// </summary>
public record GetTitleGenerationStatus();

/// <summary>
/// Response containing the current status of the title generation batch job
/// </summary>
public record TitleGenerationStatus(
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
/// Actor registry key for the TitleGenerationActor
/// Used for dependency injection with IRequiredActor<TitleGenerationActorKey>
/// </summary>
public sealed class TitleGenerationActorKey; 