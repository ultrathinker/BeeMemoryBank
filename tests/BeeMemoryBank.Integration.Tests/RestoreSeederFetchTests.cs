using System.Net.Http.Json;
using System.Security.Cryptography;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Drives <see cref="RestoreInitiatorService.DownloadFromWhitelistedSeederAsync"/> — the
/// challenge/authenticate + download portion of fetching a network-restore snapshot from a
/// whitelisted peer — against a REAL peer server.
///
/// Scope note: the full restore flow (<c>AcceptRestoreAsync</c> → pre-restore backup →
/// <c>ExecuteDownloadAndApplyAsync</c> → hash check → <c>SnapshotService.ApplyNetworkRestoreAsync</c>)
/// destructively replaces the entire local database and requires a schema-compatible signed
/// snapshot. Driving that end-to-end was judged impractical for this test seam: it would mean
/// building a full valid RESTORE_NETWORK snapshot AND accepting that the test destroys node B's
/// database as a side effect, for coverage of code (the apply path) that was not part of the V1
/// regression this suite is guarding against. Per instructions, this narrows deliberately to the
/// handshake + download portion, called out explicitly here rather than silently.
///
/// <see cref="RestoreInitiatorService.DownloadFromWhitelistedSeederAsync"/> is `internal` (see
/// BeeMemoryBank.Api.csproj's InternalsVisibleTo) purely so this test can call it directly — it was
/// extracted, as a pure cut/paste with no behavior change, from the private
/// <c>ExecuteDownloadAndApplyAsync</c> so the handshake could be isolated from the destructive
/// apply step that follows it.
///
/// Separate finding while building this test, NOT fixed here (out of scope — unrelated to the
/// V1/V2 signing regression, and not needed to write these tests): the whitelisted-peer candidate
/// URL uses a literal loopback IP, not a hostname. A DNS-hostname ApiAddress instead goes through
/// ResolveAndPinSeederAsync's DNS-resolution branch, which rebuilds the URL with
/// <c>new UriBuilder(u).Uri.ToString()</c> — this normalizes a bare "scheme://host:port" (no path)
/// to "scheme://host:port/" (trailing slash). The caller then appends "/api/sync/challenge" etc.
/// directly, producing a double-slash path ("…//api/sync/challenge") that real ASP.NET Core
/// routing 404s on. Any peer whose whitelist ApiAddress is a hostname rather than a raw IP
/// (mDNS/.local names, Tailscale MagicDNS, dynamic DNS) would currently fail every restore-fetch
/// this way — independent of, and in addition to, the V1/V2 handshake issue this suite covers.
/// </summary>
public class RestoreSeederFetchTests : IAsyncLifetime
{
    private const string MasterPassword = "restoreFetchPassword123";

    private BmbWebApplicationFactory _nodeA = null!; // the seeder
    private BmbWebApplicationFactory _nodeB = null!; // the node fetching the snapshot
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;
    private Guid _nodeAId;

    public async Task InitializeAsync()
    {
        _nodeA = new BmbWebApplicationFactory();
        await _nodeA.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _nodeA.CreateClient();
        await UnlockAsync(_clientA, MasterPassword);

        using (var scopeA = _nodeA.Services.CreateScope())
        {
            var identity = await scopeA.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
            _nodeAId = identity!.NodeId;
        }

        _nodeB = new BmbWebApplicationFactory();
        // DownloadFromWhitelistedSeederAsync calls httpClientFactory.CreateClient("SyncClient") —
        // route it (and every other outbound name) straight into NodeA's real TestServer so the
        // real production handshake code runs against a real peer.
        _nodeB.RouteOutboundHttpThrough(_nodeA.Server.CreateHandler());
        _clientB = _nodeB.CreateClient();
        await _nodeB.JoinNodeAsync(_clientA, "NodeB", MasterPassword);
        await UnlockAsync(_clientB, MasterPassword);
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose();
        _clientB.Dispose();
        _nodeA.Dispose();
        _nodeB.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DownloadFromWhitelistedSeederAsync_RealHandshake_DownloadsFile()
    {
        var (eventId, fileBytes) = await StageRestoreFileOnNodeAAsync();

        var destPath = Path.Combine(Path.GetTempPath(), $"bmb-restore-fetch-{Guid.NewGuid():N}.bin");
        try
        {
            using var scopeB = _nodeB.Services.CreateScope();
            var restoreSvc = scopeB.ServiceProvider.GetRequiredService<RestoreInitiatorService>();
            var identity = await scopeB.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
            var whitelistRepo = scopeB.ServiceProvider.GetRequiredService<IWhitelistRepository>();

            // The whitelist row NodeB is dialing NodeA as — the out-of-band anchor the
            // audience-pinning check validates the seeder's challenge against.
            var originator = new WhitelistEntry
            {
                NodeId = _nodeAId,
                DisplayName = "NodeA",
                Ed25519PublicKey = new byte[32],
                // A literal loopback IP, not a hostname: ResolveAndPinSeederAsync takes a different
                // path for a literal IP (used as-is) vs. a DNS name (re-resolved and rebuilt via
                // UriBuilder, which normalizes to a trailing "/" and would double up with the
                // "/api/..." suffix appended later — a real, separate bug independent of the
                // signing regression this test targets; see the test class report for details).
                ApiAddress = "http://127.0.0.1",
                Status = "A",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var payload = MakePayload(fileBytes.Length);

            // The real production code under test: real HTTP challenge/authenticate handshake
            // (V2 domain tag, audience-pinning check, real Ed25519 signing) plus the real download.
            await restoreSvc.DownloadFromWhitelistedSeederAsync(
                eventId.ToString(), payload, originator, identity!, whitelistRepo, destPath);

            File.Exists(destPath).Should().BeTrue();
            var downloaded = await File.ReadAllBytesAsync(destPath);
            downloaded.Should().Equal(fileBytes);
        }
        finally
        {
            try { File.Delete(destPath); } catch { }
        }
    }

    /// <summary>
    /// Covers the security fix from commit 3c43dfc5: unlike the two join paths (first contact, no
    /// anchor to check against), this fetch DOES have an out-of-band anchor — our own whitelist row
    /// for the peer we dialed — and must refuse a seeder whose challenge names a different node,
    /// even if that seeder answers at the exact URL we pinned.
    /// </summary>
    [Fact]
    public async Task DownloadFromWhitelistedSeederAsync_SeederChallengeNamesWrongNode_Refuses()
    {
        var (eventId, fileBytes) = await StageRestoreFileOnNodeAAsync();
        var wrongNodeId = Guid.NewGuid(); // does not match NodeA's real identity

        var destPath = Path.Combine(Path.GetTempPath(), $"bmb-restore-fetch-{Guid.NewGuid():N}.bin");
        try
        {
            using var scopeB = _nodeB.Services.CreateScope();
            var restoreSvc = scopeB.ServiceProvider.GetRequiredService<RestoreInitiatorService>();
            var identity = await scopeB.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
            var whitelistRepo = scopeB.ServiceProvider.GetRequiredService<IWhitelistRepository>();

            var originator = new WhitelistEntry
            {
                NodeId = wrongNodeId, // pinned to the WRONG node — NodeA will answer with its own real id
                DisplayName = "NotActuallyNodeA",
                Ed25519PublicKey = new byte[32],
                // A literal loopback IP, not a hostname: ResolveAndPinSeederAsync takes a different
                // path for a literal IP (used as-is) vs. a DNS name (re-resolved and rebuilt via
                // UriBuilder, which normalizes to a trailing "/" and would double up with the
                // "/api/..." suffix appended later — a real, separate bug independent of the
                // signing regression this test targets; see the test class report for details).
                ApiAddress = "http://127.0.0.1",
                Status = "A",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var payload = MakePayload(fileBytes.Length);

            var act = () => restoreSvc.DownloadFromWhitelistedSeederAsync(
                eventId.ToString(), payload, originator, identity!, whitelistRepo, destPath);

            // Assert the SPECIFIC audience-pinning refusal, not just "no candidate worked" —
            // otherwise this test would pass just as easily if the seeder were merely unreachable.
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Refusing to sign*");
            File.Exists(destPath).Should().BeFalse();
        }
        finally
        {
            try { File.Delete(destPath); } catch { }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task UnlockAsync(HttpClient client, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/session/unlock", new { Password = password });
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Writes an arbitrary "snapshot" file into NodeA's restore-pending directory and a matching
    /// tbl_event row (mirroring what POST /api/snapshots/restore-network does), so
    /// GET /api/snapshots/restore/{eventId}/file will actually serve it. Content doesn't need to be
    /// a real snapshot — DownloadFromWhitelistedSeederAsync only downloads bytes; the hash check
    /// and apply live in its caller (ExecuteDownloadAndApplyAsync), out of scope here (see the
    /// class doc comment).
    /// </summary>
    private async Task<(Guid eventId, byte[] fileBytes)> StageRestoreFileOnNodeAAsync()
    {
        var eventId = Guid.NewGuid();
        var fileBytes = RandomNumberGenerator.GetBytes(4096);

        using var scopeA = _nodeA.Services.CreateScope();
        var snapshotSvc = scopeA.ServiceProvider.GetRequiredService<SnapshotService>();
        var pendingDir = Path.Combine(snapshotSvc.SnapshotsDir, "restore-pending");
        Directory.CreateDirectory(pendingDir);
        await File.WriteAllBytesAsync(Path.Combine(pendingDir, $"{eventId}.bin"), fileBytes);

        var eventLogRepo = scopeA.ServiceProvider.GetRequiredService<IEventLogRepository>();
        await eventLogRepo.AppendAsync(new SyncEvent
        {
            EventId = eventId,
            NodeId = _nodeAId,
            LamportTs = 1,
            EventType = EventTypes.RestoreNetwork,
            Payload = "{}",
            Signature = new byte[64],
            ProtocolVersion = 1,
            CreatedAt = DateTime.UtcNow
        });

        return (eventId, fileBytes);
    }

    private static RestoreNetworkEventPayload MakePayload(long fileSizeBytes) => new(
        SnapshotHash: "unused-in-this-scope",
        RestorePointTs: DateTime.UtcNow.ToString("O"),
        FileSizeBytes: fileSizeBytes,
        ExpiresAt: DateTime.UtcNow.AddDays(30).ToString("O"),
        // Deliberately not a usable fallback candidate (no scheme) — the whitelist-pinned
        // originator below is the only candidate that should ever be tried in this test.
        SourceUrl: "not-a-url",
        FilterSecrets: true);
}
