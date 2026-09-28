using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The replica an Android blind node downloads carries its detached signature: the phone keeps both in the
/// body of its backups, and a restore from a backup checks the same two signatures a network restore does.
/// </summary>
public class BlindReplicaSignatureTests : IDisposable
{
    private readonly BlindNodeFactory _blind = new();

    public void Dispose() => _blind.Dispose();

    [Fact]
    public async Task TheReplica_CarriesItsDetachedSignature_ByTheServingNode()
    {
        var self = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var phone = BlindNodeId.NewId();
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = phone, DisplayName = "phone", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        using var http = _blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            _blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(phone, SyncProtocolVersion.Current));

        var resp = await http.GetAsync("/api/blind/replica");

        resp.EnsureSuccessStatusCode();
        var file = Path.Combine(_blind.DataPath, "replica-with-signature.tar.gz");
        await File.WriteAllBytesAsync(file, await resp.Content.ReadAsByteArrayAsync());
        var signature = Convert.FromBase64String(resp.Headers.GetValues("X-BMB-Snapshot-Signature").Single());
        var package = await _blind.Services.GetRequiredService<SnapshotService>()
            .ExtractVerifiedAsync(file, Path.Combine(_blind.DataPath, "replica-with-signature"));
        Ed25519Signer.Verify(self.Ed25519PublicKey,
                await SnapshotService.ComputeSignaturePayloadAsync(package.ManifestBytes, file, CancellationToken.None), signature)
            .Should().BeTrue("the detached signature covers the whole archive, by the node that served it");
    }
}
