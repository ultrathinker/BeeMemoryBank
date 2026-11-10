using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>bee_add_tags</c> and <c>bee_remove_tag</c> are writes like any other: they must go through
/// <see cref="ArticleService"/>, which stores the tag set and the <c>article_update</c> event in one
/// transaction (the event payload carries the FULL tag set), under the article's write lock, and
/// through the repository's read-only-folder guard.
///
/// They did not. They wrote the tag tables directly: the tag appeared on the node the agent talked to,
/// the article's version and <c>updatedAt</c> stayed put, the log held no event, no peer ever heard of
/// it, the next <c>article_update</c> from another node replaced the tag set and removed it silently,
/// and an agent could re-tag an article in a folder that is read-only for its user.
///
/// These tests use a REAL <see cref="EventLogger"/> (the other MCP tool tests use a null logger, which
/// is why nothing noticed) and read the event log back.
/// </summary>
public class McpTagWriteTests : IAsyncLifetime
{
    private McpToolRig _rig = null!;

    public async Task InitializeAsync() => _rig = await McpToolRig.CreateAsync();

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    // ── what a real change leaves behind ─────────────────────────────────────

    [Fact]
    public async Task AddTags_WritesOneArticleUpdateEventCarryingTheFullTagSet()
    {
        var article = await _rig.ArticleService.CreateAsync("Doc", "/Work", ["existing"], "body");
        var before = await Reload(article.Id);
        await Task.Delay(30);

        var result = await _rig.ConceptTools.AddTags(article.Id, ["fresh-one", "fresh-two"]);

        result.Should().Be("Added 2 tag(s) to article 'Doc'.");
        var updates = await UpdateEventsAsync(article.Id);
        updates.Should().ContainSingle("a real change must tell the log, so a peer hears of it");
        PayloadTags(updates[0]).Should().BeEquivalentTo("existing", "fresh-one", "fresh-two");
        (await Tags(article.Id)).Should().BeEquivalentTo("existing", "fresh-one", "fresh-two");

        var after = await Reload(article.Id);
        after.UpdatedAt.Should().BeAfter(before.UpdatedAt, "the article changed");
        (after.LamportTs, after.SourceNodeId).Should().NotBe((before.LamportTs, before.SourceNodeId), "the row's version has to move, or a peer's older copy wins the conflict");
        after.LamportTs.Should().Be(updates[0].LamportTs, "the row and the event carry the same version");
    }

    [Fact]
    public async Task AddTags_CountsOnlyTheNewlyAddedTags_AndKeepsTheReturnText()
    {
        var article = await _rig.ArticleService.CreateAsync("Doc", "/Work", ["Known"], "body");

        var result = await _rig.ConceptTools.AddTags(article.Id, ["known", "KNOWN", "new", "NEW", "  ", ""]);

        result.Should().Be("Added 1 tag(s) to article 'Doc'.", "duplicates, tags already on the article and blank names are not counted");
        (await Tags(article.Id)).Should().BeEquivalentTo("Known", "new");
        (await UpdateEventsAsync(article.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveTag_WritesOneArticleUpdateEventCarryingTheRemainingTags()
    {
        var article = await _rig.ArticleService.CreateAsync("Doc", "/Work", ["keep", "drop"], "body");
        var before = await Reload(article.Id);
        await Task.Delay(30);

        var result = await _rig.ConceptTools.RemoveTag(article.Id, "DROP");

        result.Should().Be("Removed tag 'DROP' from article 'Doc'.");
        var updates = await UpdateEventsAsync(article.Id);
        updates.Should().ContainSingle();
        PayloadTags(updates[0]).Should().BeEquivalentTo("keep");
        (await Tags(article.Id)).Should().BeEquivalentTo("keep");
        (await Reload(article.Id)).UpdatedAt.Should().BeAfter(before.UpdatedAt);
    }

    [Fact]
    public async Task RemovingTheLastTag_WritesAnEventWithAnEmptyTagSet()
    {
        var article = await _rig.ArticleService.CreateAsync("Doc", "/Work", ["only"], "body");

        (await _rig.ConceptTools.RemoveTag(article.Id, "only")).Should().Be("Removed tag 'only' from article 'Doc'.");

        var updates = await UpdateEventsAsync(article.Id);
        updates.Should().ContainSingle();
        PayloadTags(updates[0]).Should().BeEmpty("an empty set is a real state a peer has to reach");
        (await Tags(article.Id)).Should().BeEmpty();
    }

    // ── a call that changes nothing leaves nothing ───────────────────────────

    [Fact]
    public async Task AddTags_WhenEveryTagIsAlreadyThere_WritesNoEventAndLeavesUpdatedAtAlone()
    {
        var article = await _rig.ArticleService.CreateAsync("Doc", "/Work", ["alpha", "Beta"], "body");
        var before = await Reload(article.Id);
        var eventsBefore = await _rig.EventLog.GetTotalCountAsync();
        await Task.Delay(30);

        var result = await _rig.ConceptTools.AddTags(article.Id, ["ALPHA", "beta", "alpha"]);

        result.Should().Be("Added 0 tag(s) to article 'Doc'.");
        (await _rig.EventLog.GetTotalCountAsync()).Should().Be(eventsBefore);
        var after = await Reload(article.Id);
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        (after.LamportTs, after.SourceNodeId).Should().Be((before.LamportTs, before.SourceNodeId));
        (await Tags(article.Id)).Should().BeEquivalentTo("alpha", "Beta");
    }

    [Fact]
    public async Task RemoveTag_WhenTheTagWasNotThere_WritesNoEventAndLeavesUpdatedAtAlone()
    {
        var article = await _rig.ArticleService.CreateAsync("Doc", "/Work", ["alpha"], "body");
        var before = await Reload(article.Id);
        var eventsBefore = await _rig.EventLog.GetTotalCountAsync();
        await Task.Delay(30);

        var result = await _rig.ConceptTools.RemoveTag(article.Id, "typo");

        result.Should().Be("Tag 'typo' was not on article 'Doc' (no-op).");
        (await _rig.EventLog.GetTotalCountAsync()).Should().Be(eventsBefore);
        var after = await Reload(article.Id);
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        after.LamportTs.Should().Be(before.LamportTs);
    }

    [Fact]
    public async Task AddingNothing_IsANoOp()
    {
        var article = await _rig.ArticleService.CreateAsync("Doc", "/Work", ["alpha"], "body");
        var eventsBefore = await _rig.EventLog.GetTotalCountAsync();

        (await _rig.ConceptTools.AddTags(article.Id, [])).Should().Be("Added 0 tag(s) to article 'Doc'.");

        (await _rig.EventLog.GetTotalCountAsync()).Should().Be(eventsBefore);
    }

    [Fact]
    public async Task UnknownArticle_IsReportedAsNotFound_ForBothTools()
    {
        var id = Guid.NewGuid();

        (await _rig.ConceptTools.AddTags(id, ["x"])).Should().Be($"Error: article {id} not found");
        (await _rig.ConceptTools.RemoveTag(id, "x")).Should().Be($"Error: article {id} not found");
    }

    // ── nothing else of the article is touched ───────────────────────────────

    [Fact]
    public async Task AddTags_LeavesTitleFolderAndBodyAlone_AndAddsNoVersionSnapshot()
    {
        var article = await _rig.ArticleService.CreateAsync("Doc", "/Work/Sub", [], "the body text");

        await _rig.ConceptTools.AddTags(article.Id, ["t"]);

        var after = await Reload(article.Id);
        after.Title.Should().Be("Doc");
        after.TreePath.Should().Be("/Work/Sub");
        (await _rig.ArticleService.GetContentAsync(article.Id)).Should().Be("the body text");
        var versions = await new ArticleVersionRepository(_rig.Factory, _rig.ScopeHolder).GetByArticleIdAsync(article.Id);
        versions.Should().BeEmpty("a tag change does not rewrite the body, so there is nothing to snapshot");
    }

    // ── the read-only folder check ───────────────────────────────────────────

    [Fact]
    public async Task AddTags_InAReadOnlyFolder_IsDenied_AndChangesNothing()
    {
        var article = await _rig.ArticleService.CreateAsync("Locked", "/ReadOnly/Docs", ["kept"], "body");
        var eventsBefore = await _rig.EventLog.GetTotalCountAsync();
        var before = await Reload(article.Id);
        _rig.ActAsReadOnlyUser("/ReadOnly");

        var result = await _rig.ConceptTools.AddTags(article.Id, ["intruder"]);

        result.Should().StartWith("Access denied: folder '/ReadOnly/Docs' is read-only for your user.");
        _rig.ActAsSystem();
        (await Tags(article.Id)).Should().BeEquivalentTo("kept");
        (await _rig.EventLog.GetTotalCountAsync()).Should().Be(eventsBefore);
        (await Reload(article.Id)).UpdatedAt.Should().Be(before.UpdatedAt);
    }

    [Fact]
    public async Task RemoveTag_InAReadOnlyFolder_IsDenied_AndChangesNothing()
    {
        var article = await _rig.ArticleService.CreateAsync("Locked", "/ReadOnly/Docs", ["kept"], "body");
        var eventsBefore = await _rig.EventLog.GetTotalCountAsync();
        _rig.ActAsReadOnlyUser("/ReadOnly");

        var result = await _rig.ConceptTools.RemoveTag(article.Id, "kept");

        result.Should().StartWith("Access denied: folder '/ReadOnly/Docs' is read-only for your user.");
        _rig.ActAsSystem();
        (await Tags(article.Id)).Should().BeEquivalentTo("kept");
        (await _rig.EventLog.GetTotalCountAsync()).Should().Be(eventsBefore);
    }

    [Fact]
    public async Task AddTags_InAWritableFolder_StillWorksForARestrictedUser()
    {
        var article = await _rig.ArticleService.CreateAsync("Open", "/Writable", [], "body");
        _rig.ActAsReadOnlyUser("/ReadOnly");

        var result = await _rig.ConceptTools.AddTags(article.Id, ["fine"]);

        result.Should().Be("Added 1 tag(s) to article 'Open'.");
        _rig.ActAsSystem();
        (await UpdateEventsAsync(article.Id)).Should().ContainSingle();
    }

    // ── two callers at once ──────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentAddTags_OnOneArticle_LoseNoTag()
    {
        // The embedding generator is called for every new tag between the moment the tag set is read and the
        // moment it is written back, and this one is slow: a read that is not under the same lock as the write
        // lets every other caller read the OLD set meanwhile, and the last writer wins with only its own tag.
        _rig.Embeddings.Delay = TimeSpan.FromMilliseconds(15);
        var article = await _rig.ArticleService.CreateAsync("Contended", "/Work", ["seed"], "body");
        const int callers = 12;

        using var gate = new ManualResetEventSlim(false);
        var calls = Enumerable.Range(0, callers).Select(i => Task.Run(async () =>
        {
            gate.Wait();
            return await _rig.ConceptTools.AddTags(article.Id, [$"tag-{i}"]);
        })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(r => r == "Added 1 tag(s) to article 'Contended'.");
        (await Tags(article.Id)).Should().BeEquivalentTo(
            new[] { "seed" }.Concat(Enumerable.Range(0, callers).Select(i => $"tag-{i}")));
        var updates = await UpdateEventsAsync(article.Id);
        updates.Should().HaveCount(callers, "every real change is one event");
        // The events form a chain: each carries one tag more than the one before it, never a set that lost an earlier tag.
        updates.Select(e => PayloadTags(e).Length).OrderBy(n => n)
            .Should().Equal(Enumerable.Range(2, callers), "each event holds the whole set at its moment");
    }

    [Fact]
    public async Task ConcurrentAddAndRemove_OnOneArticle_EndInAStateEveryEventAgreesWith()
    {
        _rig.Embeddings.Delay = TimeSpan.FromMilliseconds(10);
        var article = await _rig.ArticleService.CreateAsync("Contended", "/Work", ["base"], "body");

        using var gate = new ManualResetEventSlim(false);
        var calls = new[]
        {
            Task.Run(async () => { gate.Wait(); return await _rig.ConceptTools.AddTags(article.Id, ["a"]); }),
            Task.Run(async () => { gate.Wait(); return await _rig.ConceptTools.AddTags(article.Id, ["b"]); }),
            Task.Run(async () => { gate.Wait(); return await _rig.ConceptTools.RemoveTag(article.Id, "base"); }),
        };
        gate.Set();
        await Task.WhenAll(calls);

        (await Tags(article.Id)).Should().BeEquivalentTo("a", "b");
        var last = (await UpdateEventsAsync(article.Id)).OrderBy(e => e.SequenceNum).Last();
        PayloadTags(last).Should().BeEquivalentTo(new[] { "a", "b" }, "the last event is the state the node ends in");
    }

    // ── an article under a second password ───────────────────────────────────

    [Fact]
    public async Task ProtectedArticle_GetsItsTagsLikeThroughRest_AndStaysProtected()
    {
        var article = await _rig.ArticleService.CreateAsync("Secret-ish", "/Work", [], "the protected body");
        await _rig.ArticleService.ProtectAsync(article.Id, "second password", "hint");
        var bodyBefore = await _rig.ArticleService.GetContentAsync(article.Id);
        var eventsBefore = (await UpdateEventsAsync(article.Id)).Count;

        var result = await _rig.ConceptTools.AddTags(article.Id, ["labelled"]);

        result.Should().Be("Added 1 tag(s) to article 'Secret-ish'.");
        var updates = await UpdateEventsAsync(article.Id);
        updates.Should().HaveCount(eventsBefore + 1);
        PayloadTags(updates.OrderBy(e => e.SequenceNum).Last()).Should().BeEquivalentTo("labelled");
        var after = await Reload(article.Id);
        after.Protected.Should().BeTrue("tagging must not unprotect the article");
        after.ProtectionHint.Should().Be("hint");
        (await _rig.ArticleService.GetContentAsync(article.Id)).Should().Be(bodyBefore, "the sealed body is not rewritten");
        (await _rig.ArticleService.UnlockContentAsync(article.Id, "second password")).Should().Be("the protected body");

        (await _rig.ConceptTools.RemoveTag(article.Id, "labelled")).Should().Be("Removed tag 'labelled' from article 'Secret-ish'.");
        (await Reload(article.Id)).Protected.Should().BeTrue();
        (await Tags(article.Id)).Should().BeEmpty();
    }

    // ── the vocabulary ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddTags_GeneratesEmbeddingsOnlyForTagsNewToTheVocabulary()
    {
        var other = await _rig.ArticleService.CreateAsync("Other", "/Work", ["shared"], "x");
        await _rig.ConceptTagService.BackfillEmbeddingsAsync();
        var target = await _rig.ArticleService.CreateAsync("Target", "/Work", [], "y");
        _rig.Embeddings.Calls.Clear();

        await _rig.ConceptTools.AddTags(target.Id, ["shared", "brand-new"]);

        _rig.Embeddings.Calls.Should().Equal(["brand-new"], "'shared' already has an embedding in the vocabulary");
        var withEmbeddings = (await _rig.ConceptTagRepo.GetWithEmbeddingsAsync()).Select(c => c.Name);
        withEmbeddings.Should().Contain(["shared", "brand-new"]);
        other.Should().NotBeNull();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Article> Reload(Guid id) =>
        (await _rig.ArticleRepo.GetByIdAsync(id))!;

    private Task<List<string>> Tags(Guid id) => _rig.ConceptTagRepo.GetByArticleIdAsync(id);

    private Task<List<SyncEvent>> UpdateEventsAsync(Guid articleId) => _rig.UpdateEventsAsync(articleId);

    private static string[] PayloadTags(SyncEvent evt) =>
        JsonSerializer.Deserialize<ArticleEventPayload>(evt.Payload)!.ConceptTags ?? [];
}
