using System.Runtime.InteropServices;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The related-articles list against a real database: tags weighed by rarity (a tag on much of the vault
/// links nothing), nearness by meaning from the article vectors, the caller's folder access applied to both
/// parts before the list is cut, protected articles, the limit, and the cheap tags-only summary that
/// bee_get_article reports. Articles are inserted with plain SQL where a test needs many of them or needs a
/// chosen vector; the vector cache is told about it with <c>Invalidate()</c>.
/// </summary>
public class RelatedArticlesTests : TestFixture
{
    private const int Dim = 64;

    private ConceptTagRepository _repo = null!;
    private ConceptTagService _service = null!;
    private Random _random = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _repo = new ConceptTagRepository(Factory, ScopeHolder, VectorCache);
        _service = new ConceptTagService(_repo, new FakeEmbeddingGenerator(), new NullEventLogger());
        _random = new Random(20261110);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private async Task<Guid> AddAsync(string title, string[]? tags = null, float[]? vector = null, string path = "/",
        bool isProtected = false, string status = "A")
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        byte[]? bytes = isProtected ? [] : vector == null ? null : MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
        using (var conn = Factory.CreateConnection())
        {
            string? folderId = null;
            if (path != "/")
            {
                folderId = await conn.ExecuteScalarAsync<string?>("SELECT id FROM tbl_folder WHERE path = @path", new { path });
                if (folderId == null)
                {
                    folderId = Guid.NewGuid().ToString().ToUpperInvariant();
                    await conn.ExecuteAsync(
                        @"INSERT INTO tbl_folder (id, path, name, parent_path, status, created_at, updated_at)
                          VALUES (@folderId, @path, @name, '/', 'A', @now, @now)",
                        new { folderId, path, name = path.TrimStart('/'), now });
                }
            }
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_article (id, title, tree_path, folder_id, status, created_at, updated_at, embedding_projection, protected)
                  VALUES (@id, @title, @path, @folderId, @status, @now, @now, @bytes, @isProtected)",
                new { id, title, path, folderId, status, now, bytes, isProtected = isProtected ? 1 : 0 });
        }
        if (tags is { Length: > 0 })
            await _repo.SetForArticleAsync(id, tags.ToList());
        VectorCache.Invalidate();
        return id;
    }

    private async Task AddFillersAsync(int count, string path = "/")
    {
        for (int i = 0; i < count; i++)
            await AddAsync($"filler {Guid.NewGuid():N}", vector: RandomVector(), path: path);
    }

    private float[] RandomVector()
    {
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++) v[i] = (float)(_random.NextDouble() * 2 - 1);
        return v;
    }

    /// <summary>A vector near <paramref name="basis"/>: the basis plus noise of the given relative size (more noise, less similar).</summary>
    private float[] Around(float[] basis, double noise)
    {
        var v = RandomVector();
        var result = new float[Dim];
        for (int i = 0; i < Dim; i++) result[i] = (float)(basis[i] + noise * v[i]);
        return result;
    }

    private static HttpCallerScope Restricted(params string[] denied) =>
        new(false, new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    // ---- tags: rarity ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATagCarriedByHalfTheVaultLinksNothing()
    {
        // 60 articles: 30 carry "common" (more than max(20, 5% of 60)), two of them also share "rare-pair".
        var subject = await AddAsync("Subject", ["common", "rare-pair"]);
        var partner = await AddAsync("Partner", ["common", "rare-pair"]);
        for (int i = 0; i < 28; i++) await AddAsync($"Common {i}", ["common"]);
        for (int i = 0; i < 30; i++) await AddAsync($"Untagged {i}");

        var related = await _repo.GetRelatedArticlesAsync(subject);

        related.Should().ContainSingle();
        related[0].Id.Should().Be(partner);
        related[0].SharedConcepts.Should().Equal("rare-pair");
        related[0].Reasons.Should().Equal("shared tag: rare-pair");
        related[0].Strength.Should().Be(60, "tags only: the best (here the only) row scores 0.6");
    }

    [Fact]
    public async Task AVaultWhereOnlyACommonplaceTagIsSharedHasNoRelatedArticles()
    {
        var subject = await AddAsync("Subject", ["common"]);
        for (int i = 0; i < 40; i++) await AddAsync($"Common {i}", ["common"]);
        for (int i = 0; i < 20; i++) await AddAsync($"Untagged {i}");

        (await _repo.GetRelatedArticlesAsync(subject)).Should().BeEmpty();
        (await _repo.GetRelatedTagSummaryAsync(subject)).Should().Be((0, 0));
    }

    [Fact]
    public async Task ArticlesSharingMoreOfTheRarerTagsRankHigher()
    {
        var subject = await AddAsync("Subject", ["alpha", "beta", "wide"]);
        var both = await AddAsync("Shares two rare", ["alpha", "beta"]);
        var wideOnly = await AddAsync("Shares the wide one", ["wide"]);
        for (int i = 0; i < 8; i++) await AddAsync($"Wide {i}", ["wide"]);     // "wide" on 10 articles: allowed, but not rare
        await AddFillersAsync(40);

        var related = await _repo.GetRelatedArticlesAsync(subject);

        related.Select(r => r.Id).First().Should().Be(both);
        related[0].SharedConcepts.Should().BeEquivalentTo("alpha", "beta");
        related[0].Strength.Should().Be(60);
        related.Single(r => r.Id == wideOnly).Strength.Should().BeLessThan(related[0].Strength);
        related.Select(r => r.Strength).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task ADeletedArticleIsNotListed()
    {
        var subject = await AddAsync("Subject", ["rare"]);
        await AddAsync("Gone", ["rare"], status: "D");
        var alive = await AddAsync("Alive", ["rare"]);
        await AddFillersAsync(10);

        (await _repo.GetRelatedArticlesAsync(subject)).Select(r => r.Id).Should().Equal(alive);
    }

    // ---- meaning ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnArticleCloseByMeaningIsListedWithoutAnySharedTag()
    {
        var basis = RandomVector();
        var subject = await AddAsync("Subject", vector: basis);
        var twin = await AddAsync("Twin", vector: Around(basis, 0.3));
        await AddFillersAsync(60);

        var related = await _repo.GetRelatedArticlesAsync(subject);

        related.Should().NotBeEmpty();
        related[0].Id.Should().Be(twin);
        related[0].SharedConcepts.Should().BeEmpty();
        related[0].SemanticPercent.Should().BeInRange(80, 100);
        related[0].Reasons.Should().Contain(r => r.StartsWith("similar meaning: "));
        related[0].Strength.Should().Be(40, "meaning only: the best row scores 0.4");
    }

    [Fact]
    public async Task ANearlyIdenticalArticleIsFlaggedAsAPossibleDuplicate()
    {
        var basis = RandomVector();
        var subject = await AddAsync("Subject", vector: basis);
        var copy = await AddAsync("Copy", vector: Around(basis, 0.03));
        await AddFillersAsync(60);

        var related = await _repo.GetRelatedArticlesAsync(subject);

        related[0].Id.Should().Be(copy);
        related[0].PossibleDuplicate.Should().BeTrue();
        related[0].Reasons.Should().Contain("possible duplicate");
    }

    [Fact]
    public async Task SharedTagsAndMeaningTogetherOutrankEitherAlone()
    {
        var basis = RandomVector();
        var subject = await AddAsync("Subject", ["topic-x"], basis);
        var both = await AddAsync("Both", ["topic-x"], Around(basis, 0.2));
        var tagOnly = await AddAsync("Tag only", ["topic-x"], RandomVector());
        var meaningOnly = await AddAsync("Meaning only", vector: Around(basis, 0.3));
        await AddFillersAsync(60);

        var related = await _repo.GetRelatedArticlesAsync(subject);

        related.Select(r => r.Id).Take(3).Should().Equal(both, tagOnly, meaningOnly);
        related[0].Strength.Should().Be(100);
        related[1].Strength.Should().Be(60);
        related[2].Strength.Should().BeInRange(30, 40, "meaning only: close to the 40 of the closest row");
    }

    [Fact]
    public async Task AProtectedArticleIsNeverAMeaningNeighbour_AndHasNoMeaningNeighboursItself()
    {
        var basis = RandomVector();
        var subject = await AddAsync("Subject", vector: basis);
        // Even when the protected article would otherwise be the nearest one, it has no vector to compare.
        var protectedSibling = await AddAsync("Protected", isProtected: true);
        var twin = await AddAsync("Twin", vector: Around(basis, 0.3));
        await AddFillersAsync(60);

        var related = await _repo.GetRelatedArticlesAsync(subject);
        related.Select(r => r.Id).Should().Contain(twin).And.NotContain(protectedSibling);

        // Seen from the protected article itself: no vector, so only tags can relate anything.
        var protectedWithTag = await AddAsync("Protected tagged", ["shared-rare"], isProtected: true);
        var taggedPartner = await AddAsync("Partner", ["shared-rare"], RandomVector());
        var fromProtected = await _repo.GetRelatedArticlesAsync(protectedWithTag);
        fromProtected.Should().ContainSingle();
        fromProtected[0].Id.Should().Be(taggedPartner);
        fromProtected[0].SemanticPercent.Should().BeNull();
    }

    [Fact]
    public async Task WithoutTheMeaningPartTheListIsTagsOnly_AndTheVectorSnapshotIsNotTouched()
    {
        var basis = RandomVector();
        var subject = await AddAsync("Subject", ["topic-q"], basis);
        var tagged = await AddAsync("Tagged", ["topic-q"], RandomVector());
        var twin = await AddAsync("Twin", vector: Around(basis, 0.05));
        await AddFillersAsync(60);

        var rebuilds = VectorCache.RebuildCount;
        var tagsOnly = await _repo.GetRelatedArticlesAsync(subject, includeMeaning: false);

        tagsOnly.Select(r => r.Id).Should().Equal(tagged);
        tagsOnly[0].SemanticPercent.Should().BeNull();
        tagsOnly[0].Strength.Should().Be(60);
        VectorCache.RebuildCount.Should().Be(rebuilds, "the vectors are not read at all");

        var full = await _repo.GetRelatedArticlesAsync(subject);
        full.Select(r => r.Id).Should().Contain([tagged, twin]);
        VectorCache.RebuildCount.Should().BeGreaterThan(rebuilds);
        (await _service.GetRelatedArticlesAsync(subject, 7, includeMeaning: false)).Select(r => r.Id).Should().Equal(tagged);
    }

    [Fact]
    public async Task WithoutAnyVectorsTheListFallsBackToTags()
    {
        var subject = await AddAsync("Subject", ["only-tags"]);
        var partner = await AddAsync("Partner", ["only-tags"]);
        for (int i = 0; i < 10; i++) await AddAsync($"Plain {i}");   // no projections at all

        var related = await _repo.GetRelatedArticlesAsync(subject);

        related.Select(r => r.Id).Should().Equal(partner);
    }

    // ---- access ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFolderTheCallerMayNotSeeGivesNeitherTagNorMeaningRelations()
    {
        var basis = RandomVector();
        var subject = await AddAsync("Subject", ["topic-y"], basis, path: "/Public");
        var publicTag = await AddAsync("Public tag", ["topic-y"], RandomVector(), path: "/Public");
        var secretTag = await AddAsync("Secret tag", ["topic-y"], RandomVector(), path: "/Secret");
        var secretTwin = await AddAsync("Secret twin", vector: Around(basis, 0.1), path: "/Secret");
        var publicTwin = await AddAsync("Public twin", vector: Around(basis, 0.3), path: "/Public");
        await AddFillersAsync(60, "/Public");

        var asAdmin = await _repo.GetRelatedArticlesAsync(subject);
        asAdmin.Select(r => r.Id).Should().Contain([publicTag, secretTag, secretTwin, publicTwin]);

        ScopeHolder.Scope = Restricted("/Secret");
        var restricted = await _repo.GetRelatedArticlesAsync(subject);

        restricted.Select(r => r.Id).Should().Contain([publicTag, publicTwin]);
        restricted.Select(r => r.Id).Should().NotContain([secretTag, secretTwin]);
        restricted.Select(r => r.Title).Should().NotContain(["Secret tag", "Secret twin"]);
    }

    [Fact]
    public async Task HiddenNeighboursDoNotTakeUpTheSlotsOfTheMeaningPart()
    {
        // 25 hidden articles are nearer than the 3 visible ones: if the list were cut to the 20 nearest BEFORE
        // the caller's access applied, a restricted caller would see none of the visible ones.
        var basis = RandomVector();
        var subject = await AddAsync("Subject", vector: basis, path: "/Public");
        for (int i = 0; i < 25; i++) await AddAsync($"Hidden {i}", vector: Around(basis, 0.2), path: "/Secret");
        var visible = new List<Guid>();
        for (int i = 0; i < 3; i++) visible.Add(await AddAsync($"Visible {i}", vector: Around(basis, 0.35), path: "/Public"));
        // Enough unrelated articles that the 28 near ones are a small cluster and still stand out (Z >= 2).
        await AddFillersAsync(300, "/Public");

        ScopeHolder.Scope = Restricted("/Secret");
        var related = await _repo.GetRelatedArticlesAsync(subject, limit: 20);

        related.Select(r => r.Id).Should().Contain(visible);
        related.Should().OnlyContain(r => r.TreePath != "/Secret");
    }

    [Fact]
    public async Task TheSummaryRespectsAccessToo()
    {
        var subject = await AddAsync("Subject", ["topic-z", "topic-w"], path: "/Public");
        await AddAsync("Public", ["topic-z"], path: "/Public");
        await AddAsync("Secret", ["topic-z", "topic-w"], path: "/Secret");
        await AddFillersAsync(20);

        (await _repo.GetRelatedTagSummaryAsync(subject)).Should().Be((2, 3));

        ScopeHolder.Scope = Restricted("/Secret");
        (await _repo.GetRelatedTagSummaryAsync(subject)).Should().Be((1, 1));
    }

    // ---- limit, summary --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheListHoldsSevenRowsByDefault_AndTheLimitIsClampedToTwenty()
    {
        var tags = Enumerable.Range(1, 25).Select(i => $"link-{i:00}").ToArray();
        var subject = await AddAsync("Subject", tags);
        foreach (var tag in tags) await AddAsync($"Linked by {tag}", [tag]);
        await AddFillersAsync(30);

        (await _repo.GetRelatedArticlesAsync(subject)).Should().HaveCount(7);
        (await _repo.GetRelatedArticlesAsync(subject, 12)).Should().HaveCount(12);
        (await _repo.GetRelatedArticlesAsync(subject, 20)).Should().HaveCount(20);
        (await _repo.GetRelatedArticlesAsync(subject, 500)).Should().HaveCount(20);
        (await _repo.GetRelatedArticlesAsync(subject, 0)).Should().HaveCount(1);
        (await _repo.GetRelatedArticlesAsync(subject, -3)).Should().HaveCount(1);
        (await _service.GetRelatedArticlesAsync(subject)).Should().HaveCount(7);
    }

    [Fact]
    public async Task TheSummaryCountsArticlesAndRareSharedTagsOnly()
    {
        var subject = await AddAsync("Subject", ["common", "rare-1", "rare-2"]);
        await AddAsync("A", ["common", "rare-1", "rare-2"]);
        await AddAsync("B", ["common", "rare-1"]);
        await AddAsync("C", ["common"]);
        for (int i = 0; i < 28; i++) await AddAsync($"Common {i}", ["common"]);
        await AddFillersAsync(30);

        // "common" is on 32 of 63 articles: ignored. A shares two rare tags, B one, C none.
        (await _repo.GetRelatedTagSummaryAsync(subject)).Should().Be((2, 3));
        (await _service.GetRelatedTagSummaryAsync(subject)).Should().Be((2, 3));
    }

    [Fact]
    public async Task TheSummaryNeverReadsVectors()
    {
        // A meaning neighbour would add a row to the list but not to the cheap summary.
        var basis = RandomVector();
        var subject = await AddAsync("Subject", vector: basis);
        await AddAsync("Twin", vector: Around(basis, 0.2));
        await AddFillersAsync(40);

        (await _repo.GetRelatedArticlesAsync(subject)).Should().NotBeEmpty();
        (await _repo.GetRelatedTagSummaryAsync(subject)).Should().Be((0, 0));
    }

    // ---- through the real article service --------------------------------------------------------------------------

    [Fact]
    public async Task ArticlesCreatedThroughTheServiceAreRelatedByTheirTags()
    {
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        var a = await ArticleService.CreateAsync("Rust async", "/Dev", ["rust", "async"], "text");
        var b = await ArticleService.CreateAsync("Tokio", "/Dev", ["rust", "async", "runtime"], "text");
        await ArticleService.CreateAsync("Cooking", "/Food", ["pasta"], "text");
        await ArticleService.CreateAsync("Gardening", "/Garden", ["tomato"], "text");

        var related = await _service.GetRelatedArticlesAsync(a.Id);

        related.Should().ContainSingle();
        related[0].Id.Should().Be(b.Id);
        related[0].TreePath.Should().Be("/Dev");
        related[0].SharedConcepts.Should().BeEquivalentTo("rust", "async");
    }

    // ---- tag list order (bee_list_tags without a filter) -----------------------------------------------------------

    [Fact]
    public async Task ListingTagsWithoutAFilterPutsTheBusiestFirst()
    {
        var one = await AddAsync("One", ["solo", "pair", "trio", "zeta-trio"]);
        await AddAsync("Two", ["pair", "trio", "zeta-trio"]);
        await AddAsync("Three", ["trio", "zeta-trio"]);
        await _repo.SetForArticleAsync(one, ["solo", "pair", "trio", "zeta-trio", "alpha-solo"]);

        var all = await _repo.ListAsync(null, 50);
        all.Select(t => t.Name).Should().Equal("trio", "zeta-trio", "pair", "alpha-solo", "solo");
        all.Select(t => t.ArticleCount).Should().Equal(3, 3, 2, 1, 1);

        // Paging runs over that order, not over the alphabet.
        (await _repo.ListAsync(null, 2, 0)).Select(t => t.Name).Should().Equal("trio", "zeta-trio");
        (await _repo.ListAsync(null, 2, 2)).Select(t => t.Name).Should().Equal("pair", "alpha-solo");

        // A filtered list keeps its alphabetical order.
        (await _repo.ListAsync("o", 50)).Select(t => t.Name).Should().Equal("alpha-solo", "solo", "trio", "zeta-trio");
    }
}
