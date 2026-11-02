namespace Memorizer.Models;

/// <summary>
/// A single tag and the number of non-archived memories using it.
/// </summary>
public class TagCount
{
    public string Tag { get; init; } = "";
    public int Count { get; init; }
}
