using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A soft-deleted row is still a replicated row, and the applier compares incoming creates and
/// updates against the version stored on it. These tests pin down that the version stored on a
/// deleted row is the DELETE's — not whatever the last edit left behind.
///
/// <para>
/// The failure this guards against is not a crash and not a lost write; it is two nodes that
/// permanently disagree about whether an article exists, each convinced it applied the newest
/// event, with nothing in the protocol that ever revisits the question.
/// </para>
/// </summary>
public class DeleteRowVersionTests : IAsyncLifetime
{
    private SyncTestFixture _nodeA = null!;
    private SyncTestFixture _nodeB = null!;

    public async Task InitializeAsync()
    {
        _nodeA = new ConcreteFixture();
        await _nodeA.InitializeAsync();
        await _nodeA.InitService.InitializeAsync("admin", "NodeA", "passwordA");
        await _nodeA.Session.UnlockAsync("passwordA");

        _nodeB = new ConcreteFixture();
        await _nodeB.InitializeAsync();
        await _nodeB.InitService.InitializeAsync("admin", "NodeB", "passwordB");
        await _nodeB.Session.UnlockAsync("passwordB");

        await TrustAsync(_nodeA, _nodeB);
        await TrustAsync(_nodeB, _nodeA);
    }

    private static async Task TrustAsync(SyncTestFixture host, SyncTestFixture peer)
    {
        var identity = (await peer.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await host.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identity.NodeId,
            DisplayName = identity.DisplayName,
            Ed25519PublicKey = identity.Ed25519PublicKey,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    /// <summary>
    /// The resurrection. NodeA deletes an article; NodeB, which has not seen the delete yet, edits
    /// it at a STRICTLY LOWER Lamport. NodeA must drop that edit.
    ///
    /// <para>
    /// Before the row carried the delete's version, NodeA compared the incoming edit against the
    /// version of the article's last edit — a number from before the delete, and lower than the
    /// peer's — so the edit won and flipped the row back to 'A'. NodeB meanwhile compared NodeA's
    /// delete EVENT (correctly versioned) against its own row and deleted. Article alive on A,
    /// gone on B, forever.
    /// </para>
    ///
    /// <para>
    /// The Lamport values are arranged so the outcome does not depend on the node-id tiebreak:
    /// the delete is strictly newer than the edit it must beat, and strictly newer than the row
    /// version it replaces. An unrelated article is created on NodeA purely to advance its clock,
    /// so the delete lands above NodeB's edit without touching the row under test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LocalDelete_BeatsOlderPeerEdit_InsteadOfBeingResurrected()
    {
        // L=1 on A: create, and replicate it so both nodes hold the same row.
        var article = await _nodeA.ArticleService.CreateAsync("Shared", "/Root", [], "original");
        var createEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single();
        await _nodeB.ApplyFromAsync(_nodeA, createEvent);

        // The concurrent edit, held back as if the link were down. Title only, deliberately: the
        // two fixture nodes never joined, so they hold different master DEKs and B cannot
        // re-encrypt a body A wrote. The gate under test reads the version, not the body.
        await _nodeB.ArticleService.UpdateAsync(article.Id, title: "edited on B");
        var peerEdit = (await _nodeB.EventLogRepo.GetAfterSequenceAsync(0))
            .Last(e => e.EventType == EventTypes.ArticleUpdate);

        // Put A's clock strictly above the peer's edit before deleting. Otherwise the two tie and
        // the node-id tiebreak — a random Guid per run — decides the outcome instead of the rule
        // under test. Burning ticks is what any other local write on A would have done anyway.
        while (_nodeA.Clock.Current <= peerEdit.LamportTs) _nodeA.Clock.Tick();

        await _nodeA.ArticleService.DeleteAsync(article.Id);

        var deletedRow = await _nodeA.ArticleRepo.GetByIdAsync(article.Id, includeDeleted: true);
        deletedRow!.Status.Should().Be("D");
        deletedRow.LamportTs.Should().BeGreaterThan(peerEdit.LamportTs,
            "the row must carry the delete's Lamport, not the version its last edit left behind");

        // A receives the older edit. It must lose — but it must actually be APPLIED and lose on
        // the version comparison, not be dropped for some unrelated reason, or this test would
        // pass with the bug still in place.
        var applyResult = await _nodeA.ApplyFromAsync(_nodeB, peerEdit);
        applyResult.Should().Be(EventApplyResult.Applied,
            "the peer edit must reach the version gate, not be dropped for an unrelated reason");

        var afterApply = await _nodeA.ArticleRepo.GetByIdAsync(article.Id, includeDeleted: true);
        afterApply!.Status.Should().Be("D", "an edit older than the delete must not resurrect the article");

        // And the other half of the divergence: B applies A's delete and agrees.
        var deleteEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0))
            .Single(e => e.EventType == EventTypes.ArticleDelete);
        await _nodeB.ApplyFromAsync(_nodeA, deleteEvent);

        var onB = await _nodeB.ArticleRepo.GetByIdAsync(article.Id, includeDeleted: true);
        onB!.Status.Should().Be("D");
    }

    /// <summary>
    /// The same rule on the applier's own delete path: a delete that arrives over sync stamps the
    /// row too, so a node that only ever RECEIVES deletes is no more likely to resurrect than the
    /// one that issued them.
    /// </summary>
    [Fact]
    public async Task AppliedDelete_StampsTheRowWithTheEventsVersion()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Shared", "/Root", [], "original");
        var createEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single();
        await _nodeB.ApplyFromAsync(_nodeA, createEvent);

        await _nodeA.ArticleService.CreateAsync("Unrelated", "/Root", [], "noise");
        await _nodeA.ArticleService.DeleteAsync(article.Id);
        var deleteEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0))
            .Single(e => e.EventType == EventTypes.ArticleDelete);

        await _nodeB.ApplyFromAsync(_nodeA, deleteEvent);

        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var onB = await _nodeB.ArticleRepo.GetByIdAsync(article.Id, includeDeleted: true);
        onB!.Status.Should().Be("D");
        onB.LamportTs.Should().Be(deleteEvent.LamportTs);
        onB.SourceNodeId.Should().Be(identityA.NodeId);
    }

    /// <summary>
    /// Two nodes delete the same article independently, then each receives the other's delete.
    /// Both rows must end up attributed to the SAME delete.
    ///
    /// <para>
    /// The applier used to return the moment it saw a row that was already 'D', so each node kept
    /// its own delete's version. The article is gone on both, which looks fine — until a later
    /// event ties on Lamport with the recorded delete, at which point the node-id tiebreak reads a
    /// different id on each node and the event lands on one but not the other. Convergence here is
    /// what keeps the two histories from forking in a way nothing later repairs.
    /// </para>
    ///
    /// </summary>
    [Fact]
    public async Task ConcurrentDeletes_ConvergeOnTheSameVersion()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Shared", "/Root", [], "original");
        var createEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single();
        await _nodeB.ApplyFromAsync(_nodeA, createEvent);

        await _nodeA.ArticleService.DeleteAsync(article.Id);
        await _nodeB.ArticleService.DeleteAsync(article.Id);

        var deleteFromA = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0))
            .Single(e => e.EventType == EventTypes.ArticleDelete);
        var deleteFromB = (await _nodeB.EventLogRepo.GetAfterSequenceAsync(0))
            .Single(e => e.EventType == EventTypes.ArticleDelete);
        await _nodeA.ApplyFromAsync(_nodeB, deleteFromB);
        await _nodeB.ApplyFromAsync(_nodeA, deleteFromA);

        var onA = await _nodeA.ArticleRepo.GetByIdAsync(article.Id, includeDeleted: true);
        var onB = await _nodeB.ArticleRepo.GetByIdAsync(article.Id, includeDeleted: true);

        onA!.Status.Should().Be("D");
        onB!.Status.Should().Be("D");
        onA.LamportTs.Should().Be(onB.LamportTs);
        onA.SourceNodeId.Should().Be(onB.SourceNodeId,
            "both nodes must attribute the row to the delete that won, not to their own");

        // And the winner is the one the comparator picks, not merely whichever arrived second.
        var fromA = new RowVersion(deleteFromA.LamportTs, deleteFromA.NodeId);
        var fromB = new RowVersion(deleteFromB.LamportTs, deleteFromB.NodeId);
        var expected = ConflictResolver.IncomingWins(fromA, fromB) ? fromB : fromA;
        new RowVersion(onA.LamportTs, onA.SourceNodeId ?? Guid.Empty).Should().Be(expected);
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
