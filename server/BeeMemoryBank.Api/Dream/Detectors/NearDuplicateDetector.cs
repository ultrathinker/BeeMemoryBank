using System.Globalization;
using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Duplicate candidates that are not exact, only without the model: near_duplicate (plan 3.6). Runs only when
/// <see cref="DreamRunContext.UseModel"/> is false; with the model on, the pair_verdict task judges these pairs
/// instead. A candidate is found by <see cref="IDreamCorpus.CandidatesOf"/> (similarity at least
/// <see cref="DreamThresholds.DuplicateCandidateMinSimilarity"/> or Z at least
/// <see cref="DreamThresholds.DuplicateCandidateMinZ"/>), never by rank; the exact pairs are left to
/// <see cref="ExactDuplicateDetector"/>. Score is clamp(round(100 * similarity) - 25, 50, 75).
/// </summary>
public sealed class NearDuplicateDetector : IDreamDetector
{
    public string Name => "NearDuplicate";

    public async IAsyncEnumerable<DreamAdviceDraft> DetectAsync(IDreamCorpus corpus, DreamRunContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.UseModel) yield break;
        var t = ctx.Thresholds;

        var byId = corpus.Articles.ToDictionary(article => article.Id);
        var digests = new Dictionary<Guid, string?>();
        var seen = new HashSet<(Guid Low, Guid High)>();

        // A body is folded and hashed once; only the digest stays in memory.
        async Task<string?> DigestAsync(Guid id)
        {
            if (digests.TryGetValue(id, out var known)) return known;
            var body = await corpus.ReadBodyAsync(id, ct);
            var digest = body is null
                ? null
                : DreamDetectorSupport.Hash(DreamDetectorSupport.FoldWhiteSpace(body));
            digests[id] = digest;
            return digest;
        }

        async Task<bool> SameTextAsync(Guid firstId, Guid secondId)
        {
            var first = await DigestAsync(firstId);
            var second = await DigestAsync(secondId);
            return first is not null && second is not null
                && string.Equals(first, second, StringComparison.Ordinal);
        }

        foreach (var article in corpus.Articles)
        {
            foreach (var neighbour in corpus.CandidatesOf(
                         article.Id, t.DuplicateCandidateMinSimilarity, t.DuplicateCandidateMinZ))
            {
                ct.ThrowIfCancellationRequested();
                if (article.Id == neighbour.Id) continue;
                if (!byId.TryGetValue(neighbour.Id, out var other)) continue;
                var key = Canonical(article.Id, other.Id);
                if (!seen.Add(key)) continue;

                // An exact pair belongs to ExactDuplicateDetector; the cheap test saves hashing two bodies for
                // pairs that cannot be exact at all.
                var sameTitle = string.Equals(
                    DreamDetectorSupport.FoldWhiteSpace(article.Title),
                    DreamDetectorSupport.FoldWhiteSpace(other.Title),
                    StringComparison.Ordinal);
                if ((sameTitle || neighbour.Similarity >= t.ExactDuplicateMinSimilarity)
                    && await SameTextAsync(article.Id, other.Id))
                {
                    continue;
                }

                var (lowId, highId) = key;
                var first = byId[lowId];
                var second = byId[highId];
                var shown = (neighbour.Similarity * 100).ToString("0", CultureInfo.InvariantCulture);
                var summary = DreamDetectorSupport.Clamp(
                    $"The articles '{first.Title}' and '{second.Title}' are {shown}% similar but are not exact copies. " +
                    "Review both and keep one if they say the same.",
                    t.SummaryMaxLength);

                yield return new DreamAdviceDraft
                {
                    Kind = DreamAdviceKind.NearDuplicate,
                    Source = DreamAdviceSource.Rule,
                    ApplyMode = DreamApplyMode.Chat,
                    Score = DreamScore.NearDuplicate(neighbour.Similarity, t),
                    Title = DreamDetectorSupport.Clamp($"Near duplicate: {first.Title}", t.TitleMaxLength),
                    Summary = summary,
                    Action = DreamAction.ReviewPair(lowId, highId),
                    Evidence =
                    [
                        new DreamEvidence(DreamEvidenceType.Similarity,
                            $"Their similarity is {shown}%.", neighbour.Similarity),
                        new DreamEvidence(DreamEvidenceType.Note,
                            "The model is off, so this pair is offered for a person to review."),
                    ],
                    Objects =
                    [
                        DreamAdviceObject.ForArticle(first, DreamObjectRole.Subject),
                        DreamAdviceObject.ForArticle(second, DreamObjectRole.Other),
                    ],
                    Preconditions =
                    [
                        DreamPrecondition.ArticleUnchanged(first.Id, first.LamportTs),
                        DreamPrecondition.ArticleUnchanged(second.Id, second.LamportTs),
                    ],
                    FamilyKey = DreamNames.PairFamilyKey(first.Id, second.Id),
                };
            }
        }
    }

    private static (Guid Low, Guid High) Canonical(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString("D"), b.ToString("D")) <= 0 ? (a, b) : (b, a);
}
