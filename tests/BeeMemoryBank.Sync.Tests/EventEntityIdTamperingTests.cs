using BeeMemoryBank.Core.Models;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// <c>SyncEvent.EntityId</c> is transported but not signed — <see cref="EventSignature.BuildPayload"/>
/// covers EventId, NodeId, LamportTs, EventType, ArticleId, Payload, ProtocolVersion and CreatedAt,
/// and nothing else. So a node that merely RELAYS someone else's event can rewrite this one field
/// and the Ed25519 signature still verifies.
///
/// The hard-delete gate keys on that field, so a relay could point it at an unrelated id that HAS
/// been hard-deleted and watch a perfectly innocent event get silently dropped instead of applied —
/// censorship that leaves no error anywhere. The forged value was then written to tbl_event and
/// relayed onward, so the lie outlived the hop that told it.
///
/// These tests tamper with exactly what a relay can tamper with — the transported field, leaving the
/// signature untouched — and assert the applier ignores it in favour of the value derived from the
/// signed fields. Two of them failed before the derivation was added (relabelling an event to get it
/// dropped, and the forged value being persisted and relayed onward); the other two document paths
/// that happened to be safe already, and exist so a future simplification cannot quietly open them.
/// </summary>
public class EventEntityIdTamperingTests : IAsyncLifetime
{
    private SyncTestFixture _nodeA = null!;
    private SyncTestFixture _nodeB = null!;

    public async Task InitializeAsync()
    {
        _nodeA = new ConcreteFixture();
        await _nodeA.InitializeAsync();
        await _nodeA.InitService.InitializeAsync("admin", "NodeA", "pass");
        await _nodeA.Session.UnlockAsync("pass");

        _nodeB = new ConcreteFixture();
        await _nodeB.InitializeAsync();
        await _nodeB.InitService.InitializeAsync("admin", "NodeB", "pass");
        await _nodeB.Session.UnlockAsync("pass");

        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var identityB = (await _nodeB.NodeRepo.GetAsync())!;

        // IsSuperadmin: hard-delete events are only accepted from a superadmin originator.
        await _nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityA.NodeId,
            DisplayName = identityA.DisplayName,
            Ed25519PublicKey = identityA.Ed25519PublicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            IsSuperadmin = true
        });
        await _nodeA.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityB.NodeId,
            DisplayName = identityB.DisplayName,
            Ed25519PublicKey = identityB.Ed25519PublicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            IsSuperadmin = true
        });
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    [Fact]
    public async Task BlankingEntityId_DoesNotSlipAnUpdatePastTheHardDeleteGate()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Target", "/Work", [], "Secret");
        foreach (var e in await _nodeA.EventLogRepo.GetAfterSequenceAsync(0))
            await _nodeB.ApplyFromAsync(_nodeA, e);

        await _nodeB.HardDeleteService.DeleteArticleAsync(article.Id, 1, null, CancellationToken.None);

        await _nodeA.ArticleService.UpdateAsync(article.Id, title: "Resurrected");
        var updateEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        updateEvent.EventType.Should().Be(EventTypes.ArticleUpdate);

        // The relay's whole move: erase the field the gate reads, touch nothing the signature covers.
        updateEvent.EntityId = null;

        await _nodeB.ApplyFromAsync(_nodeA, updateEvent);

        // This one was already safe: the gate fell back to ArticleId when EntityId was missing, so
        // blanking it bought the attacker nothing for an article event. Kept as a guard, because the
        // fallback is exactly the kind of line that gets tidied away as redundant.
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id)).Should().BeNull(
            "the gate identifier comes from the signed ArticleId, not from the field the relay controls");
    }

    [Fact]
    public async Task PointingEntityIdAtADeletedEntity_DoesNotSuppressAnUnrelatedEvent()
    {
        var victim = await _nodeA.ArticleService.CreateAsync("Victim", "/Work", [], "still wanted");
        var decoy = await _nodeA.ArticleService.CreateAsync("Decoy", "/Work", [], "will be deleted");
        foreach (var e in await _nodeA.EventLogRepo.GetAfterSequenceAsync(0))
            await _nodeB.ApplyFromAsync(_nodeA, e);

        await _nodeB.HardDeleteService.DeleteArticleAsync(decoy.Id, 1, null, CancellationToken.None);

        await _nodeA.ArticleService.UpdateAsync(victim.Id, title: "Still Here");
        var updateEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();

        // Censorship by relabelling: claim this event is about the entity that was deleted, and the
        // gate drops it. The victim's update disappears with nothing logged as an error.
        updateEvent.EntityId = decoy.Id.ToString();

        await _nodeB.ApplyFromAsync(_nodeA, updateEvent);

        var applied = await _nodeB.ArticleRepo.GetByIdAsync(victim.Id);
        applied.Should().NotBeNull();
        applied!.Title.Should().Be("Still Here",
            "the event is about the article named in the signed ArticleId, whatever the relay claims");
    }

    [Fact]
    public async Task ARelayCannotRedirectAHardDeleteToADifferentEntity()
    {
        var target = await _nodeA.ArticleService.CreateAsync("Target", "/Work", [], "delete me");
        var bystander = await _nodeA.ArticleService.CreateAsync("Bystander", "/Work", [], "keep me");
        foreach (var e in await _nodeA.EventLogRepo.GetAfterSequenceAsync(0))
            await _nodeB.ApplyFromAsync(_nodeA, e);

        await _nodeA.HardDeleteService.DeleteArticleAsync(target.Id, 1, null, CancellationToken.None);
        var deleteEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        deleteEvent.EventType.Should().Be(EventTypes.HardDelete);

        // For hard deletes the entity is named in the signed payload, so redirecting the transported
        // copy should change nothing at all — the delete must still land on its real target. Also
        // already safe today (HardDeleteService reads the payload), and pinned here so it stays that
        // way if anyone reaches for the more convenient-looking transported field.
        deleteEvent.EntityId = bystander.Id.ToString();

        await _nodeB.ApplyFromAsync(_nodeA, deleteEvent);

        (await _nodeB.ArticleRepo.GetByIdAsync(target.Id)).Should().BeNull("the payload names this one");
        (await _nodeB.ArticleRepo.GetByIdAsync(bystander.Id)).Should().NotBeNull(
            "the relay does not get to choose what a signed hard delete destroys");
    }

    [Fact]
    public async Task TheStoredEventCarriesTheDerivedEntityId_NotTheOneThatArrived()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Target", "/Work", [], "body");
        var createEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();

        createEvent.EntityId = "totally-made-up";
        await _nodeB.ApplyFromAsync(_nodeA, createEvent);

        // Whatever we persist gets relayed onward and read back by compaction and the audit trail,
        // so a forged value must not survive the hop even when it changes no decision today.
        var stored = (await _nodeB.EventLogRepo.GetAfterSequenceAsync(0))
            .Single(e => e.EventId == createEvent.EventId);
        stored.EntityId.Should().Be(article.Id.ToString());
    }

    private class ConcreteFixture : SyncTestFixture { }
}
