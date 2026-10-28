using Memorizer.Models;

namespace Memorizer.Services;

public class ExportProgress
{
    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public int SuccessfulItems { get; set; }
    public int FailedItems { get; set; }
}

public class ExportResult
{
    public int TotalExported { get; set; }
    public int TotalFailed { get; set; }
    public int TotalSkipped { get; set; }
    public List<string> Errors { get; set; } = [];
}

public interface IMarkdownExportService
{
    bool IsEnabled { get; }

    Task ExportMemoryAsync(Models.Memory memory, CancellationToken ct = default);

    Task DeleteMemoryFileAsync(MemoryId id, CancellationToken ct = default);

    Task RenameWorkspaceFolderAsync(WorkspaceId id, string oldSlug, string newSlug, CancellationToken ct = default);

    Task RenameProjectFolderAsync(ProjectId id, string oldSlug, string newSlug, CancellationToken ct = default);

    Task MoveMemoryFileAsync(MemoryId id, MemoryOwner oldOwner, MemoryOwner newOwner, CancellationToken ct = default);

    Task<ExportResult> ExportAllAsync(
        WorkspaceId? workspaceFilter = null,
        ProjectId? projectFilter = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default);
}
