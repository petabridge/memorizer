using Memorizer.Models;

namespace Memorizer.Actors;

public sealed record StartMarkdownExport
{
    public required string RequestedBy { get; init; }
    public WorkspaceId? WorkspaceFilter { get; init; }
    public ProjectId? ProjectFilter { get; init; }
}

public record GetMarkdownExportStatus;

public record MarkdownExportStatus(
    bool IsRunning,
    string Status,
    int? TotalProcessed = null,
    int? TotalSuccessful = null,
    int? TotalFailed = null,
    int? TotalSkipped = null,
    int? Outstanding = null,
    DateTime? StartTime = null,
    TimeSpan? Duration = null,
    string? RequestedBy = null
);

public sealed record MarkdownExportCompleted(
    string RequestedBy,
    DateTime StartTime,
    int TotalExported,
    int TotalFailed,
    int TotalSkipped,
    TimeSpan Duration
);

public sealed class MarkdownExportActorKey;
