using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Folders with no articles and no subfolders: delete_empty_folder (plan 3.6). A system folder is never proposed,
/// and the root is not a folder one can delete. An empty folder whose name looks like a test folder scores
/// <see cref="DreamThresholds.EmptyTestFolderScore"/> instead of <see cref="DreamThresholds.EmptyFolderScore"/>.
/// </summary>
public sealed class EmptyFolderDetector : IDreamDetector
{
    public string Name => "EmptyFolder";

    public async IAsyncEnumerable<DreamAdviceDraft> DetectAsync(IDreamCorpus corpus, DreamRunContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(ctx);
        var t = ctx.Thresholds;

        var folders = corpus.Folders
            .Select(folder => (Folder: folder, Path: DreamDetectorSupport.CanonicalPath(folder.Path)))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToList();

        // A folder has a subfolder when some folder names it as its parent. The set is built once, so this stays
        // linear in the number of folders instead of comparing every pair.
        var parents = folders
            .Select(item => ParentOf(item.Path))
            .Where(parent => parent != null)
            .ToHashSet(StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (folder, path) in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (path == "/" || folder.IsSystem) continue;
            if (!seen.Add(path)) continue;
            if (folder.DirectArticles != 0 || folder.SubtreeArticles != 0) continue;
            if (parents.Contains(path)) continue;

            var testNamed = DreamDetectorSupport.IsTestFolderName(DreamDetectorSupport.FolderName(path), t);
            var score = testNamed ? t.EmptyTestFolderScore : t.EmptyFolderScore;
            var summary = testNamed
                ? $"The folder '{path}' holds no articles and no subfolders, and its name looks like a test folder. Deleting it removes an empty place in the tree."
                : $"The folder '{path}' holds no articles and no subfolders. Deleting it removes an empty place in the tree.";

            yield return new DreamAdviceDraft
            {
                Kind = DreamAdviceKind.DeleteEmptyFolder,
                Source = DreamAdviceSource.Rule,
                ApplyMode = DreamApplyMode.Code,
                Score = DreamScore.Clamp(score),
                Title = DreamDetectorSupport.Clamp($"Delete empty folder {path}", t.TitleMaxLength),
                Summary = DreamDetectorSupport.Clamp(summary, t.SummaryMaxLength),
                Action = DreamAction.DeleteFolder(path, folder.Id),
                Evidence =
                [
                    new DreamEvidence(DreamEvidenceType.Count,
                        "The folder holds no articles and no subfolders.", 0),
                ],
                Objects = [DreamAdviceObject.ForFolder(folder.Id, path, DreamObjectRole.Subject)],
                Preconditions =
                [
                    DreamPrecondition.FolderExists(path),
                    DreamPrecondition.FolderEmpty(path),
                ],
            };
        }
    }

    /// <summary>The parent of a canonical tree path; null for the root.</summary>
    private static string? ParentOf(string path)
    {
        if (path == "/") return null;
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }
}
