using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The joiner's half of the revoked peers a join hands over (<see cref="JoinRevocations"/>): each becomes a revoked row at its revoke's version,
/// and what the host's word cannot be trusted with (a version out of range, a key that is not a key, this node, the host, a row the same answer
/// made active) is left out.
/// </summary>
public class JoinRevocationsTests : SyncTestFixture
{
    private static readonly Guid Self = Guid.Parse("11111111-0000-4000-8000-000000000001");
    private static readonly Guid Host = Guid.Parse("11111111-0000-4000-8000-000000000002");
    private static readonly Guid Author = Guid.Parse("11111111-0000-4000-8000-000000000003");

    [Fact]
    public async Task EachRevokedPeer_BecomesARevokedRowAtTheRevokesVersion_WithItsKey()
    {
        var key = Ed25519Signer.GenerateKeyPair().publicKey;
        var peer = Guid.NewGuid();
        var keyless = Guid.NewGuid();

        var written = await JoinRevocations.ImportAsync(WhitelistRepo,
            [new JoinRevokedPeer(peer, 7365, Author, Convert.ToBase64String(key)), new JoinRevokedPeer(keyless, 7498, Author)],
            Self, Host, NullLogger.Instance);

        written.Should().Be(2);
        var row = await WhitelistRepo.GetByNodeIdAsync(peer, includeDeleted: true);
        (row!.Status, row.Version).Should().Be((WhitelistStatuses.Revoked, new RowVersion(7365, Author)));
        row.Ed25519PublicKey.Should().Equal(key);
        (await WhitelistRepo.GetByNodeIdAsync(keyless, includeDeleted: true))!.Ed25519PublicKey.Should().BeEmpty();
        (await WhitelistRepo.GetAllActiveAsync()).Should().BeEmpty("a revoked peer is never a peer");
    }

    [Fact]
    public async Task WhatTheHostsWordCannotBeTrustedWith_IsLeftOut()
    {
        var active = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = active, DisplayName = "Active", Ed25519PublicKey = new byte[32], Status = WhitelistStatuses.Active, CreatedAt = now, UpdatedAt = now
        });
        var tooHigh = Guid.NewGuid();
        var negative = Guid.NewGuid();
        var badKey = Guid.NewGuid();

        var written = await JoinRevocations.ImportAsync(WhitelistRepo,
        [
            new JoinRevokedPeer(Self, 5, Author), new JoinRevokedPeer(Host, 5, Author), new JoinRevokedPeer(Guid.Empty, 5, Author),
            new JoinRevokedPeer(active, 5, Author),
            new JoinRevokedPeer(tooHigh, SnapshotJoin.MaxAcceptedLamport, Author), new JoinRevokedPeer(negative, -1, Author),
            new JoinRevokedPeer(badKey, 5, Author, "not base64 at all"),
        ], Self, Host, NullLogger.Instance);

        written.Should().Be(1, "only the peer with a bad key is written, without the key");
        (await WhitelistRepo.GetByNodeIdAsync(active))!.Status.Should().Be(WhitelistStatuses.Active, "a row the answer made active is not turned");
        (await WhitelistRepo.GetByNodeIdAsync(tooHigh, includeDeleted: true)).Should().BeNull();
        (await WhitelistRepo.GetByNodeIdAsync(negative, includeDeleted: true)).Should().BeNull();
        foreach (var id in new[] { Self, Host, Guid.Empty })
            (await WhitelistRepo.GetByNodeIdAsync(id, includeDeleted: true)).Should().BeNull();
        (await WhitelistRepo.GetByNodeIdAsync(badKey, includeDeleted: true))!.Ed25519PublicKey.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnswerWithoutRevokedPeers_AsFromA275Host_WritesNothing()
    {
        (await JoinRevocations.ImportAsync(WhitelistRepo, null, Self, Host, NullLogger.Instance)).Should().Be(0);
    }

    /// <summary>The host's version of each concern (migration 045), each one held to what a join accepts; the row's own is the newest.</summary>
    [Fact]
    public void AnInheritedPeer_TakesTheHostsVersions_WhenTheyAreOnesAJoinAccepts()
    {
        var row = new WhitelistEntry();
        JoinRevocations.Inherit(row, new JoinPeerVersions(new JoinVersion(6636, Author), new JoinVersion(-5, Author), new JoinVersion(0, Author),
            new JoinVersion(SnapshotJoin.MaxAcceptedLamport, Author), new JoinVersion(6700, Author)));
        row.VersionOf(WhitelistConcerns.Membership).Should().Be(new RowVersion(6636, Author));
        foreach (var refused in new[] { WhitelistConcerns.Superadmin, WhitelistConcerns.Name, WhitelistConcerns.Address })
            row.VersionOf(refused).Should().Be(new RowVersion(0, Guid.Empty), $"{refused}: a version outside what a join accepts is no version");
        row.VersionOf(WhitelistConcerns.Tls).Should().Be(new RowVersion(6700, Author));
        row.Version.Should().Be(new RowVersion(6700, Author));

        var fromOldHost = new WhitelistEntry();
        JoinRevocations.Inherit(fromOldHost, null);
        fromOldHost.Version.Should().Be(new RowVersion(0, Guid.Empty), "a 2.7.5 host sends none");
        foreach (var concern in WhitelistConcernSet.Each) fromOldHost.VersionOf(concern).LamportTs.Should().Be(0);
    }

    /// <summary>The host's half: a row is sent with the version and the key it has, and a key that is not 32 bytes is not sent at all.</summary>
    [Fact]
    public void TheHostSendsARevokedRow_WithItsVersionAndKey()
    {
        var key = Ed25519Signer.GenerateKeyPair().publicKey;
        var row = new WhitelistEntry { NodeId = Guid.NewGuid(), Ed25519PublicKey = key, Status = WhitelistStatuses.Revoked };
        row.SetAllVersions(new RowVersion(42, Author));
        JoinRevokedPeer.From(row).Should().Be(new JoinRevokedPeer(row.NodeId, 42, Author, Convert.ToBase64String(key)));
        var tombstone = new WhitelistEntry { NodeId = row.NodeId, Status = WhitelistStatuses.Revoked };
        tombstone.SetAllVersions(new RowVersion(42, Guid.Empty));
        JoinRevokedPeer.From(tombstone).Ed25519PublicKeyB64.Should().BeNull("a tombstone has no key to send");
    }
}
