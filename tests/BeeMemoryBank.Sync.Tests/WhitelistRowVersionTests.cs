using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// <c>tbl_whitelist</c> was the last replicated table applied in arrival order rather than by
/// version. These tests pin down the rule that replaced that: plain LWW on (Lamport, node), the
/// same one every other replicated row uses.
///
/// <para>
/// Plain LWW is deliberate, not a compromise. Revoke has to stay undoable — re-adding a peer you
/// previously revoked is a workflow the UI offers — so "revoke always wins" would be wrong. What
/// must never happen is an add issued BEFORE the revoke undoing it, which is precisely the case
/// arrival order could not distinguish from a legitimate re-add.
/// </para>
/// </summary>
public class WhitelistRowVersionTests : IAsyncLifetime
{
    private SyncTestFixture _nodeA = null!;
    private SyncTestFixture _nodeB = null!;

    /// <summary>The peer both nodes are making decisions about. Never runs; only talked about.</summary>
    private static readonly Guid PeerC = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

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

    /// <summary>
    /// IsSuperadmin is required, not incidental: EventApplier refuses every whitelist event from a
    /// peer that is not superadmin in the local whitelist, so without it these tests would be
    /// asserting on a gate that never runs.
    /// </summary>
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
            IsSuperadmin = true,
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
    /// Adds peer C on <paramref name="node"/> the way the join endpoint does: log the event first,
    /// then write the row carrying the version that event published.
    /// </summary>
    private static async Task<RowVersion> AddPeerCAsync(SyncTestFixture node)
    {
        var now = DateTime.UtcNow;
        var entry = new WhitelistEntry
        {
            NodeId = PeerC,
            DisplayName = "PeerC",
            Ed25519PublicKey = new byte[32],
            ApiAddress = "https://peer-c.example",
            Status = "A",
            IsSuperadmin = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        var version = await node.EventLogger.LogWhitelistAddAsync(entry);
        entry.LamportTs = version.LamportTs;
        entry.SourceNodeId = version.SourceNodeId;
        await node.WhitelistRepo.CreateAsync(entry);
        return version;
    }

    private static SyncEvent LastEventOfType(IReadOnlyList<SyncEvent> events, string type) =>
        events.Last(e => e.EventType == type);

    /// <summary>
    /// The one that matters. An admin revokes a compromised peer; a node that was offline at the
    /// time still holds an OLDER whitelist_add for it and delivers it on catch-up. That add must
    /// lose.
    ///
    /// <para>
    /// Before this, the re-activation branch ran on any add for a revoked peer regardless of when
    /// it was issued, so the revoked node came back — and the admin's own UI showed it active with
    /// nothing to distinguish "my revoke was undone" from "my revoke never applied".
    /// </para>
    /// </summary>
    [Fact]
    public async Task StaleAdd_DoesNotResurrectARevokedPeer()
    {
        // A knows about C from before it went offline. Its clock stays where it is from here.
        await AddPeerCAsync(_nodeA);
        var staleAdd = LastEventOfType(await _nodeA.EventLogRepo.GetAfterSequenceAsync(0), EventTypes.WhitelistAdd);

        // B knows about C too, and has since done other work — so its revoke lands strictly above
        // A's add rather than tying with it, keeping the node-id tiebreak (a random Guid per run)
        // out of the assertion.
        await AddPeerCAsync(_nodeB);
        while (_nodeB.Clock.Current <= staleAdd.LamportTs) _nodeB.Clock.Tick();

        var revokeVersion = await _nodeB.EventLogger.LogWhitelistRevokeAsync(PeerC);
        await _nodeB.WhitelistRepo.RevokeAsync(PeerC, revokeVersion);
        revokeVersion.LamportTs.Should().BeGreaterThan(staleAdd.LamportTs);

        // A comes back online and its old add finally arrives.
        var result = await _nodeB.ApplyFromAsync(_nodeA, staleAdd);
        result.Should().Be(EventApplyResult.Applied,
            "the add must reach the version gate and be judged there, not be dropped for an unrelated reason");

        var onB = await _nodeB.WhitelistRepo.GetByNodeIdAsync(PeerC, includeDeleted: true);
        onB!.Status.Should().Be("R", "an add issued before the revoke must not put the peer back into the mesh");
        onB.LamportTs.Should().Be(revokeVersion.LamportTs, "the row must still be attributed to the revoke that won");
    }

    /// <summary>
    /// The other half, and the reason the rule is plain LWW rather than "revoke always wins":
    /// re-adding a peer you previously revoked has to keep working, or the fix above would have
    /// quietly removed a feature instead of closing a hole.
    /// </summary>
    [Fact]
    public async Task NewerAdd_DoesReactivateARevokedPeer()
    {
        await AddPeerCAsync(_nodeB);
        var revokeVersion = await _nodeB.EventLogger.LogWhitelistRevokeAsync(PeerC);
        await _nodeB.WhitelistRepo.RevokeAsync(PeerC, revokeVersion);

        // A decides to re-add C, after B's revoke.
        while (_nodeA.Clock.Current <= revokeVersion.LamportTs) _nodeA.Clock.Tick();
        await AddPeerCAsync(_nodeA);
        var freshAdd = LastEventOfType(await _nodeA.EventLogRepo.GetAfterSequenceAsync(0), EventTypes.WhitelistAdd);
        freshAdd.LamportTs.Should().BeGreaterThan(revokeVersion.LamportTs);

        await _nodeB.ApplyFromAsync(_nodeA, freshAdd);

        var onB = await _nodeB.WhitelistRepo.GetByNodeIdAsync(PeerC, includeDeleted: true);
        onB!.Status.Should().Be("A", "an add newer than the revoke is the newer decision and must win");
        onB.LamportTs.Should().Be(freshAdd.LamportTs);
    }

    /// <summary>
    /// The key is bound to NodeId at first registration and a winning add must still never replace
    /// it — otherwise versioning the row would have turned "newest write wins" into a way to take
    /// over an existing NodeId with a fresh key.
    /// </summary>
    [Fact]
    public async Task WinningAdd_StillNeverReplacesTheEd25519Key()
    {
        var original = new byte[32];
        original[0] = 0x11;

        var now = DateTime.UtcNow;
        var entry = new WhitelistEntry
        {
            NodeId = PeerC,
            DisplayName = "PeerC",
            Ed25519PublicKey = original,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        };
        var addVersion = await _nodeB.EventLogger.LogWhitelistAddAsync(entry);
        entry.LamportTs = addVersion.LamportTs;
        entry.SourceNodeId = addVersion.SourceNodeId;
        await _nodeB.WhitelistRepo.CreateAsync(entry);

        var revokeVersion = await _nodeB.EventLogger.LogWhitelistRevokeAsync(PeerC);
        await _nodeB.WhitelistRepo.RevokeAsync(PeerC, revokeVersion);

        // A re-adds C with a DIFFERENT key, at a newer version so it wins the comparison.
        while (_nodeA.Clock.Current <= revokeVersion.LamportTs) _nodeA.Clock.Tick();
        await AddPeerCAsync(_nodeA);   // AddPeerCAsync uses an all-zero key
        var freshAdd = LastEventOfType(await _nodeA.EventLogRepo.GetAfterSequenceAsync(0), EventTypes.WhitelistAdd);

        await _nodeB.ApplyFromAsync(_nodeA, freshAdd);

        var onB = await _nodeB.WhitelistRepo.GetByNodeIdAsync(PeerC, includeDeleted: true);
        onB!.Status.Should().Be("A");
        onB.Ed25519PublicKey.Should().Equal(original,
            "winning the version comparison decides whether the row is touched, never whether the key may be swapped");
    }

    /// <summary>
    /// Ordinary divergence rather than a security hole, but the same cause: two admins changing the
    /// same peer's address resolved to whichever event happened to arrive last, and nothing ever
    /// recompares a whitelist row, so the nodes stayed split.
    /// </summary>
    [Fact]
    public async Task ConcurrentUpdates_ResolveTheSameWayOnBothNodes()
    {
        // Both nodes start from the same add, replicated.
        await AddPeerCAsync(_nodeA);
        var add = LastEventOfType(await _nodeA.EventLogRepo.GetAfterSequenceAsync(0), EventTypes.WhitelistAdd);
        await _nodeB.ApplyFromAsync(_nodeA, add);

        // Each admin changes C's address, neither having seen the other's change.
        var versionA = await _nodeA.EventLogger.LogWhitelistUpdateAsync(PeerC, "https://from-a.example", null);
        var entryA = (await _nodeA.WhitelistRepo.GetByNodeIdAsync(PeerC))!;
        entryA.ApiAddress = "https://from-a.example";
        entryA.LamportTs = versionA.LamportTs;
        entryA.SourceNodeId = versionA.SourceNodeId;
        await _nodeA.WhitelistRepo.UpdateAsync(entryA);

        var versionB = await _nodeB.EventLogger.LogWhitelistUpdateAsync(PeerC, "https://from-b.example", null);
        var entryB = (await _nodeB.WhitelistRepo.GetByNodeIdAsync(PeerC))!;
        entryB.ApiAddress = "https://from-b.example";
        entryB.LamportTs = versionB.LamportTs;
        entryB.SourceNodeId = versionB.SourceNodeId;
        await _nodeB.WhitelistRepo.UpdateAsync(entryB);

        var updateFromA = LastEventOfType(await _nodeA.EventLogRepo.GetAfterSequenceAsync(0), EventTypes.WhitelistUpdate);
        var updateFromB = LastEventOfType(await _nodeB.EventLogRepo.GetAfterSequenceAsync(0), EventTypes.WhitelistUpdate);

        await _nodeA.ApplyFromAsync(_nodeB, updateFromB);
        await _nodeB.ApplyFromAsync(_nodeA, updateFromA);

        var finalA = (await _nodeA.WhitelistRepo.GetByNodeIdAsync(PeerC))!;
        var finalB = (await _nodeB.WhitelistRepo.GetByNodeIdAsync(PeerC))!;

        finalA.ApiAddress.Should().Be(finalB.ApiAddress, "both nodes must land on the same address");
        finalA.LamportTs.Should().Be(finalB.LamportTs);
        finalA.SourceNodeId.Should().Be(finalB.SourceNodeId);

        // And on the one the comparator picks, not on whichever arrived second.
        var expected = ConflictResolver.IncomingWins(versionA, versionB) ? versionB : versionA;
        finalA.Version.Should().Be(expected);
    }

    /// <summary>
    /// A row revoked before migration 021 sits at Lamport 0, so ANY incoming add outranks it
    /// arithmetically. That must not be enough to bring the node back: nothing in the row says
    /// whether the revoke was newer than the add, and the safe reading of "unknown" is that it was —
    /// otherwise a peer that never heard about a revocation can undo it just by being out of date.
    ///
    /// <para>
    /// The refusal is deliberately narrow: only a REMOTE add is blocked. An admin re-adding the node
    /// locally writes the row with a fresh version and is unaffected, which is the distinction that
    /// matters — a local action is a decision, an arriving old event is not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RemoteAdd_CannotResurrectARevocationThatPredatesVersioning()
    {
        // A legacy row: revoked, no version, exactly as migration 021's DEFAULT 0 leaves it.
        var now = DateTime.UtcNow;
        await _nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = PeerC,
            DisplayName = "PeerC",
            Ed25519PublicKey = new byte[32],
            Status = "R",
            CreatedAt = now,
            UpdatedAt = now,
            LamportTs = 0,
            SourceNodeId = null
        });

        // A perfectly ordinary add from a trusted superadmin peer, at any version at all.
        await AddPeerCAsync(_nodeA);
        var add = LastEventOfType(await _nodeA.EventLogRepo.GetAfterSequenceAsync(0), EventTypes.WhitelistAdd);
        add.LamportTs.Should().BeGreaterThan(0, "the incoming add outranks the unversioned row arithmetically");

        await _nodeB.ApplyFromAsync(_nodeA, add);

        var onB = await _nodeB.WhitelistRepo.GetByNodeIdAsync(PeerC, includeDeleted: true);
        onB!.Status.Should().Be("R", "an unversioned revocation must not be undone by a remote add");
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
