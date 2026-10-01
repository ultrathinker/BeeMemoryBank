using System.Net;
using System.Net.Http.Headers;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        var (cache, time) = NewCache();
        var first = await cache.GetAsync(producerIsSuperadmin: false, CancellationToken.None);
        File.Exists(first.FilePath).Should().BeTrue();
        File.Exists(first.FilePath + ".sig").Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(30));

        var replacement = await cache.GetAsync(producerIsSuperadmin: false, CancellationToken.None);

        replacement.FilePath.Should().NotBe(first.FilePath);
        File.Exists(first.FilePath).Should().BeFalse("the cache must not leave an obsolete package in the snapshot directory");
        File.Exists(first.FilePath + ".sig").Should().BeFalse("the obsolete detached signature must be removed with its package");
        await cache.DisposeAsync();
    }

    [Fact]
    public async Task ReplicaCache_KeepsASupersededPackageUntilItsResponseLeaseCompletes()
    {
        var (cache, time) = NewCache();
        var lease = await cache.AcquireAsync(producerIsSuperadmin: false, CancellationToken.None);
        var first = lease.Package;
        time.Advance(TimeSpan.FromMinutes(30));

        _ = await cache.GetAsync(producerIsSuperadmin: false, CancellationToken.None);

        File.Exists(first.FilePath).Should().BeTrue("the active response still needs to stream this package");
        await lease.DisposeAsync();
        File.Exists(first.FilePath).Should().BeFalse("the package becomes reclaimable as soon as its last response completes");
        await cache.DisposeAsync();
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
        var (cache, time) = NewCache();
        var lease = await cache.AcquireAsync(producerIsSuperadmin: false, CancellationToken.None);
        var retired = lease.Package;
        time.Advance(TimeSpan.FromMinutes(30));
        _ = await cache.GetAsync(producerIsSuperadmin: false, CancellationToken.None);

        await cache.DisposeAsync();

        File.Exists(retired.FilePath).Should().BeFalse("process shutdown must not leave an abandoned retired package");
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task ReplicaCache_ExpirySweepKeepsAnActiveLeaseUntilTheResponseCompletes()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var cache = new BlindReplicaPackageCache(_blind.Services.GetRequiredService<IServiceScopeFactory>(), time);
        var lease = await cache.AcquireAsync(producerIsSuperadmin: false, CancellationToken.None);
        var expired = lease.Package;
        time.Advance(TimeSpan.FromMinutes(30));

        await cache.SweepExpiredAsync(CancellationToken.None);

        File.Exists(expired.FilePath).Should().BeTrue("an in-flight HTTP response still owns the package file");
        await lease.DisposeAsync();
        File.Exists(expired.FilePath).Should().BeFalse("the expired package is reclaimed after its final response completes");
        await cache.DisposeAsync();
    }

    [Fact]
    public void ReplicaCache_IsRegisteredAsAHostedExpiryService()
    {
        _blind.Services.GetServices<IHostedService>().OfType<BlindReplicaPackageCache>().Should().ContainSingle();
    }

    [Fact]
    public async Task ReplicaCache_StartRemovesOnlyItsOwnDirectorysOrphanFiles()
    {
        var snapshots = _blind.Services.GetRequiredService<SnapshotService>();
        var directory = ReplicaDirectory(snapshots);
        Directory.CreateDirectory(directory);
        var orphan = Path.Combine(directory, "bmb-snapshot-orphan.tar.gz");
        await File.WriteAllBytesAsync(orphan, [1]);
        await File.WriteAllBytesAsync(orphan + ".sig", [2]);
        // Older than a package's lease: nobody can still be streaming it.
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(orphan + ".sig", DateTime.UtcNow.AddHours(-2));
        Directory.CreateDirectory(snapshots.SnapshotsDir);
        var realSnapshot = Path.Combine(snapshots.SnapshotsDir, "bmb-snapshot-keep-me.tar.gz");
        await File.WriteAllBytesAsync(realSnapshot, [3]);
        await using var cache = new BlindReplicaPackageCache(
            _blind.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        await cache.StartAsync(CancellationToken.None);

        File.Exists(orphan).Should().BeFalse();
        File.Exists(orphan + ".sig").Should().BeFalse();
        File.Exists(realSnapshot).Should().BeTrue("the sweep must never touch the real snapshots directory");
    }

    [Fact]
    public async Task ReplicaCache_StartKeepsAPackageAnotherProcessMayStillBeServing()
    {
        // Two API processes can share one data directory: a package the other one wrote a moment ago is
        // inside its lease and may be mid-response, so a start-up sweep must not delete it.
        var snapshots = _blind.Services.GetRequiredService<SnapshotService>();
        var directory = ReplicaDirectory(snapshots);
        Directory.CreateDirectory(directory);
        var live = Path.Combine(directory, "bmb-snapshot-live-elsewhere.tar.gz");
        var stale = Path.Combine(directory, "bmb-snapshot-stale.tar.gz");
        await File.WriteAllBytesAsync(live, [1]);
        await File.WriteAllBytesAsync(live + ".sig", [2]);
        await File.WriteAllBytesAsync(stale, [3]);
        await File.WriteAllBytesAsync(stale + ".sig", [4]);
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-31));
        await using var cache = new BlindReplicaPackageCache(
            _blind.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        await cache.StartAsync(CancellationToken.None);

        File.Exists(live).Should().BeTrue("it was written within the lease time");
        File.Exists(live + ".sig").Should().BeTrue("its signature belongs to the package that stays");
        File.Exists(stale).Should().BeFalse("older than the lease, nobody can still be streaming it");
        File.Exists(stale + ".sig").Should().BeFalse();
    }

    [Fact]
    public async Task ReplicaCache_BuildsPackagesInItsOwnDirectoryNotInTheSnapshotsDirectory()
    {
        await using var cache = new BlindReplicaPackageCache(
            _blind.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        var package = await cache.GetAsync(producerIsSuperadmin: false, CancellationToken.None);

        var snapshots = _blind.Services.GetRequiredService<SnapshotService>();
        Path.GetDirectoryName(package.FilePath).Should().Be(ReplicaDirectory(snapshots));
        snapshots.List().Should().BeEmpty("nothing of the replica package may be written where the snapshot code looks");
    }

    [Fact]
    public async Task ReplicaCache_AsksTheSuperadminQuestionOnlyWhenItBuilds_AndServesTheCachedPackageAfterAFlip()
    {
        var (cache, time) = NewCache();
        var asked = 0;
        Func<bool, Func<CancellationToken, Task<bool>>> answer = value => _ =>
        {
            asked++;
            return Task.FromResult(value);
        };

        var first = await cache.GetAsync(answer(true), CancellationToken.None);
        var retry = await cache.GetAsync(answer(false), CancellationToken.None);

        asked.Should().Be(1, "the second request is answered from the cache without the peer round");
        retry.FilePath.Should().Be(first.FilePath, "a flaky pre-flight answer must not swap the bytes a resume continues");
        retry.Sha256.Should().Be(first.Sha256);
        retry.Manifest.Whitelist.Single(p => p.NodeId == retry.Manifest.ProducerNodeId).IsSuperadmin
            .Should().BeTrue("the producer row keeps what the build was told");

        time.Advance(TimeSpan.FromMinutes(30));
        var rebuilt = await cache.GetAsync(answer(false), CancellationToken.None);

        asked.Should().Be(2, "an expired package is rebuilt, and only then is the question asked again");
        rebuilt.FilePath.Should().NotBe(first.FilePath);
        rebuilt.Manifest.Whitelist.Single(p => p.NodeId == rebuilt.Manifest.ProducerNodeId).IsSuperadmin.Should().BeFalse();
        await cache.DisposeAsync();
    }

    private (BlindReplicaPackageCache Cache, AdjustableTimeProvider Time) NewCache()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        return (new BlindReplicaPackageCache(_blind.Services.GetRequiredService<IServiceScopeFactory>(), time), time);
    }

    private static string ReplicaDirectory(SnapshotService snapshots) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snapshots.SnapshotsDir))!, "blind-replica");

    [Fact]
    public async Task ReplicaPackage_IsNeitherListedNorCountedNorDeletedByTheSnapshotRetention()
    {
        var snapshots = _blind.Services.GetRequiredService<SnapshotService>();
        var oldest = (await snapshots.CreateAsync(encryptDb: false)).FileName;
        var middle = (await snapshots.CreateAsync(encryptDb: false)).FileName;
        var newest = (await snapshots.CreateAsync(encryptDb: false)).FileName;
        File.SetLastWriteTimeUtc(snapshots.GetSnapshotPath(oldest), DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(snapshots.GetSnapshotPath(middle), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(snapshots.GetSnapshotPath(newest), DateTime.UtcNow.AddHours(-1));
        var cache = _blind.Services.GetRequiredService<BlindReplicaPackageCache>();
        var package = await cache.GetAsync(producerIsSuperadmin: false, CancellationToken.None);
        // Counted, not matched by name: the package and a snapshot made in the same second share a name shape.
        snapshots.List().Should().HaveCount(3,
            "the replica package is internal and must not show up in GET /api/snapshots");
        snapshots.PruneOldSnapshots(keepCount: 2).Should().Be(1,
            "only the three real snapshots are ranked, so exactly the oldest one goes");

        File.Exists(package.FilePath).Should().BeTrue("retention must not delete a leased replica package");
        File.Exists(package.FilePath + ".sig").Should().BeTrue();
        File.Exists(snapshots.GetSnapshotPath(newest)).Should().BeTrue("the replica must not take a retention slot");
        File.Exists(snapshots.GetSnapshotPath(middle)).Should().BeTrue();
        // Delete reads names from the snapshots directory only; names are not compared across the two
        // directories because a package and a snapshot made in the same second share one.
        foreach (var info in snapshots.List()) snapshots.Delete(info.FileName);
        File.Exists(package.FilePath).Should().BeTrue("the public delete route must not reach the replica package");
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
