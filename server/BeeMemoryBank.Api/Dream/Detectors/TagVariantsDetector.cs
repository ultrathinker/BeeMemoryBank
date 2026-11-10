using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Tags whose normalized names match: merge_tags into the most used (plan 3.6). One advice per group of two or
/// more tags; the target is the tag with the most active articles, ties broken by the ordinal name so a run
/// always proposes the same merge.
/// </summary>
public sealed class TagVariantsDetector : IDreamDetector
{
    public string Name => "TagVariants";

    public async IAsyncEnumerable<DreamAdviceDraft> DetectAsync(IDreamCorpus corpus, DreamRunContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(ctx);
        var t = ctx.Thresholds;

        var groups = new Dictionary<string, List<DreamTag>>(StringComparer.Ordinal);
        foreach (var tag in corpus.Tags)
        {
            var key = DreamDetectorSupport.NormalizeTagName(tag.Name, t);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
            group.Add(tag);
        }

        foreach (var group in groups.Values
                     .Where(g => g.Count > 1)
                     .OrderBy(
                         g => g.Select(tag => tag.Name).OrderBy(name => name, StringComparer.Ordinal).First(),
                         StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            // Most used first; the ordinal name makes a tie deterministic. The rest become the sources.
            var ordered = group
                .OrderByDescending(tag => tag.ArticleCount)
                .ThenBy(tag => tag.Name, StringComparer.Ordinal)
                .ToList();
            var target = ordered[0];
            var sources = ordered.Skip(1).Select(tag => tag.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();

            var spelled = group.OrderBy(tag => tag.Name, StringComparer.Ordinal)
                .Select(tag => $"'{tag.Name}' ({tag.ArticleCount})");
            var summary = DreamDetectorSupport.Clamp(
                $"{group.Count} tags are one tag spelled differently: {string.Join(", ", spelled)}. " +
                $"Merging them into '{target.Name}' keeps their articles under one name.",
                t.SummaryMaxLength);

            var objects = new List<DreamAdviceObject>
            {
                DreamAdviceObject.ForTag(target.Name, target.ArticleCount) with { Role = DreamObjectRole.Target },
            };
            objects.AddRange(ordered.Skip(1).OrderBy(tag => tag.Name, StringComparer.Ordinal)
                .Select(tag => DreamAdviceObject.ForTag(tag.Name, tag.ArticleCount) with { Role = DreamObjectRole.Other }));

            var preconditions = ordered.Select(tag => DreamPrecondition.TagExists(tag.Name)).ToList();

            yield return new DreamAdviceDraft
            {
                Kind = DreamAdviceKind.MergeTags,
                Source = DreamAdviceSource.Rule,
                ApplyMode = DreamApplyMode.Code,
                Score = DreamScore.Clamp(t.TagVariantsScore),
                Title = DreamDetectorSupport.Clamp($"Merge tag variants into '{target.Name}'", t.TitleMaxLength),
                Summary = summary,
                Action = DreamAction.MergeTags(sources, target.Name),
                Evidence =
                [
                    new DreamEvidence(DreamEvidenceType.Count,
                        $"{group.Count} tags share one normalized name.", group.Count),
                    new DreamEvidence(DreamEvidenceType.Note,
                        "Every article keeps its tags; only the spelling under which they are grouped changes."),
                ],
                Objects = objects,
                Preconditions = preconditions,
            };
        }
    }
}
