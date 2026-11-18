using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using FluentAssertions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// a peer that re-keyed loses what a closed node queued for it in the boot relay inbox, as a
/// revoked one does - its old-key events and position reports.
/// </summary>
public class RekeyedPeerRelayInboxTests : SyncTestFixture
{
    private readonly RecordingInbox _inbox = new();

    protected override IRelayInbox? CreateRelayInbox() => _inbox;

    [Fact]
    public async Task Applying_a_peers_rotation_drops_its_queued_relay_envelopes()
    {
        var (oldPub, oldSeed) = Ed25519Signer.GenerateKeyPair();
        var peer = Guid.NewGuid();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = peer, DisplayName = "Office", Ed25519PublicKey = oldPub, Status = "A", IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var (_, newSeed) = Ed25519Signer.GenerateKeyPair();
        var (rotation, _) = NodeKeyRotationEvents.Rotation(peer, oldPub, b => Ed25519Signer.Sign(oldSeed, b), newSeed, 1, 100, 0, [], "test",
            SyncProtocolVersion.Current, "Office");

        (await EventApplier.ApplyAsync(rotation)).Should().Be(EventApplyResult.Applied);

        _inbox.DeletedFor.Should().Equal(peer);
    }

    private sealed class RecordingInbox : IRelayInbox
    {
        public List<Guid> DeletedFor { get; } = [];

        public Task DeleteForPeerAsync(Guid peer, CancellationToken ct = default)
        {
            DeletedFor.Add(peer);
            return Task.CompletedTask;
        }

        public Task<long> AppendAsync(SyncEvent evt, Guid? originPeer, long? originSeq, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RelayEventRow>> ReadAfterAsync(long afterRelaySeq, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RelayInboxSummary> GetSummaryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreBlobAsync(string hash, byte[] ciphertext, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> GetBlobAsync(string hash, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListBlobHashesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteBlobsAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordReportAsync(Guid peer, long sequence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(Guid Peer, long Sequence)>> ListReportsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearReportsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
