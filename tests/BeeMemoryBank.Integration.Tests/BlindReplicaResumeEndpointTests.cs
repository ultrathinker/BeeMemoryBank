using System.Net;
using System.Net.Http.Headers;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

public sealed class BlindReplicaResumeEndpointTests : IDisposable
{
    private readonly BlindNodeFactory _blind = new();

    public void Dispose() => _blind.Dispose();

    [Fact]
    public async Task ReplicaEndpoint_ReusesTheSignedPackageForARangeResume()
    {
        var phone = BlindNodeId.NewId();
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = phone,
            DisplayName = "phone",
            Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Status = WhitelistStatuses.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var token = _blind.Services.GetRequiredService<SyncTokenStore>()
            .IssueToken(phone, SyncProtocolVersion.Current);
        using var http = _blind.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/blind/replica");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Range = new RangeHeaderValue(0, 63);

        using var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange.Should().NotBeNull();
        response.Headers.GetValues("X-BMB-Package-Sha256").Should().ContainSingle();
        response.Headers.GetValues("X-BMB-Snapshot-Signature").Should().ContainSingle();
    }
}
