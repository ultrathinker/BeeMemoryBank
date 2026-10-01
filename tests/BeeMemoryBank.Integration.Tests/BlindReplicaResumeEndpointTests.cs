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

        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/api/blind/replica");
        firstRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var firstResponse = await http.SendAsync(firstRequest);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstHash = firstResponse.Headers.GetValues("X-BMB-Package-Sha256").Should().ContainSingle().Which;
        var firstSignature = firstResponse.Headers.GetValues("X-BMB-Snapshot-Signature").Should().ContainSingle().Which;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/blind/replica");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Range = new RangeHeaderValue(0, 63);

        using var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange.Should().NotBeNull();
        response.Headers.GetValues("X-BMB-Package-Sha256").Should().ContainSingle().Which.Should().Be(firstHash,
            "a range retry must resume the exact archive from the first response");
        response.Headers.GetValues("X-BMB-Snapshot-Signature").Should().ContainSingle().Which.Should().Be(firstSignature,
            "the detached signature must describe the exact archive the range resumes");
    }

    [Fact]
    public async Task ReplicaCache_DeletesAnUnusedSupersededPackage()
    {
        var cache = _blind.Services.GetRequiredService<BlindReplicaPackageCache>();
        var first = await cache.GetAsync(producerIsSuperadmin: false, CancellationToken.None);
        File.Exists(first.FilePath).Should().BeTrue();
        File.Exists(first.FilePath + ".sig").Should().BeTrue();

        var replacement = await cache.GetAsync(producerIsSuperadmin: true, CancellationToken.None);

        replacement.FilePath.Should().NotBe(first.FilePath);
        File.Exists(first.FilePath).Should().BeFalse("the cache must not leave an obsolete package in the snapshot directory");
        File.Exists(first.FilePath + ".sig").Should().BeFalse("the obsolete detached signature must be removed with its package");
    }

    [Fact]
    public async Task ReplicaCache_KeepsASupersededPackageUntilItsResponseLeaseCompletes()
    {
        var cache = _blind.Services.GetRequiredService<BlindReplicaPackageCache>();
        var lease = await cache.AcquireAsync(producerIsSuperadmin: false, CancellationToken.None);
        var first = lease.Package;

        _ = await cache.GetAsync(producerIsSuperadmin: true, CancellationToken.None);

        File.Exists(first.FilePath).Should().BeTrue("the active response still needs to stream this package");
        await lease.DisposeAsync();
        File.Exists(first.FilePath).Should().BeFalse("the package becomes reclaimable as soon as its last response completes");
    }

    [Fact]
    public async Task ReplicaCache_DisposeDeletesTheCurrentPackage()
    {
        var cache = _blind.Services.GetRequiredService<BlindReplicaPackageCache>();
        var package = await cache.GetAsync(producerIsSuperadmin: false, CancellationToken.None);

        await cache.DisposeAsync();

        File.Exists(package.FilePath).Should().BeFalse();
        File.Exists(package.FilePath + ".sig").Should().BeFalse();
    }

    [Fact]
    public async Task ReplicaCache_DisposeAlsoDeletesARetiredPackageWithAnActiveLease()
    {
        var cache = _blind.Services.GetRequiredService<BlindReplicaPackageCache>();
        var lease = await cache.AcquireAsync(producerIsSuperadmin: false, CancellationToken.None);
        var retired = lease.Package;
        _ = await cache.GetAsync(producerIsSuperadmin: true, CancellationToken.None);

        await cache.DisposeAsync();

        File.Exists(retired.FilePath).Should().BeFalse("process shutdown must not leave an abandoned retired package");
        await lease.DisposeAsync();
    }
}
