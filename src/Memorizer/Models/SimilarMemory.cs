namespace Memorizer.Models;

/// <summary>
/// Represents a memory that is similar to another memory,
/// including similarity score and relationship status.
/// </summary>
public class SimilarMemory
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Similarity score from 0.0 to 1.0 (higher is more similar).
    /// </summary>
    public double Similarity { get; init; }

    /// <summary>
    /// Whether a relationship already exists between the source memory and this one.
    /// </summary>
    public bool HasExistingRelationship { get; init; }
}
