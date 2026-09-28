using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Plan 4.4: the blind node's TLS pin is replicated with its whitelist row, so every full node
/// dialling it — the phone as much as the PC that paired it — pins the same key.
/// </summary>
public class WhitelistTlsPinTests : SyncTestFixture
{
    private Guid _pc;
    private byte[] _pcKey = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var (pub, key) = Ed25519Signer.GenerateKeyPair();
        (_pc, _pcKey) = (Guid.NewGuid(), key);
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _pc, DisplayName = "PC", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
    }

    [Fact]
    public async Task AddCarriesThePin_UpdateChangesIt_AndAnUpdateWithoutOneKeepsIt()
    {
        var blind = BlindNodeId.NewId();
        await Apply(EventTypes.WhitelistAdd, 1, new WhitelistAddPayload(blind, "Blind", Convert.ToBase64String(new byte[32]),
            "https://blind.lan:5610", false, TlsSpki: "pin-1"));
        (await WhitelistRepo.GetByNodeIdAsync(blind))!.TlsSpki.Should().Be("pin-1");

        await Apply(EventTypes.WhitelistUpdate, 2, new WhitelistUpdatePayload(blind, null, null, TlsSpki: "pin-2"));
        (await WhitelistRepo.GetByNodeIdAsync(blind))!.TlsSpki.Should().Be("pin-2");

        await Apply(EventTypes.WhitelistUpdate, 3, new WhitelistUpdatePayload(blind, "https://blind.lan:5611", null));
        var row = (await WhitelistRepo.GetByNodeIdAsync(blind))!;
        row.TlsSpki.Should().Be("pin-2", "an update that says nothing about the pin leaves it");
        row.ApiAddress.Should().Be("https://blind.lan:5611");
    }

    private async Task Apply<T>(string type, long lamport, T payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = _pc, LamportTs = lamport, EventType = type,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(_pcKey, EventSignature.BuildPayload(evt));
        (await EventApplier.ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
    }
}
