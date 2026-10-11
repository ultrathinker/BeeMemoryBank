using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 9.4): what the applier does on a blind host with the relay opener. A sealed event is
/// stored relayed and opens nothing; a control event applies; clear content is materialised before this host applied the
/// activation and dropped after it.
/// </summary>
public class RelayHostPipelineTests : IAsyncLifetime
{
    private readonly RelayFixture _blind = new();
    private readonly StepAAuthor _author = new();

    public async Task InitializeAsync()
    {
        await _blind.InitializeAsync();
        var (pub, _) = Ed25519Signer.GenerateKeyPair();
        await _blind.NodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = pub, Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = 2, CreatedAt = DateTime.UtcNow
        });
        await _blind.WhitelistRepo.CreateAsync(_author.Entry("Author", superadmin: true));
    }

    public Task DisposeAsync() => _blind.DisposeAsync();

    [Fact]
    public async Task ASealedEvent_IsRelayed_ControlApplies_ClearContentIsDroppedOnceActivated()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        (await _blind.EventApplier.ApplyAsync(_author.NewKey(dek))).Should().Be(EventApplyResult.Applied, "the key record is stored as recovery material");
        var folderId = Guid.NewGuid();
        var sealedEvent = _author.Sealed(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(folderId, "/Sealed", "Sealed", null!));

        (await _blind.EventApplier.ApplyAsync(sealedEvent)).Should().Be(EventApplyResult.Applied);
        using (var conn = _blind.Factory.CreateConnection())
        {
            (await conn.ExecuteScalarAsync<long>("SELECT apply_state FROM tbl_event WHERE event_id = @Id COLLATE NOCASE",
                new { Id = sealedEvent.EventId.ToString() })).Should().Be(EventApplyStates.Relayed);
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_folder WHERE path = '/Sealed'")).Should().Be(0);
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_key")).Should().Be(1);
        }
        (await new HeldEventStatus(_blind.Factory).AnyHeldAsync()).Should().BeFalse("a blind host never holds");

        // Before this host applied the activation, clear content is materialised as 2.5.1 did.
        var before = _author.Clear(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(Guid.NewGuid(), "/Before", "Before", null!));
        (await _blind.EventApplier.ApplyAsync(before)).Should().Be(EventApplyResult.Applied);

        (await _blind.EventApplier.ApplyAsync(_author.Activation())).Should().Be(EventApplyResult.Applied);
        _blind.ProtocolPolicy.SealsMetadata.Should().BeTrue();
        var after = _author.Clear(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(Guid.NewGuid(), "/After", "After", null!));
        (await _blind.EventApplier.ApplyAsync(after)).Should().Be(EventApplyResult.SilentlyDropped, "accepted, not stored");
        (await _blind.EventLogRepo.ExistsAsync(after.EventId)).Should().BeFalse();
        using (var conn = _blind.Factory.CreateConnection())
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_folder WHERE path = '/After'")).Should().Be(0);
    }

    private sealed class RelayFixture : SyncTestFixture
    {
        protected override IEnvelopeOpener CreateEnvelopeOpener() => new RelayEnvelopeOpener(ProtocolPolicy);
    }
}
