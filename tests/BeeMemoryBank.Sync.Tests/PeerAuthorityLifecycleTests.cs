using BeeMemoryBank.Core.Models;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Two gaps in how peer authority begins and ends.
///
/// <para>Joining a network is authorised by the master password, and a joined node arrives with
/// <c>is_superadmin</c> set — full authority to revoke peers, hard-delete content everywhere and
/// restore the whole mesh. There was no way back: nothing in the API or the UI ever cleared that
/// bit, so "I no longer trust this machine" had exactly one answer, revoking it outright, which
/// also cuts it off from content it may legitimately still need.</para>
///
/// <para>And because <c>tbl_key_slot</c> is node-local — never synced, dropped from join snapshots —
/// changing the master password rewraps one node's slot and nothing else. Every other node keeps
/// accepting the old password at its own <c>/api/join</c>, which is the endpoint that grants mesh
/// membership. Nobody was told. These tests cover the demotion path and the notice.</para>
/// </summary>
public class PeerAuthorityLifecycleTests : IAsyncLifetime
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

        await _nodeB.WhitelistRepo.CreateAsync(Entry(identityA, isSuperadmin: true));
        await _nodeA.WhitelistRepo.CreateAsync(Entry(identityB, isSuperadmin: true));
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    [Fact]
    public async Task DemotingAPeer_PropagatesToTheOtherNodes()
    {
        // A demotion that reaches only the node where it was made is no demotion at all: every node
        // enforces the flag from its OWN whitelist row, so the peer keeps full authority everywhere
        // it was not applied.
        var thirdParty = await AddThirdPartyToBothAsync();

        await _nodeA.EventLogger.LogWhitelistUpdateAsync(
            thirdParty, apiAddress: null, displayName: null, isSuperadmin: false);

        var evt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        await _nodeB.ApplyFromAsync(_nodeA, evt);

        (await _nodeB.WhitelistRepo.GetByNodeIdAsync(thirdParty))!.IsSuperadmin
            .Should().BeFalse("the demotion has to reach every node to mean anything");
    }

    [Fact]
    public async Task ARenameFromAnOlderPeer_DoesNotDemoteAnyone()
    {
        // The forward-compat trap: a sender that predates demotion omits the field entirely. If
        // that deserialized to "false" rather than "not mentioned", every rename such a node makes
        // would quietly strip authority from its subject — the same class of bug that once cost a
        // 3-node cluster its is_superadmin bits on every sync.
        var thirdParty = await AddThirdPartyToBothAsync();

        await _nodeA.EventLogger.LogWhitelistUpdateAsync(
            thirdParty, apiAddress: null, displayName: "Renamed", isSuperadmin: null);

        var evt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        await _nodeB.ApplyFromAsync(_nodeA, evt);

        var applied = (await _nodeB.WhitelistRepo.GetByNodeIdAsync(thirdParty))!;
        applied.DisplayName.Should().Be("Renamed");
        applied.IsSuperadmin.Should().BeTrue("an event that says nothing about the flag must not change it");
    }

    [Fact]
    public async Task ADemotedPeer_CannotPromoteItselfBack()
    {
        // Demotion has to be a one-way door from the demoted node's side, or the remedy lasts until
        // its next sync round and its version of events then spreads to everyone else as fact.
        //
        // What actually stops it is the superadmin gate: it reads the CURRENT whitelist row, so the
        // moment the demotion lands the peer can no longer sign any cluster-state event, including
        // one about itself. EventApplier.ApplyWhitelistUpdateAsync carries a second, narrower rule
        // for the same move; this test pins the guarantee rather than the layer that provides it.
        var identityA = (await _nodeA.NodeRepo.GetAsync())!;

        await _nodeA.EventLogger.LogWhitelistUpdateAsync(
            identityA.NodeId, apiAddress: null, displayName: null, isSuperadmin: true);
        var selfPromotion = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();

        var entryOnB = (await _nodeB.WhitelistRepo.GetByNodeIdAsync(identityA.NodeId))!;
        entryOnB.IsSuperadmin = false;
        await _nodeB.WhitelistRepo.UpdateAsync(entryOnB);

        var apply = async () => await _nodeB.ApplyFromAsync(_nodeA, selfPromotion);
        await apply.Should().ThrowAsync<UnauthorizedAccessException>();

        (await _nodeB.WhitelistRepo.GetByNodeIdAsync(identityA.NodeId))!.IsSuperadmin
            .Should().BeFalse("a node does not get to hand itself back the authority it was stripped of");
    }

    [Fact]
    public async Task APasswordChangeElsewhere_LeavesAReadableNoticeAndNoKeyMaterial()
    {
        await _nodeA.EventLogger.LogMasterPasswordChangedAsync();
        var evt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();

        // The payload is the whole design decision: the alternative was shipping a slot wrapped
        // under the new password's KEK, which would have made the change propagate automatically at
        // the cost of putting key material on the wire.
        evt.Payload.Should().NotContain("dek", "the event must carry no key material");
        evt.Payload.Should().NotContain("salt");
        evt.Payload.Should().Contain("NodeA");

        await _nodeB.ApplyFromAsync(_nodeA, evt);

        var notice = await _nodeB.NodeRepo.GetMasterPasswordNoticeAsync();
        notice.Should().NotBeNull("NodeB still accepts the old password and has to be able to say so");
        notice!.Value.ByNode.Should().Be("NodeA");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static WhitelistEntry Entry(NodeIdentity identity, bool isSuperadmin) => new()
    {
        NodeId = identity.NodeId,
        DisplayName = identity.DisplayName,
        Ed25519PublicKey = identity.Ed25519PublicKey,
        Status = "A",
        IsSuperadmin = isSuperadmin,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    /// <summary>
    /// A peer both nodes know about, so a demotion has a subject that is neither the sender nor the
    /// receiver — the ordinary case, and the one the self-promotion rule must not interfere with.
    /// </summary>
    private async Task<Guid> AddThirdPartyToBothAsync()
    {
        var nodeId = Guid.NewGuid();
        var (pubKey, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
        var entry = new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = "NodeC",
            Ed25519PublicKey = pubKey,
            Status = "A",
            IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _nodeA.WhitelistRepo.CreateAsync(entry);
        await _nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = "NodeC",
            Ed25519PublicKey = pubKey,
            Status = "A",
            IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return nodeId;
    }

    private class ConcreteFixture : SyncTestFixture { }
}
