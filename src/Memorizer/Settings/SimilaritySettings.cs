namespace Memorizer.Settings;

/// <summary>
/// Settings for the memory similarity discovery feature.
/// </summary>
public class SimilaritySettings
{
    /// <summary>
    /// Default similarity threshold (0.0 to 1.0). Memories with similarity below this won't be shown.
    /// </summary>
    public double DefaultThreshold { get; init; } = 0.7;

    /// <summary>
    /// Minimum threshold the UI slider can be set to.
    /// </summary>
    public double MinThreshold { get; init; } = 0.5;

    /// <summary>
    /// Maximum threshold the UI slider can be set to.
    /// </summary>
    public double MaxThreshold { get; init; } = 0.95;

    /// <summary>
    /// Default number of similar memories to return.
    /// </summary>
    public int DefaultLimit { get; init; } = 10;
}
