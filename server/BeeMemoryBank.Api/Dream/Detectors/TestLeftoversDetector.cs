using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Small test-named folders on the first two levels: test_leftovers (plan 3.6). The advice only asks for a review,
/// so it applies through a prompt and never deletes anything by itself. A folder with no articles at all is left
/// to <see cref="EmptyFolderDetector"/>, which proposes its deletion at the higher test-name score.
/// </summary>
public sealed class TestLeftoversDetector : IDreamDetector
{
    public string Name => "TestLeftovers";

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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (folder, path) in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(path)) continue;
            if (Depth(path) is < 1 || Depth(path) > t.TestLeftoversMaxDepth) continue;
            if (!DreamDetectorSupport.IsTestFolderName(DreamDetectorSupport.FolderName(path), t)) continue;
            if (folder.SubtreeArticles <= 0 || folder.SubtreeArticles > t.TestLeftoversMaxArticles) continue;

            var summary =
                $"The folder '{path}' is named like a test folder and holds {folder.SubtreeArticles} articles. " +
                "Review it and delete what nobody needs.";

            yield return new DreamAdviceDraft
            {
                Kind = DreamAdviceKind.TestLeftovers,
                Source = DreamAdviceSource.Rule,
                ApplyMode = DreamApplyMode.Prompt,
                Score = DreamScore.Clamp(t.TestLeftoversScore),
                Title = DreamDetectorSupport.Clamp($"Review test folder {path}", t.TitleMaxLength),
                Summary = DreamDetectorSupport.Clamp(summary, t.SummaryMaxLength),
                Action = DreamAction.ReviewFolder(path),
                Evidence =
                [
                    new DreamEvidence(DreamEvidenceType.Count,
                        $"The folder holds {folder.SubtreeArticles} articles.", folder.SubtreeArticles),
                ],
                Objects = [DreamAdviceObject.ForFolder(folder.Id, path, DreamObjectRole.Subject)],
                Preconditions = [DreamPrecondition.FolderExists(path)],
            };
        }
    }

    /// <summary>The tree level of a canonical path: "/test" is 1, "/A/test" is 2.</summary>
    private static int Depth(string path) =>
        path == "/" ? 0 : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
}
