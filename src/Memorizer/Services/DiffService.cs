using System.Web;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Registrator.Net;

namespace Memorizer.Services;

public enum DiffLineType
{
    Unchanged,
    Added,
    Removed,
    Modified
}

public class DiffLine
{
    public DiffLineType Type { get; init; }
    public string Text { get; init; } = string.Empty;
    public int? OldPosition { get; init; }
    public int? NewPosition { get; init; }
}

public class DiffResult
{
    public List<DiffLine> Lines { get; init; } = new();
    public bool HasChanges { get; init; }
    public int AddedCount { get; init; }
    public int RemovedCount { get; init; }
    public int ModifiedCount { get; init; }
}

public interface IDiffService
{
    DiffResult ComputeDiff(string? oldText, string? newText);
    string RenderInlineHtml(DiffResult diff);
}

[AutoRegisterInterfaces(ServiceLifetime.Singleton)]
public class DiffService : IDiffService
{
    private readonly InlineDiffBuilder _diffBuilder;

    public DiffService()
    {
        _diffBuilder = new InlineDiffBuilder(new Differ());
    }

    public DiffResult ComputeDiff(string? oldText, string? newText)
    {
        var diff = _diffBuilder.BuildDiffModel(oldText ?? "", newText ?? "");

        int addedCount = 0;
        int removedCount = 0;
        int modifiedCount = 0;
        int oldLine = 0;
        int newLine = 0;

        var lines = new List<DiffLine>();

        foreach (var line in diff.Lines)
        {
            DiffLineType lineType;
            int? oldPos = null;
            int? newPos = null;

            switch (line.Type)
            {
                case ChangeType.Inserted:
                    lineType = DiffLineType.Added;
                    newLine++;
                    newPos = newLine;
                    addedCount++;
                    break;
                case ChangeType.Deleted:
                    lineType = DiffLineType.Removed;
                    oldLine++;
                    oldPos = oldLine;
                    removedCount++;
                    break;
                case ChangeType.Modified:
                    lineType = DiffLineType.Modified;
                    oldLine++;
                    newLine++;
                    oldPos = oldLine;
                    newPos = newLine;
                    modifiedCount++;
                    break;
                case ChangeType.Unchanged:
                default:
                    lineType = DiffLineType.Unchanged;
                    oldLine++;
                    newLine++;
                    oldPos = oldLine;
                    newPos = newLine;
                    break;
            }

            lines.Add(new DiffLine
            {
                Type = lineType,
                Text = line.Text ?? "",
                OldPosition = oldPos,
                NewPosition = newPos
            });
        }

        return new DiffResult
        {
            Lines = lines,
            HasChanges = diff.HasDifferences,
            AddedCount = addedCount,
            RemovedCount = removedCount,
            ModifiedCount = modifiedCount
        };
    }

    public string RenderInlineHtml(DiffResult diff)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"diff-view\">");

        foreach (var line in diff.Lines)
        {
            var cssClass = line.Type switch
            {
                DiffLineType.Added => "diff-added",
                DiffLineType.Removed => "diff-removed",
                DiffLineType.Modified => "diff-modified",
                _ => "diff-unchanged"
            };

            var prefix = line.Type switch
            {
                DiffLineType.Added => "+",
                DiffLineType.Removed => "-",
                DiffLineType.Modified => "~",
                _ => " "
            };

            var lineNum = line.Type switch
            {
                DiffLineType.Removed => line.OldPosition?.ToString() ?? "",
                DiffLineType.Added => line.NewPosition?.ToString() ?? "",
                _ => $"{line.OldPosition ?? 0}"
            };

            sb.Append($"<div class=\"diff-line {cssClass}\">");
            sb.Append($"<span class=\"line-num\">{lineNum}</span>");
            sb.Append($"<span class=\"prefix\">{prefix}</span>");
            sb.Append($"<span class=\"content\">{HttpUtility.HtmlEncode(line.Text)}</span>");
            sb.Append("</div>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }
}
