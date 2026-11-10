using System.Net.Http.Json;
using System.Text.RegularExpressions;

using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Embeddings;
using BeeMemoryBank.TestSupport;

using Microsoft.Extensions.DependencyInjection;

using Xunit.Abstractions;

namespace BeeMemoryBank.Integration.Tests.Dream;

/// <summary>
/// Package X1's own tests: the Dream corpus definition must be internally consistent with the
/// rule formulas of the dream plan (section 3.6), the loader must seed the whole corpus onto a
/// live node, and, where the local embedding model exists, the seeded articles must be picked
/// up by the embedding pipeline, the protected one excepted.
/// </summary>
public class DreamCorpusTests
{
    private const string Canary = "QX9107-PROTECTED-CANARY";

    private readonly ITestOutputHelper _output;

    public DreamCorpusTests(ITestOutputHelper output) => _output = output;

    // Section 3.6 normalizations: tag names fold to lowercase without spaces, '-', '_', '.';
    // title and body comparisons fold case and collapse whitespace.
    private static string NormTag(string s) => Regex.Replace(s.ToLowerInvariant(), "[ \\-_.]", "");
    private static string NormText(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

    [Fact]
    public void Definition_IsInternallyConsistent()
    {
        var corpus = DreamCorpus.Load();
        corpus.Source.Should().StartWith("embedded:", "the integration tests read the embedded copy");

        corpus.Version.Should().Be(1);
        corpus.Articles.Should().HaveCount(120);
        corpus.Folders.Should().HaveCount(21);
        corpus.UnusedTags.Should().HaveCount(5);
        corpus.Expected.Should().HaveCount(18);

        var folders = corpus.Folders.ToHashSet();
        var articles = corpus.Articles.ToList();
        var articleFolders = articles.Select(a => a.Path).ToHashSet();
        foreach (var article in articles)
            folders.Should().Contain(article.Path, $"article '{article.Title}' must name a declared folder");

        // The declared folders are exactly the article folders plus the two planted empty ones.
        var emptyFolders = corpus.Expected
            .Where(e => e.Kind == "delete_empty_folder")
            .SelectMany(e => e.Objects)
            .Select(o => o.Folder!)
            .ToHashSet();
        emptyFolders.Should().HaveCount(2);
        foreach (var empty in emptyFolders)
            articleFolders.Should().NotContain(empty, $"'{empty}' is planted as an empty folder");
        corpus.Folders.Should().HaveCount(articleFolders.Count + emptyFolders.Count);

        // Cluster sizes the move_article planting relies on: the 12-note clusters, the junk-drawer
        // /Inbox above them, and the two junk folders with exactly 3 notes each (at most 30).
        articles.Count(a => a.Path == "/Inbox").Should().Be(25);
        articles.Count(a => a.Path == "/Kids/Puzzles").Should().Be(12);
        articles.Count(a => a.Path == "/Cooking/Sourdough").Should().Be(12);
        articles.Count(a => a.Path == "/test").Should().Be(3);
        articles.Count(a => a.Path == "/Test").Should().Be(3);

        // Exact-duplicate planting: the ONLY normalized-title collisions and the ONLY
        // normalized-body collisions in the whole corpus are the two planted pairs, each
        // byte-identical in title, tags and body so the rule fires embedding-free.
        var titleGroups = articles.GroupBy(a => NormText(a.Title)).ToDictionary(g => g.Key, g => g.ToList());
        var titleCollisions = titleGroups.Where(g => g.Value.Count > 1).ToList();
        titleCollisions.Select(g => g.Key).Should().BeEquivalentTo(["wifi printer setup", "router reboot routine"]);
        titleCollisions.Single(g => g.Key == "wifi printer setup").Value.Select(a => a.Path)
            .Should().BeEquivalentTo(["/Guides", "/Imported"]);
        titleCollisions.Single(g => g.Key == "router reboot routine").Value.Select(a => a.Path)
            .Should().BeEquivalentTo(["/Home Network", "/Maintenance"]);

        int SeededIndex(string folder, string title) => articles.FindIndex(a => a.Path == folder && a.Title == title);
        foreach (var group in titleCollisions)
        {
            var pair = group.Value;
            pair.Select(a => NormText(a.Body)).Distinct().Should().HaveCount(1, "an exact-duplicate pair shares its body verbatim");
            pair[0].Tags.Should().BeEquivalentTo(pair[1].Tags, "an exact-duplicate pair shares its tags");
            SeededIndex(pair[0].Path, pair[0].Title).Should().BeLessThan(SeededIndex(pair[1].Path, pair[1].Title),
                "the original must seed before the copy: with no references the trash rule's tie-break picks the later one");
        }
        articles.GroupBy(a => NormText(a.Body)).Count(g => g.Count() > 1).Should().Be(2,
            "no note body may accidentally duplicate another note's body");

        // Near-duplicate planting: the three pairs differ in title and body, so they must NOT
        // collapse into exact duplicates, only into near_duplicate advices.
        var nearPairs = corpus.Expected.Where(e => e.Kind == "near_duplicate").ToList();
        nearPairs.Should().HaveCount(3);
        foreach (var e in nearPairs)
        {
            var first = articles.Single(a => a.Path == e.Objects[0].Folder && a.Title == e.Objects[0].Title);
            var second = articles.Single(a => a.Path == e.Objects[1].Folder && a.Title == e.Objects[1].Title);
            NormText(first.Title).Should().NotBe(NormText(second.Title), "a near pair must not collapse to exact by title");
            NormText(first.Body).Should().NotBe(NormText(second.Body), "a near pair must not collapse to exact by body hash");
        }

        // Protected planting: one passphrase-protected note carrying the unique marker, and the
        // marker appears nowhere else in the corpus.
        var protectedArticles = articles.Where(a => a.ProtectedPassphrase != null).ToList();
        protectedArticles.Should().ContainSingle();
        protectedArticles[0].Path.Should().Be("/Personal");
        protectedArticles[0].Body.Should().Contain(Canary, "the protected note carries the unique marker");
        articles.Where(a => a.ProtectedPassphrase == null)
            .Should().NotContain(a => a.Body.Contains(Canary), "the marker must appear nowhere outside the protected body");

        // Tag-variant planting: exactly two variant groups under the 3.6 normalization, and the
        // decoy tech-debt folds to a form that merges with neither of them.
        var usedTags = articles.SelectMany(a => a.Tags).ToList();
        var tagGroups = usedTags.GroupBy(NormTag).ToDictionary(g => g.Key, g => g.ToList());
        var variantGroups = tagGroups.Where(g => g.Value.Distinct().Count() > 1).ToDictionary(g => g.Key, g => g.Value);
        variantGroups.Keys.Should().BeEquivalentTo(["howto", "technicaldebt"]);
        variantGroups["howto"].Distinct().Should().BeEquivalentTo(["how-to", "howto", "How To"]);
        variantGroups["technicaldebt"].Distinct().Should().BeEquivalentTo(["technical debt", "technical-debt"]);
        tagGroups["techdebt"].Distinct().Should().ContainSingle("the decoy tech-debt must stand alone under the normalization");
        usedTags.Count(t => t == "how-to").Should().Be(7);
        usedTags.Count(t => t == "howto").Should().Be(2);
        usedTags.Count(t => t == "How To").Should().Be(1);
        usedTags.Count(t => t == "technical debt").Should().Be(2);
        usedTags.Count(t => t == "technical-debt").Should().Be(1);
        usedTags.Count(t => t == "tech-debt").Should().Be(2);

        // Unused-tag planting: distinct under the normalization, attached to nothing, and not
        // colliding with any used tag under the same normalization.
        corpus.UnusedTags.Select(NormTag).Should().OnlyHaveUniqueItems();
        foreach (var unused in corpus.UnusedTags)
        {
            usedTags.Should().NotContain(unused, $"'{unused}' is planted as unused");
            tagGroups.Keys.Should().NotContain(NormTag(unused),
                $"'{unused}' must not collide with a used tag under the 3.6 normalization");
        }

        // expected[] mirrors the planted defects with the scores section 3.6 fixes outright, and
        // the clamp bounds where a formula derives the score from a measured similarity.
        var byKind = corpus.Expected.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.ToList());
        byKind["duplicate_exact"].Should().HaveCount(2).And.OnlyContain(e => e.Score == 95 && e.ScoreMin == null && e.ScoreMax == null);
        byKind["near_duplicate"].Should().HaveCount(3).And.OnlyContain(e => e.Score == null && e.ScoreMin == 50 && e.ScoreMax == 75);
        byKind["move_article"].Should().HaveCount(6).And.OnlyContain(e => e.Score == null && e.ScoreMin == 70 && e.ScoreMax == 90);
        byKind["merge_tags"].Should().HaveCount(2).And.OnlyContain(e => e.Score == 85 && e.ScoreMin == null && e.ScoreMax == null);
        byKind["delete_unused_tags"].Should().HaveCount(1).And.OnlyContain(e => e.Score == 60 && e.ScoreMin == null && e.ScoreMax == null);
        byKind["delete_empty_folder"].Should().HaveCount(2).And.OnlyContain(e => e.Score == 55 && e.ScoreMin == null && e.ScoreMax == null);
        byKind["test_leftovers"].Should().HaveCount(2).And.OnlyContain(e => e.Score == 50 && e.ScoreMin == null && e.ScoreMax == null);

        // Every expected object names something the corpus really contains.
        foreach (var e in corpus.Expected)
        {
            e.Objects.Should().NotBeEmpty();
            foreach (var o in e.Objects)
            {
                if (o.Title != null)
                {
                    o.Folder.Should().NotBeNull("an article reference names its folder too");
                    articles.Should().Contain(a => a.Path == o.Folder && a.Title == o.Title,
                        $"expected {e.Kind} must reference a planted article");
                }
                else if (o.Folder != null)
                {
                    folders.Should().Contain(o.Folder, $"expected {e.Kind} must reference a declared folder");
                }
                if (o.Tag != null)
                    usedTags.Concat(corpus.UnusedTags).Should().Contain(o.Tag,
                        $"expected {e.Kind} must reference a planted tag");
            }
        }

        // Exact-duplicate entries: subject is the kept original, other is the later-seeded copy
        // the tie-break trashes.
        foreach (var e in byKind["duplicate_exact"])
        {
            var subject = e.Objects.Should().Contain(o => o.Role == "subject").Subject;
            var other = e.Objects.Should().Contain(o => o.Role == "other").Subject;
            SeededIndex(subject.Folder!, subject.Title!).Should().BeLessThan(SeededIndex(other.Folder!, other.Title!));
        }

        // Move entries: the subject sits in the junk-drawer /Inbox, the target is one of the two
        // clusters, and the target is a folder, not an article.
        foreach (var e in byKind["move_article"])
        {
            var subject = e.Objects.Should().Contain(o => o.Role == "subject").Subject;
            subject.Folder.Should().Be("/Inbox");
            subject.Title.Should().NotBeNull();
            var target = e.Objects.Should().Contain(o => o.Role == "target").Subject;
            target.Title.Should().BeNull("the move target is a folder");
            target.Folder.Should().BeOneOf("/Kids/Puzzles", "/Cooking/Sourdough");
        }

        // Merge entries: the target variant out-uses every source variant, so it is the merge
        // target the 3.6 rule picks.
        foreach (var e in byKind["merge_tags"])
        {
            var target = e.Objects.Should().Contain(o => o.Role == "target").Subject;
            var targetUses = usedTags.Count(t => t == target.Tag);
            foreach (var source in e.Objects.Where(o => o.Role == "other"))
                targetUses.Should().BeGreaterThan(usedTags.Count(t => t == source.Tag),
                    $"'{target.Tag}' must out-use '{source.Tag}' to be the merge target");
        }
    }

    [Fact]
    public async Task SeedAsync_CreatesTheWholeCorpusOnANode()
    {
        using var factory = new BmbWebApplicationFactory();
        const string password = "dreamX1corpus";
        await factory.InitializeNodeAsync(password: password);
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/session/login", new { username = "admin", password }))
            .EnsureSuccessStatusCode();

        DreamCorpusSeeded seeded;
        using (var scope = factory.Services.CreateScope())
        {
            seeded = await DreamCorpus.SeedAsync(scope.ServiceProvider);
            seeded.ArticleCount.Should().Be(120);
            seeded.ProtectedArticles.Should().ContainSingle();

            // Every declared folder exists and is active, including the two planted empty ones.
            var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
            var activeFolders = await folderRepo.GetAllActiveAsync();
            foreach (var path in seeded.Definition.Folders)
                activeFolders.Should().Contain(f => f.Path == path, $"declared folder '{path}' must be active");

            // The unused tags exist as bare rows (zero articles); the merge-target variants carry
            // the counts expected[] relies on; the decoy stands alone.
            var tags = await scope.ServiceProvider.GetRequiredService<IConceptTagRepository>().GetAllAsync();
            foreach (var unused in seeded.Definition.UnusedTags)
            {
                var info = tags.SingleOrDefault(t => t.Name == unused);
                info.Should().NotBeNull($"unused tag '{unused}' must exist as a bare tag row");
                info!.ArticleCount.Should().Be(0, $"unused tag '{unused}' must carry no articles");
            }
            tags.Single(t => t.Name == "how-to").ArticleCount.Should().Be(7);
            tags.Single(t => t.Name == "technical debt").ArticleCount.Should().Be(2);
            tags.Should().Contain(t => t.Name == "tech-debt", "the decoy is a tag of its own");
        }

        // The protected article: Protected flag set, the canary hidden behind the passphrase,
        // and the passphrase reveals exactly that canary.
        var protectedArticle = seeded.ProtectedArticles.Single();
        using (var scope = factory.Services.CreateScope())
        {
            var articleRepo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
            var articleService = scope.ServiceProvider.GetRequiredService<ArticleService>();
            (await articleRepo.GetByIdAsync(protectedArticle.Id))!.Protected.Should().BeTrue();
            var locked = await articleService.GetContentAsync(protectedArticle.Id);
            locked.Should().NotContain(Canary, "a protected body is an opaque blob; the marker must not surface");
            var unlocked = await articleService.UnlockContentAsync(protectedArticle.Id, protectedArticle.Passphrase);
            unlocked.Should().Contain(Canary, "the corpus passphrase must unlock the canary body");
        }

        // Every expected[] reference resolves against the seeded corpus.
        foreach (var e in seeded.Definition.Expected)
        {
            foreach (var o in e.Objects)
            {
                if (o.Title != null)
                    seeded.TryArticleId(o.Folder!, o.Title, out _).Should().BeTrue(
                        $"expected {e.Kind} must reference a seeded article ('{o.Title}' in '{o.Folder}')");
                else if (o.Folder != null)
                    seeded.FolderIdByPath.Should().ContainKey(o.Folder,
                        $"expected {e.Kind} must reference a seeded folder ('{o.Folder}')");
            }
        }
    }

    [Fact]
    public async Task SeedAsync_WithModelAvailable_EmbedsEveryUnprotectedArticle_AndSkipsTheProtectedOne()
    {
        using var factory = new BmbWebApplicationFactory();
        const string password = "dreamX1corpus";
        await factory.InitializeNodeAsync(password: password);
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/session/login", new { username = "admin", password }))
            .EnsureSuccessStatusCode();

        DreamCorpusSeeded seeded;
        using (var scope = factory.Services.CreateScope())
        {
            seeded = await DreamCorpus.SeedAsync(scope.ServiceProvider);
        }

        // Probe before asserting: no real model provider is ever contacted; a machine without the
        // local model simply has nothing for this fact to assert (xUnit 2.9 has no dynamic skip).
        // The Linux test nodes carry the model and run the full assertions below.
        try
        {
            factory.Services.GetRequiredService<IEmbeddingGenerator>().Generate("model availability probe");
        }
        catch (ModelUnavailableException ex)
        {
            _output.WriteLine("SKIP embedding assertions: the local embedding model is unavailable ({0}). " +
                "The model-gated part of this fact runs on the Linux test nodes.", ex.Message);
            return;
        }

        var drained = await factory.Services.GetRequiredService<PendingEmbeddingProcessor>()
            .DrainAllPendingAsync(CancellationToken.None);
        drained.Should().BeGreaterThan(0, "with a model available, the drain must have processed the seeded articles");

        using var check = factory.Services.CreateScope();
        var chunkRows = await check.ServiceProvider.GetRequiredService<IArticleChunkEmbeddingRepository>()
            .GetAllForActiveArticlesAsync();
        var embedded = chunkRows.Select(r => r.ArticleId).ToHashSet();

        foreach (var a in seeded.Definition.Articles.Where(a => a.ProtectedPassphrase == null))
            embedded.Should().Contain(seeded.ArticleId(a.Path, a.Title),
                $"'{a.Title}' must carry at least one chunk embedding");
        foreach (var p in seeded.ProtectedArticles)
            embedded.Should().NotContain(p.Id, "a passphrase-protected body is never embedded");
    }
}
