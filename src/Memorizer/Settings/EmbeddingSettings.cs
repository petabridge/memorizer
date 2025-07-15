namespace Memorizer.Settings;

public class EmbeddingSettings
{   
    public required Uri ApiUrl { get; init; }
    public required string Model { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}