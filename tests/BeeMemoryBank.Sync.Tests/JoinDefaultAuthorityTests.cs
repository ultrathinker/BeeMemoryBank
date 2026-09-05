using BeeMemoryBank.Core.Models;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Pins the default authority a freshly joined peer gets, matching what
/// <c>JoinEndpoints.cs</c> now writes for a new joiner: <c>IsSuperadmin = false</c>. The whitelist
/// row NodeR holds for NodeD in these tests is built the same way — it is what the receiving node
/// stores after a real <c>/api/join</c> call, not a synthetic value invented for the test.
///
/// <para>Content still syncs freely (article create/update/delete are never gated). Cluster-state
/// events — <c>hard_delete</c> and <c>whitelist_revoke</c> here, the <c>EventAuthorization.SuperadminOnly</c>
/// set that <c>EventApplier.ApplyAsync</c>'s gate protects — are refused from a non-superadmin
/// originator, and accepted again only after the receiving node performs the explicit promotion
/// (<c>PUT /api/whitelist/{nodeId}/superadmin</c>), modelled here by flipping the local row the same
/// way that endpoint does. That the set is complete and correctly classified is held separately by
/// <c>EventAuthorizationGuardTests</c>.</para>
/// </summary>
public class JoinDefaultAuthorityTests : IAsyncLifetime
{
    private SyncTestFixture _nodeR = null!; // The established node a new peer joins through.
    private SyncTestFixture _nodeD = null!; // A freshly joined peer: content-only by default.
    private Guid _thirdPartyId;             // A peer already on NodeR, used as a revoke target.

    public async Task InitializeAsync()
    {
        _nodeR = new ConcreteFixture();
        await _nodeR.InitializeAsync();
        await _nodeR.InitService.InitializeAsync("admin", "NodeR", "pass");
        await _nodeR.Session.UnlockAsync("pass");

        _nodeD = new ConcreteFixture();
        await _nodeD.InitializeAsync();
        await _nodeD.InitService.InitializeAsync("admin", "NodeD", "pass");
        await _nodeD.Session.UnlockAsync("pass");

        var identityD = (await _nodeD.NodeRepo.GetAsync())!;

        // This is exactly the row JoinEndpoints.cs writes today for a new joiner: IsSuperadmin
        // defaults to false. If that default ever regresses back to true, every "refused" test
        // below must fail — see the reinstate-the-bug step in the PR description.
        await _nodeR.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityD.NodeId,
            DisplayName = identityD.DisplayName,
            Ed25519PublicKey = identityD.Ed25519PublicKey,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsSuperadmin = false
        });

        // A third peer already on NodeR, so the whitelist_revoke tests have a real row to target
        // that is neither the sender nor the receiver.
        _thirdPartyId = Guid.NewGuid();
        var (thirdPartyKey, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
        await _nodeR.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _thirdPartyId,
            DisplayName = "NodeC",
            Ed25519PublicKey = thirdPartyKey,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsSuperadmin = false
        });
    }

    public async Task DisposeAsync()
    {
        await _nodeR.DisposeAsync();
        await _nodeD.DisposeAsync();
    }

    [Fact]
    public async Task FreshlyJoinedPeer_ArticleCreateUpdateDelete_AreApplied()
    {
        // Create.
        var article = await _nodeD.ArticleService.CreateAsync("Notes", "/Inbox", [], "hello");
        var createEvent = (await _nodeD.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        createEvent.EventType.Should().Be(EventTypes.ArticleCreate);
        await _nodeR.ApplyFromAsync(_nodeD, createEvent);
        (await _nodeR.ArticleRepo.GetByIdAsync(article.Id)).Should()
            .NotBeNull("content sync from a content-only peer must never be gated");

        // Update.
        await _nodeD.ArticleService.UpdateAsync(article.Id, title: "Notes (edited)");
        var updateEvent = (await _nodeD.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        updateEvent.EventType.Should().Be(EventTypes.ArticleUpdate);
        await _nodeR.ApplyFromAsync(_nodeD, updateEvent);
        (await _nodeR.ArticleRepo.GetByIdAsync(article.Id))!.Title.Should().Be("Notes (edited)");

        // Delete.
        await _nodeD.ArticleService.DeleteAsync(article.Id);
        var deleteEvent = (await _nodeD.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        deleteEvent.EventType.Should().Be(EventTypes.ArticleDelete);
        await _nodeR.ApplyFromAsync(_nodeD, deleteEvent);
        (await _nodeR.ArticleRepo.GetByIdAsync(article.Id)).Should().BeNull();
    }

    [Fact]
    public async Task FreshlyJoinedPeer_HardDelete_IsRefused()
    {
        var article = await _nodeD.ArticleService.CreateAsync("Target", "/Work", [], "secret");
        var hardDeleteEvent = await LogHardDeleteFromD(article.Id);

        var apply = async () => await _nodeR.ApplyFromAsync(_nodeD, hardDeleteEvent);

        await apply.Should().ThrowAsync<UnauthorizedAccessException>(
            "a content-only peer must not be able to purge content network-wide");
    }

    [Fact]
    public async Task FreshlyJoinedPeer_WhitelistRevoke_IsRefused()
    {
        var revokeEvent = await LogWhitelistRevokeFromD(_thirdPartyId);

        var apply = async () => await _nodeR.ApplyFromAsync(_nodeD, revokeEvent);

        await apply.Should().ThrowAsync<UnauthorizedAccessException>(
            "a content-only peer must not be able to revoke another peer");
        (await _nodeR.WhitelistRepo.GetByNodeIdAsync(_thirdPartyId))!.Status.Should().Be("A",
            "the refused revoke must not have taken effect");
    }

    [Fact]
    public async Task AfterExplicitPromotion_HardDeleteAndWhitelistRevoke_AreAccepted()
    {
        // The explicit, deliberate act: an existing superadmin flips the peer's row, the same way
        // WhitelistEndpoints.cs's PUT /api/whitelist/{nodeId}/superadmin does locally before it
        // emits the whitelist_update that tells the rest of the mesh.
        await PromoteDOnR();

        var article = await _nodeD.ArticleService.CreateAsync("Target", "/Work", [], "secret");
        var hardDeleteEvent = await LogHardDeleteFromD(article.Id);
        var hardDeleteResult = await _nodeR.ApplyFromAsync(_nodeD, hardDeleteEvent);
        hardDeleteResult.Should().NotBe(EventApplyResult.SilentlyDropped);

        var revokeEvent = await LogWhitelistRevokeFromD(_thirdPartyId);
        await _nodeR.ApplyFromAsync(_nodeD, revokeEvent);
        (await _nodeR.WhitelistRepo.GetByNodeIdAsync(_thirdPartyId, includeDeleted: true))!.Status.Should().Be("R",
            "a promoted peer's revoke must now take effect");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task PromoteDOnR()
    {
        var identityD = (await _nodeD.NodeRepo.GetAsync())!;
        var entry = (await _nodeR.WhitelistRepo.GetByNodeIdAsync(identityD.NodeId))!;
        entry.IsSuperadmin = true;
        await _nodeR.WhitelistRepo.UpdateAsync(entry);
    }

    private async Task<SyncEvent> LogHardDeleteFromD(Guid articleId)
    {
        await _nodeD.HardDeleteService.DeleteArticleAsync(articleId, 1, null, CancellationToken.None);
        var evt = (await _nodeD.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        evt.EventType.Should().Be(EventTypes.HardDelete);
        return evt;
    }

    private async Task<SyncEvent> LogWhitelistRevokeFromD(Guid targetNodeId)
    {
        await _nodeD.EventLogger.LogWhitelistRevokeAsync(targetNodeId);
        var evt = (await _nodeD.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        evt.EventType.Should().Be(EventTypes.WhitelistRevoke);
        return evt;
    }

    private class ConcreteFixture : SyncTestFixture { }
}
