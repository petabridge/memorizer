using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Settings;

namespace Memorizer.Services;

public partial class MarkdownExportService : IMarkdownExportService
{
    private readonly MarkdownExportSettings _settings;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MarkdownExportService> _logger;
    private static readonly SemaphoreSlim _fileLock = new(1, 1);

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_settings.RootPath);

    public MarkdownExportService(
        MarkdownExportSettings settings,
        IServiceProvider serviceProvider,
        ILogger<MarkdownExportService> logger)
    {
        _settings = settings;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private IStorage GetStorage() => _serviceProvider.GetRequiredService<IStorage>();

    public async Task ExportMemoryAsync(Models.Memory memory, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var folderPath = await ResolveOwnerFolderAsync(memory.Owner, ct);
        var fileName = BuildFileName(memory.Title ?? "untitled", memory.Id);
        var filePath = Path.Combine(folderPath, fileName);

        var content = BuildMarkdownContent(memory);

        await _fileLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(folderPath);

            // Remove any existing file for this memory ID (title might have changed)
            RemoveExistingFileForId(folderPath, memory.Id);

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct);
            _logger.LogDebug("Exported memory {MemoryId} to {FilePath}", memory.Id, filePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task DeleteMemoryFileAsync(MemoryId id, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        await _fileLock.WaitAsync(ct);
        try
        {
            var idSuffix = GetIdSuffix(id);
            var files = Directory.GetFiles(_settings.RootPath!, $"*--{idSuffix}.md", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                File.Delete(file);
                _logger.LogDebug("Deleted memory file {FilePath}", file);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task RenameWorkspaceFolderAsync(WorkspaceId id, string oldSlug, string newSlug, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        // Build the path to the workspace folder by walking the ancestor chain
        var workspace = await GetStorage().GetWorkspaceAsync(id, ct);
        if (workspace == null) return;

        var parentPath = await BuildWorkspaceParentPath(id, ct);
        var oldPath = Path.Combine(parentPath, oldSlug);
        var newPath = Path.Combine(parentPath, newSlug);

        await _fileLock.WaitAsync(ct);
        try
        {
            if (Directory.Exists(oldPath) && oldPath != newPath)
            {
                Directory.Move(oldPath, newPath);
                _logger.LogInformation("Renamed workspace folder from {OldPath} to {NewPath}", oldPath, newPath);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task RenameProjectFolderAsync(ProjectId id, string oldSlug, string newSlug, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var project = await GetStorage().GetProjectAsync(id, ct);
        if (project == null) return;

        var projectParentPath = await BuildProjectParentPath(project, ct);
        var oldPath = Path.Combine(projectParentPath, oldSlug);
        var newPath = Path.Combine(projectParentPath, newSlug);

        await _fileLock.WaitAsync(ct);
        try
        {
            if (Directory.Exists(oldPath) && oldPath != newPath)
            {
                Directory.Move(oldPath, newPath);
                _logger.LogInformation("Renamed project folder from {OldPath} to {NewPath}", oldPath, newPath);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task MoveMemoryFileAsync(MemoryId id, MemoryOwner oldOwner, MemoryOwner newOwner, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var idSuffix = GetIdSuffix(id);

        // Find existing file
        var oldFolder = await ResolveOwnerFolderAsync(oldOwner, ct);
        string? existingFile = null;

        if (Directory.Exists(oldFolder))
        {
            var files = Directory.GetFiles(oldFolder, $"*--{idSuffix}.md");
            existingFile = files.FirstOrDefault();
        }

        if (existingFile == null)
        {
            // No file to move; export fresh
            var memory = await GetStorage().Get(id, ct);
            if (memory != null)
                await ExportMemoryAsync(memory, ct);
            return;
        }

        var newFolder = await ResolveOwnerFolderAsync(newOwner, ct);
        var newFilePath = Path.Combine(newFolder, Path.GetFileName(existingFile));

        await _fileLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(newFolder);
            File.Move(existingFile, newFilePath, overwrite: true);
            _logger.LogDebug("Moved memory file from {OldPath} to {NewPath}", existingFile, newFilePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<ExportResult> ExportAllAsync(
        WorkspaceId? workspaceFilter = null,
        ProjectId? projectFilter = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return new ExportResult();

        var result = new ExportResult();

        // Get all memories via pagination
        var allMemories = new List<Models.Memory>();
        const int pageSize = 100;
        int page = 1;

        while (!ct.IsCancellationRequested)
        {
            var (memories, totalCount) = await GetStorage().GetMemoriesPaginated(page, pageSize, cancellationToken: ct);
            if (memories.Count == 0) break;

            foreach (var m in memories)
            {
                // Apply filters
                if (workspaceFilter.HasValue)
                {
                    if (m.Owner.Type == OwnerTypeEnum.Workspace && m.Owner.WorkspaceId != workspaceFilter.Value)
                        continue;
                    if (m.Owner.Type == OwnerTypeEnum.Project)
                    {
                        var project = await GetStorage().GetProjectAsync(m.Owner.ProjectId!.Value, ct);
                        if (project == null || project.WorkspaceId != workspaceFilter.Value)
                            continue;
                    }
                }

                if (projectFilter.HasValue)
                {
                    if (m.Owner.Type != OwnerTypeEnum.Project || m.Owner.ProjectId != projectFilter.Value)
                        continue;
                }

                allMemories.Add(m);
            }

            if (memories.Count < pageSize) break;
            page++;
        }

        // Also include unfiled memories if no filter or workspace filter matches unfiled
        if (!projectFilter.HasValue && (!workspaceFilter.HasValue || workspaceFilter.Value.Value == Guid.Empty))
        {
            // Unfiled memories are already included via GetMemoriesPaginated
        }

        var progressState = new ExportProgress { TotalItems = allMemories.Count };
        progress?.Report(progressState);

        foreach (var memory in allMemories)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Skip system and archived memories
                if (memory.Archetype is ArchetypeEnum.System or ArchetypeEnum.Archived)
                {
                    result.TotalSkipped++;
                    progressState.ProcessedItems++;
                    progress?.Report(progressState);
                    continue;
                }

                await ExportMemoryAsync(memory, ct);
                result.TotalExported++;
                progressState.SuccessfulItems++;
            }
            catch (Exception ex)
            {
                result.TotalFailed++;
                result.Errors.Add($"Memory {memory.Id}: {ex.Message}");
                progressState.FailedItems++;
                _logger.LogWarning(ex, "Failed to export memory {MemoryId}", memory.Id);
            }

            progressState.ProcessedItems++;
            progress?.Report(progressState);
        }

        return result;
    }

    private async Task<string> ResolveOwnerFolderAsync(MemoryOwner owner, CancellationToken ct)
    {
        var root = _settings.RootPath!;

        if (owner.IsUnfiled)
            return Path.Combine(root, "unfiled");

        if (owner.Type == OwnerTypeEnum.Workspace)
        {
            var workspace = await GetStorage().GetWorkspaceAsync(owner.WorkspaceId!.Value, ct);
            if (workspace == null)
                return Path.Combine(root, "unfiled");

            return await BuildWorkspaceFolderPath(workspace, ct);
        }

        if (owner.Type == OwnerTypeEnum.Project)
        {
            var project = await GetStorage().GetProjectAsync(owner.ProjectId!.Value, ct);
            if (project == null)
                return Path.Combine(root, "unfiled");

            return await BuildProjectFolderPath(project, ct);
        }

        return Path.Combine(root, "unfiled");
    }

    private async Task<string> BuildWorkspaceFolderPath(Workspace workspace, CancellationToken ct)
    {
        var root = _settings.RootPath!;
        var segments = new List<string> { root };

        // Get ancestor path
        var ancestors = await GetStorage().GetWorkspacePathAsync(workspace.Id, ct);
        foreach (var ancestor in ancestors)
        {
            var ancestorWs = await GetStorage().GetWorkspaceAsync(ancestor.Id, ct);
            segments.Add(ancestorWs?.Slug ?? GenerateSlug(ancestor.Name));
        }

        segments.Add(workspace.Slug);
        return Path.Combine(segments.ToArray());
    }

    private async Task<string> BuildWorkspaceParentPath(WorkspaceId id, CancellationToken ct)
    {
        var root = _settings.RootPath!;
        var segments = new List<string> { root };

        var ancestors = await GetStorage().GetWorkspacePathAsync(id, ct);
        foreach (var ancestor in ancestors)
        {
            var ancestorWs = await GetStorage().GetWorkspaceAsync(ancestor.Id, ct);
            segments.Add(ancestorWs?.Slug ?? GenerateSlug(ancestor.Name));
        }

        return Path.Combine(segments.ToArray());
    }

    private async Task<string> BuildProjectFolderPath(Project project, CancellationToken ct)
    {
        // Start with workspace path
        var workspace = await GetStorage().GetWorkspaceAsync(project.WorkspaceId, ct);
        string workspacePath;
        if (workspace != null)
        {
            workspacePath = await BuildWorkspaceFolderPath(workspace, ct);
        }
        else
        {
            workspacePath = Path.Combine(_settings.RootPath!, "unfiled");
        }

        // Build project ancestor chain
        var projectSegments = new List<string>();
        var projectPath = await GetStorage().GetProjectPathAsync(project.Id, ct);
        foreach (var ancestor in projectPath.ProjectAncestors)
        {
            if (!ancestor.IsWorkspace)
            {
                var ancestorProject = await GetStorage().GetProjectAsync(new ProjectId(ancestor.Id), ct);
                projectSegments.Add(ancestorProject?.Slug ?? GenerateSlug(ancestor.Name));
            }
        }

        projectSegments.Add(project.Slug);

        return Path.Combine(workspacePath, Path.Combine(projectSegments.ToArray()));
    }

    private async Task<string> BuildProjectParentPath(Project project, CancellationToken ct)
    {
        var workspace = await GetStorage().GetWorkspaceAsync(project.WorkspaceId, ct);
        string workspacePath;
        if (workspace != null)
        {
            workspacePath = await BuildWorkspaceFolderPath(workspace, ct);
        }
        else
        {
            workspacePath = Path.Combine(_settings.RootPath!, "unfiled");
        }

        // Build project ancestor chain (excluding the project itself)
        var projectPath = await GetStorage().GetProjectPathAsync(project.Id, ct);
        var segments = new List<string> { workspacePath };
        foreach (var ancestor in projectPath.ProjectAncestors)
        {
            if (!ancestor.IsWorkspace)
            {
                var ancestorProject = await GetStorage().GetProjectAsync(new ProjectId(ancestor.Id), ct);
                segments.Add(ancestorProject?.Slug ?? GenerateSlug(ancestor.Name));
            }
        }

        return Path.Combine(segments.ToArray());
    }

    private static string BuildMarkdownContent(Models.Memory memory)
    {
        var sb = new StringBuilder();

        // YAML Frontmatter
        sb.AppendLine("---");
        sb.Append("title: \"").Append(EscapeYamlString(memory.Title ?? "Untitled")).AppendLine("\"");
        sb.Append("id: ").AppendLine(memory.Id.Value.ToString());
        sb.Append("type: ").AppendLine(memory.Type ?? "reference");

        if (memory.Tags is { Length: > 0 })
        {
            sb.AppendLine("tags:");
            foreach (var tag in memory.Tags)
            {
                sb.Append("  - ").AppendLine(tag);
            }
        }

        sb.Append("confidence: ").AppendLine(((double)memory.Confidence).ToString(CultureInfo.InvariantCulture));
        sb.Append("source: ").AppendLine(memory.Source ?? "unknown");
        sb.Append("archetype: ").AppendLine(memory.Archetype.ToString().ToLowerInvariant());
        sb.Append("version: ").AppendLine(((int)memory.CurrentVersion).ToString());
        sb.Append("created: ").AppendLine(memory.CreatedAt.ToString("O"));
        sb.Append("updated: ").AppendLine(memory.UpdatedAt.ToString("O"));
        sb.AppendLine("---");
        sb.AppendLine();

        // Body
        sb.Append(memory.Text);

        return sb.ToString();
    }

    internal static string BuildFileName(string title, MemoryId id)
    {
        var slug = GenerateSlug(title);
        var idSuffix = GetIdSuffix(id);
        return $"{slug}--{idSuffix}.md";
    }

    private static string GetIdSuffix(MemoryId id)
    {
        return id.Value.ToString("N")[..8];
    }

    private void RemoveExistingFileForId(string folder, MemoryId id)
    {
        if (!Directory.Exists(folder)) return;

        var idSuffix = GetIdSuffix(id);
        var existing = Directory.GetFiles(folder, $"*--{idSuffix}.md");
        foreach (var file in existing)
        {
            File.Delete(file);
        }
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("--", "-");

        slug = SlugRegex().Replace(slug, "");
        slug = slug.Trim('-');

        return string.IsNullOrEmpty(slug) ? "unnamed" : slug;
    }

    private static string EscapeYamlString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex SlugRegex();
}
