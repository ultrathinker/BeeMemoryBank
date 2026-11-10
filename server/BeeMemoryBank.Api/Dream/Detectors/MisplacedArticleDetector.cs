using System.Globalization;
using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Articles whose strong neighbours sit in another folder: move_article (plan 3.6). An article needs at least
/// <see cref="DreamThresholds.MisplacedMinStrongNeighbours"/> strong neighbours (in the nearest
/// <see cref="DreamThresholds.StrongNeighbourTopK"/> with Z at least
/// <see cref="DreamThresholds.StrongNeighbourMinZ"/>), at least the
/// <see cref="DreamThresholds.MisplacedMinShare"/> share of them in one folder G, at most
/// <see cref="DreamThresholds.MisplacedMaxOwnShare"/> in its own folder, G must not be an ancestor of its own
/// folder or a system folder, and the article must not have changed for
/// <see cref="DreamThresholds.MisplacedQuietDays"/> days. Score is round(40 + 50 * share).
/// </summary>
public sealed class MisplacedArticleDetector : IDreamDetector
{
    public string Name => "MisplacedArticle";

    public async IAsyncEnumerable<DreamAdviceDraft> DetectAsync(IDreamCorpus corpus, DreamRunContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(ctx);
        var t = ctx.Thresholds;

        var byId = corpus.Articles.ToDictionary(article => article.Id);
        var folders = corpus.Folders
            .GroupBy(folder => DreamDetectorSupport.CanonicalPath(folder.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var quietSince = ctx.Clock.GetUtcNow().UtcDateTime.AddDays(-t.MisplacedQuietDays);

        foreach (var article in corpus.Articles.OrderBy(a => a.Id.ToString("D"), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (article.UpdatedAt > quietSince) continue;

            var strong = corpus.NeighboursOf(article.Id, t.StrongNeighbourTopK)
                .Where(neighbour => neighbour.Z >= t.StrongNeighbourMinZ && byId.ContainsKey(neighbour.Id))
                .ToList();
            if (strong.Count < t.MisplacedMinStrongNeighbours) continue;

            var ownPath = DreamDetectorSupport.CanonicalPath(article.TreePath);
            var ownNeighbours = strong.Count(neighbour =>
                DreamDetectorSupport.CanonicalPath(byId[neighbour.Id].TreePath) == ownPath);
            if ((double)ownNeighbours / strong.Count > t.MisplacedMaxOwnShare) continue;

            var target = strong
                .Select(neighbour => DreamDetectorSupport.CanonicalPath(byId[neighbour.Id].TreePath))
                .Where(path => path != ownPath && !DreamDetectorSupport.IsAncestor(path, ownPath))
                .GroupBy(path => path, StringComparer.Ordinal)
                .Select(group => new TargetFolder(group.Key, group.Count(), (double)group.Count() / strong.Count))
                .Where(candidate => candidate.Share >= t.MisplacedMinShare)
                .Where(candidate => folders.TryGetValue(candidate.Path, out var folder) && !folder.IsSystem)
                .OrderByDescending(candidate => candidate.Share)
                .ThenByDescending(candidate => candidate.Count)
                .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
                .FirstOrDefault();
            if (target is null) continue;

            var targetFolder = folders[target.Path];
            var shown = (target.Share * 100).ToString("0", CultureInfo.InvariantCulture);
            var ownShown = (ownNeighbours * 100.0 / strong.Count).ToString("0", CultureInfo.InvariantCulture);
            var objects = new List<DreamAdviceObject>
            {
                DreamAdviceObject.ForArticle(article, DreamObjectRole.Subject),
            };
            objects.Add(folders.TryGetValue(ownPath, out var ownFolder)
                ? DreamAdviceObject.ForFolder(ownFolder.Id, ownPath, DreamObjectRole.Other)
                : DreamAdviceObject.ForFolder(null, ownPath, DreamObjectRole.Other));
            objects.Add(DreamAdviceObject.ForFolder(targetFolder.Id, target.Path, DreamObjectRole.Target));

            yield return new DreamAdviceDraft
            {
                Kind = DreamAdviceKind.MoveArticle,
                Source = DreamAdviceSource.Rule,
                ApplyMode = DreamApplyMode.Code,
                Score = DreamScore.Misplaced(target.Share, t),
                Title = DreamDetectorSupport.Clamp($"Move '{article.Title}' to {target.Path}", t.TitleMaxLength),
                Summary = DreamDetectorSupport.Clamp(
                    $"{target.Count} of its {strong.Count} strong neighbours ({shown}%) are in '{target.Path}', " +
                    $"and only {ownShown}% are in its own folder '{ownPath}'. " +
                    $"The article has not changed in the last {t.MisplacedQuietDays} days.",
                    t.SummaryMaxLength),
                Action = DreamAction.MoveArticle(article.Id, target.Path),
                Evidence =
                [
                    new DreamEvidence(DreamEvidenceType.Neighbours,
                        $"{target.Count} of {strong.Count} strong neighbours are in '{target.Path}'.",
                        target.Share),
                    new DreamEvidence(DreamEvidenceType.Count,
                        $"{ownNeighbours} of {strong.Count} strong neighbours are in its own folder '{ownPath}'.",
                        ownNeighbours),
                    new DreamEvidence(DreamEvidenceType.Note,
                        $"The article has not changed in the last {t.MisplacedQuietDays} days."),
                ],
                Objects = objects,
                Preconditions =
                [
                    DreamPrecondition.ArticleUnchanged(article.Id, article.LamportTs),
                    DreamPrecondition.ArticleInPath(article.Id, ownPath),
                    DreamPrecondition.FolderExists(target.Path),
                ],
            };
        }
    }

    private sealed record TargetFolder(string Path, int Count, double Share);
}
