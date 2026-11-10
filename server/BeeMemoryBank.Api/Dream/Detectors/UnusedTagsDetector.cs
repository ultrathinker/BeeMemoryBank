using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Tags without active articles: one delete_unused_tags advice per run, naming at most
/// <see cref="DreamThresholds.UnusedTagsMaxNames"/> of them (plan 3.6). Every named tag carries a tag_unused
/// precondition, so Apply deletes only a tag that is still unused at that moment.
/// </summary>
public sealed class UnusedTagsDetector : IDreamDetector
{
    public string Name => "UnusedTags";

    public async IAsyncEnumerable<DreamAdviceDraft> DetectAsync(IDreamCorpus corpus, DreamRunContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(ctx);
        var t = ctx.Thresholds;

        var unused = corpus.Tags
            .Where(tag => tag.ArticleCount == 0)
            .OrderBy(tag => tag.Name, StringComparer.Ordinal)
            .ToList();
        if (unused.Count == 0) yield break;

        ct.ThrowIfCancellationRequested();

        var named = unused.Take(Math.Max(0, t.UnusedTagsMaxNames)).ToList();
        if (named.Count == 0) yield break;
        var names = named.Select(tag => tag.Name).ToList();
        var summary = unused.Count > names.Count
            ? $"{unused.Count} tags are used by no article. Deleting them keeps the tag list clean; this advice names the first {names.Count}."
            : $"{unused.Count} tags are used by no article. Deleting them keeps the tag list clean.";

        yield return new DreamAdviceDraft
        {
            Kind = DreamAdviceKind.DeleteUnusedTags,
            Source = DreamAdviceSource.Rule,
            ApplyMode = DreamApplyMode.Code,
            Score = DreamScore.Clamp(t.UnusedTagsScore),
            Title = DreamDetectorSupport.Clamp("Delete unused tags", t.TitleMaxLength),
            Summary = DreamDetectorSupport.Clamp(summary, t.SummaryMaxLength),
            Action = DreamAction.DeleteTags(names),
            Evidence =
            [
                new DreamEvidence(DreamEvidenceType.Count,
                    $"{unused.Count} tags have no active articles.", unused.Count),
            ],
            Objects = named.Select(tag => DreamAdviceObject.ForTag(tag.Name, tag.ArticleCount)).ToList(),
            Preconditions = names.Select(DreamPrecondition.TagUnused).ToList(),
        };
    }
}
