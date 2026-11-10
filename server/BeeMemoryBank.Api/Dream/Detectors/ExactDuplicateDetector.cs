using System.Globalization;
using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Pairs with the same normalized text: duplicate_exact (plan 3.6). A pair is a candidate when its normalized titles
/// are equal or its similarity is at least <see cref="DreamThresholds.ExactDuplicateMinSimilarity"/>; it is an
/// advice only when the digest of the normalized bodies (lower case, folded white space) is also equal. Each body
/// is read and hashed once and only its digest is kept; same-title groups are bucketed by that digest, so a large
/// group of equal titles compares only articles that really share the text. Different-title pairs are found
/// through <see cref="IDreamCorpus.CandidatesOf"/>, with no rank cut. The copy with fewer references is proposed
/// for the trash, at equal references the later one.
/// </summary>
public sealed class ExactDuplicateDetector : IDreamDetector
{
    public string Name => "ExactDuplicate";

    public async IAsyncEnumerable<DreamAdviceDraft> DetectAsync(IDreamCorpus corpus, DreamRunContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(ctx);
        var t = ctx.Thresholds;
        var byId = corpus.Articles.ToDictionary(article => article.Id);
        var digests = new Dictionary<Guid, string?>();
        var references = new Dictionary<Guid, int>();
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

        async Task<int> ReferencesAsync(Guid id)
        {
            if (references.TryGetValue(id, out var known)) return known;
            var count = await corpus.CountReferencesAsync(id, ct);
            references[id] = count;
            return count;
        }

        async Task<DreamAdviceDraft> BuildAsync(DreamArticle first, DreamArticle second, double? similarity)
        {
            var (trash, keep, trashReferences) = ChooseCopy(first, second,
                await ReferencesAsync(first.Id), await ReferencesAsync(second.Id));

            var checkedReferences = trashReferences >= 0;
            var applyMode = trashReferences == 0 ? DreamApplyMode.Code : DreamApplyMode.Chat;
            var why = trashReferences switch
            {
                0 => "No other article names it.",
                > 0 => $"{trashReferences} other articles name it, so the copy is handed to the chat.",
                _ => "The reference check is unavailable, so the copy is handed to the chat.",
            };

            var evidence = new List<DreamEvidence>
            {
                new(DreamEvidenceType.Note, "The normalized texts of the two articles are identical."),
            };
            if (similarity is { } value)
            {
                evidence.Add(new DreamEvidence(DreamEvidenceType.Similarity,
                    $"Their similarity is {(value * 100).ToString("0", CultureInfo.InvariantCulture)}%.", value));
            }
            if (checkedReferences)
            {
                evidence.Add(new DreamEvidence(DreamEvidenceType.Count, why, trashReferences));
            }
            else
            {
                evidence.Add(new DreamEvidence(DreamEvidenceType.Note, why));
            }

            var summary = DreamDetectorSupport.Clamp(
                $"These two articles have the same text after case and white space are folded. " +
                $"'{trash.Title}' is proposed for the trash and can be restored for 30 days. {why}",
                t.SummaryMaxLength);

            return new DreamAdviceDraft
            {
                Kind = DreamAdviceKind.DuplicateExact,
                Source = DreamAdviceSource.Rule,
                ApplyMode = applyMode,
                Score = DreamScore.Clamp(t.ExactDuplicateScore),
                Title = DreamDetectorSupport.Clamp($"Exact duplicate: {keep.Title}", t.TitleMaxLength),
                Summary = summary,
                Action = DreamAction.TrashArticle(trash.Id, keep.Id),
                Evidence = evidence,
                Objects =
                [
                    DreamAdviceObject.ForArticle(trash, DreamObjectRole.Subject),
                    DreamAdviceObject.ForArticle(keep, DreamObjectRole.Other),
                ],
                Preconditions =
                [
                    DreamPrecondition.ArticleUnchanged(trash.Id, trash.LamportTs),
                    DreamPrecondition.ArticleUnchanged(keep.Id, keep.LamportTs),
                ],
                FamilyKey = DreamNames.PairFamilyKey(first.Id, second.Id),
                References = new DreamReferences(checkedReferences, Math.Max(0, trashReferences), []),
            };
        }

        // Same normalized title: bucket by the digest of the body, then pair only inside a bucket. A title group of
        // k articles costs k body reads and k digests, not k*(k-1)/2 folds and hashes.
        foreach (var group in corpus.Articles
                     .GroupBy(article => DreamDetectorSupport.FoldWhiteSpace(article.Title), StringComparer.Ordinal))
        {
            var articles = group.OrderBy(article => article.Id.ToString("D"), StringComparer.Ordinal).ToList();
            if (articles.Count < 2) continue;

            var buckets = new Dictionary<string, List<DreamArticle>>(StringComparer.Ordinal);
            foreach (var article in articles)
            {
                var digest = await DigestAsync(article.Id);
                if (digest is null) continue;
                if (!buckets.TryGetValue(digest, out var bucket)) buckets[digest] = bucket = [];
                bucket.Add(article);
            }

            foreach (var bucket in buckets.Values
                         .Where(candidates => candidates.Count > 1)
                         .OrderBy(
                             candidates => candidates.Select(article => article.Id.ToString("D"))
                                 .OrderBy(id => id, StringComparer.Ordinal).First(),
                             StringComparer.Ordinal))
            {
                for (var i = 0; i < bucket.Count; i++)
                {
                    for (var j = i + 1; j < bucket.Count; j++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!seen.Add(Canonical(bucket[i].Id, bucket[j].Id))) continue;
                        yield return await BuildAsync(bucket[i], bucket[j], null);
                    }
                }
            }
        }

        // Different titles: every similarity candidate, no rank cut. The digests decide which candidates are exact.
        foreach (var article in corpus.Articles)
        {
            foreach (var neighbour in corpus.CandidatesOf(
                         article.Id, t.ExactDuplicateMinSimilarity, double.PositiveInfinity))
            {
                ct.ThrowIfCancellationRequested();
                if (neighbour.Similarity < t.ExactDuplicateMinSimilarity) continue;
                if (!byId.TryGetValue(neighbour.Id, out var other)) continue;
                if (!seen.Add(Canonical(article.Id, other.Id))) continue;

                var firstDigest = await DigestAsync(article.Id);
                var secondDigest = await DigestAsync(other.Id);
                if (firstDigest is null || secondDigest is null
                    || !string.Equals(firstDigest, secondDigest, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return await BuildAsync(article, other, neighbour.Similarity);
            }
        }
    }

    private static (DreamArticle Trash, DreamArticle Keep, int TrashReferences) ChooseCopy(
        DreamArticle first, DreamArticle second, int firstReferences, int secondReferences)
    {
        DreamArticle trash;
        if (firstReferences >= 0 && secondReferences >= 0 && firstReferences != secondReferences)
        {
            trash = firstReferences < secondReferences ? first : second;
        }
        else
        {
            trash = Later(first, second);
        }
        var keep = trash.Id == first.Id ? second : first;
        return (trash, keep, trash.Id == first.Id ? firstReferences : secondReferences);
    }

    /// <summary>The later of two articles; the id settles a pair created at the same instant.</summary>
    private static DreamArticle Later(DreamArticle first, DreamArticle second)
    {
        if (first.CreatedAt != second.CreatedAt) return first.CreatedAt > second.CreatedAt ? first : second;
        if (first.UpdatedAt != second.UpdatedAt) return first.UpdatedAt > second.UpdatedAt ? first : second;
        return string.CompareOrdinal(first.Id.ToString("D"), second.Id.ToString("D")) >= 0 ? first : second;
    }

    private static (Guid Low, Guid High) Canonical(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString("D"), b.ToString("D")) <= 0 ? (a, b) : (b, a);
}
