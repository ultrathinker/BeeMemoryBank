using System.Runtime.InteropServices;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Perf-correctness pairing for the embedding vector cache at ArticleService's level (as opposed to
/// <c>EmbeddingVectorCacheTests</c> in Storage.Tests, which drills into
/// <c>EmbeddingVectorCache</c> itself). Pins down the two halves of the fix:
///
/// <list type="number">
/// <item>editing an article's text must NOT touch the cache at all -- <c>ArticleService</c>
/// never sets <c>Article.EmbeddingProjection</c>, so there is nothing for it to invalidate;</item>
/// <item>a write that genuinely changes the stored projection
/// (<c>IArticleRepository.UpdateEmbeddingUnscopedAsync</c>, the background re-embed path)
/// still must invalidate/patch the cache, and a semantic search run afterwards must reflect the new
/// vector, not a stale cached one.</item>
/// </list>
///
/// Distinguishing "cache untouched" from "cache patched incrementally" from "cache fully rebuilt"
/// relies on two signals: <c>EmbeddingVectorCache.RebuildCount</c> (bumped only by an actual
/// SQL re-query) and reference identity of the <c>EmbeddingVectorCache.Snapshot</c> object
/// returned by <c>EmbeddingVectorCache.GetOrRebuild</c> (a NEW object is published on either a
/// rebuild or an incremental patch; the SAME object is returned when nothing at all happened).
/// </summary>
public class EmbeddingVectorCacheInvalidationTests : TestFixture
{
    private const int Dim = 8;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    private static float[] RandomVector(Random random, int dim)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(random.NextDouble() * 2 - 1);
        return v;
    }

    private static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    [Fact]
    public async Task EditingArticleText_DoesNotInvalidateOrTouchTheVectorCache()
    {
        var random = new Random(2026090401);
        var vec = RandomVector(random, Dim);

        var article = await ArticleService.CreateAsync("Doc", "/T", [], "initial content");
        // Simulate the background embedding processor having already embedded this article --
        // ArticleService.CreateAsync itself never sets a projection.
        await ArticleRepo.UpdateEmbeddingUnscopedAsync(article.Id, ToBytes(vec), "v1");

        // Warm the cache with a real search.
        var warmed = await ArticleRepo.SearchByEmbeddingAsync(vec, topK: 5);
        warmed.Should().ContainSingle(a => a.Id == article.Id);

        var snapshotBefore = VectorCache.GetOrRebuild();
        var rebuildsBefore = VectorCache.RebuildCount;

        // An ordinary text edit through the exact path a user/agent save takes. This changes the
        // body, flags EmbeddingPending for later reprocessing, but must never touch
        // embedding_projection -- ArticleService.UpdateAsync writes back the SAME projection bytes
        // that were already stored (see its own comment).
        await ArticleService.UpdateAsync(article.Id, plaintext: "edited content, not yet re-embedded");

        var snapshotAfter = VectorCache.GetOrRebuild();

        ReferenceEquals(snapshotBefore, snapshotAfter).Should().BeTrue(
            "editing an article's text must not invalidate or patch the embedding vector cache at " +
            "all -- only a genuine embedding rewrite (UpdateEmbeddingUnscopedAsync) may do that");
        VectorCache.RebuildCount.Should().Be(rebuildsBefore,
            "a text-only edit must never force a full cache rebuild");

        // The (now stale-relative-to-text, but still correct-relative-to-embedding) vector must
        // still be exactly as searchable as before the edit -- nothing was silently dropped.
        var afterEdit = await ArticleRepo.SearchByEmbeddingAsync(vec, topK: 5);
        afterEdit.Should().ContainSingle(a => a.Id == article.Id);
    }

    [Fact]
    public async Task GenuineEmbeddingRewrite_InvalidatesIncrementally_AndSearchReflectsTheNewVector()
    {
        var random = new Random(2026090402);
        var vecA1 = RandomVector(random, Dim);
        var vecB = RandomVector(random, Dim);

        var articleA = await ArticleService.CreateAsync("A", "/T", [], "content A");
        var articleB = await ArticleService.CreateAsync("B", "/T", [], "content B");
        await ArticleRepo.UpdateEmbeddingUnscopedAsync(articleA.Id, ToBytes(vecA1), "v1");
        await ArticleRepo.UpdateEmbeddingUnscopedAsync(articleB.Id, ToBytes(vecB), "v1");

        // Warm the cache -- querying with A's own vector must surface A on top.
        var warmed = await ArticleRepo.SearchByEmbeddingAsync(vecA1, topK: 1);
        warmed.Should().ContainSingle(a => a.Id == articleA.Id);
        var rebuildsAfterWarm = VectorCache.RebuildCount;

        // Genuine change: the background processor re-embeds article A with a brand-new vector --
        // deliberately the EXACT NEGATION of its old one, not just "some other random vector". With
        // only two candidates, a plain independent random vecA2 gives roughly a coin-flip chance
        // that the STALE vecA1 happens to still rank above vecB purely by chance, which would make
        // this test pass even with the bug reintroduced. Negation makes the two outcomes airtight
        // instead: cosine(vecA1, -vecA1) is exactly -1 (the worst possible score, guaranteed to lose
        // to vecB's ~unrelated score) if the cache is stale, vs. cosine(vecA2, vecA2) = +1 (the best
        // possible score, guaranteed to win) if the patch actually landed.
        var vecA2 = vecA1.Select(x => -x).ToArray();
        await ArticleRepo.UpdateEmbeddingUnscopedAsync(articleA.Id, ToBytes(vecA2), "v2");

        // This must be patched incrementally, not a full corpus re-read.
        VectorCache.RebuildCount.Should().Be(rebuildsAfterWarm,
            "a genuine single-row embedding rewrite must be patched into the cache incrementally, " +
            "not force a full SQL rebuild of the whole corpus");

        // The staleness check that actually matters: search must reflect vecA2 immediately.
        var afterRewrite = await ArticleRepo.SearchByEmbeddingAsync(vecA2, topK: 1);
        afterRewrite.Should().ContainSingle(a => a.Id == articleA.Id,
            "semantic search must reflect the new vector immediately -- a STALE cached vecA1 would " +
            "score exactly -1 (worst possible) against this query, guaranteed to lose to B");
    }

    /// <summary>
    /// The concrete before/after story from the task brief: ~20 people editing constantly means
    /// searches and edits interleave, so this simulates the realistic case -- an edit immediately
    /// followed by a search, repeated -- rather than "10 edits, then 1 search" (which would cost
    /// exactly one rebuild either way, since a rebuild only ever happens lazily on a READ, and
    /// nothing reads in between those 10 edits). Before this fix, every edit's now-removed
    /// <c>InvalidateVectorCache()</c> call meant each of the 10 searches below paid its own full
    /// rebuild (10 rebuilds total for the sequence). After the fix, edits touch nothing, so only the
    /// very first search ever needs to build at all -- the other 9 reuse the same warm snapshot (1
    /// rebuild total).
    /// </summary>
    [Fact]
    public async Task TenInterleavedEditsAndSearches_TriggerExactlyOneRebuild_NotTen()
    {
        var random = new Random(2026090403);
        var vec = RandomVector(random, Dim);

        var article = await ArticleService.CreateAsync("Doc", "/T", [], "content v0");
        await ArticleRepo.UpdateEmbeddingUnscopedAsync(article.Id, ToBytes(vec), "v1");

        VectorCache.RebuildCount.Should().Be(0, "nothing has read the cache yet");

        for (int i = 0; i < 10; i++)
        {
            await ArticleService.UpdateAsync(article.Id, plaintext: $"content edit #{i}");
            var result = await ArticleRepo.SearchByEmbeddingAsync(vec, topK: 5);
            result.Should().ContainSingle(a => a.Id == article.Id);
        }

        VectorCache.RebuildCount.Should().Be(1,
            "10 edits interleaved with 10 searches must cost exactly ONE full corpus rebuild -- the " +
            "first search warms the cache and none of the 10 text edits may invalidate it again");
    }
}
