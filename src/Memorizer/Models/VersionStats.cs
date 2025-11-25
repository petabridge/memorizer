namespace Memorizer.Models;

public class VersionStats
{
    public int TotalMemories { get; init; }
    public int TotalVersions { get; init; }
    public int TotalEvents { get; init; }
    public double AverageVersionsPerMemory { get; init; }
    public int MemoriesWithMultipleVersions { get; init; }
    public DateTime? OldestVersion { get; init; }
    public DateTime? NewestVersion { get; init; }
    public long EstimatedStorageBytes { get; init; }
}
