namespace Memorizer.Settings;

public class VersioningSettings
{
    /// <summary>
    /// Maximum number of versions to retain per memory.
    /// When exceeded, oldest versions are automatically deleted.
    /// Set to 0 or negative to disable automatic pruning.
    /// </summary>
    public int MaxVersionsPerMemory { get; init; } = 50;
}
