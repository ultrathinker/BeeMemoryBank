using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Api.Services;

public sealed class BlindNodeUnreachableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A blind node as the PC's "Blind nodes" page shows it (plan 9, 5.6).</summary>
/// <param name="Alarms">"old_protocol" — it last spoke a protocol below this build's;
/// "silent" — no contact in <see cref="BlindNodeManager.SilentAfter"/>.</param>
/// <param name="AdoptedCheckpoint">The checkpoint this node took from the blind node as its pull position, when it held
/// none (see <see cref="SyncClient.SyncWithPeerAsync"/>); null when it never had to.</param>
public sealed record BlindNodeStatus(
    Guid NodeId, string DisplayName, string? Address, int? Protocol, DateTime? LastContact,
    IReadOnlyList<string> Alarms, long? AdoptedCheckpoint = null);

/// <param name="Warnings">What the pre-flight wants the operator to know, which did not stop the add.</param>
public sealed record BlindNodeAdded(Guid NodeId, string DisplayName, long PackageCp, IReadOnlyList<string> Warnings);

/// <summary>
/// The PC's side of blind nodes (plan 4.2, 5.2): add one from its pair code, reseed it, list it.
/// Also the full node's <see cref="IBlindPeerReseeder"/>: after each sync with a blind peer it
/// reseeds that peer when the gap detector fired or the peer asks for it — and only then.
/// </summary>
public sealed class BlindNodeManager(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    SpkiPinRegistry pins,
    ILogger<BlindNodeManager> logger) : IBlindPeerReseeder
{
    /// <summary>A blind node that has not been in contact this long is flagged (plan 5.6).</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromDays(3);

    private const int PartBytes = 32 * 1024 * 1024;

    public async Task<IReadOnlyList<BlindNodeStatus>> ListAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var rows = (await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync())
            .Where(r => BlindNodeId.IsBlind(r.NodeId));
        var pulled = (await sp.GetRequiredService<ISyncPositionRepository>().GetAllAsync())
            .ToDictionary(p => p.RemoteNodeId, p => p.UpdatedAt);
        var pushed = (await sp.GetRequiredService<ISyncPushPositionRepository>().GetAllAsync())
            .ToDictionary(p => p.RemoteNodeId, p => p.PushedAt);
        var adopted = await sp.GetRequiredService<BlindState>().GetAdoptedCheckpointsAsync();

        var now = DateTime.UtcNow;
        return rows.Select(r =>
        {
            // Most recent contact in either direction, or when it last authenticated to us.
            var latest = new[] { pulled.GetValueOrDefault(r.NodeId), pushed.GetValueOrDefault(r.NodeId), r.LastProtocolSeenAt ?? default }.Max();
            DateTime? last = latest > DateTime.MinValue ? latest : null;
            var alarms = new List<string>();
            if (r.LastProtocolVersion is { } v && v < SyncProtocolVersion.Current) alarms.Add("old_protocol");
            if ((last ?? r.CreatedAt) < now - SilentAfter) alarms.Add("silent");
            return new BlindNodeStatus(r.NodeId, r.DisplayName, r.ApiAddress, r.LastProtocolVersion, last, alarms,
                adopted.TryGetValue(r.NodeId, out var cp) ? cp : null);
        }).ToList();
    }

    /// <summary>
    /// Plan 4.2: check the pinned key and identity against the code, run the pre-flight, publish
    /// whitelist_add (with the pin), then seed the blind node with this PC's package and start
    /// pushing to it from the package's checkpoint.
    /// </summary>
    public async Task<BlindNodeAdded> AddAsync(string codeText, CancellationToken ct)
    {
        var code = BlindPairCode.Parse(codeText);
        using var http = CreateClient();
        await VerifyIdentityAsync(http, code, ct);

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var warnings = await sp.GetRequiredService<BlindPreflight>().RunAsync(http, ct);

        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var displayName = $"Blind {code.NodeId.ToString()[..8]}";
        var entry = await whitelist.GetByNodeIdAsync(code.NodeId, includeDeleted: true);
        var eventLogger = sp.GetRequiredService<IEventLogger>();
        if (entry is { Status: "A" })
        {
            // Paired again (a recreated volume keeps the key, a renewed address or certificate
            // does not): update the row everyone dials instead of adding a second one.
            var version = await eventLogger.LogWhitelistUpdateAsync(code.NodeId, code.Address, displayName: null, tlsSpki: code.TlsSpki);
            entry.ApiAddress = code.Address;
            entry.TlsSpki = code.TlsSpki;
            entry.UpdatedAt = DateTime.UtcNow;
            entry.LamportTs = version.LamportTs;
            entry.SourceNodeId = version.SourceNodeId;
            await whitelist.UpdateAsync(entry);
        }
        else
        {
            var now = DateTime.UtcNow;
            entry = new WhitelistEntry
            {
                NodeId = code.NodeId,
                DisplayName = displayName,
                Ed25519PublicKey = Convert.FromBase64String(code.PublicKeyB64),
                ApiAddress = code.Address,
                TlsSpki = code.TlsSpki,
                Status = "A",
                // A blind node never authors anything, so the flag would never be read — and
                // "superadmin" on a node that holds no key would only confuse the Nodes page.
                IsSuperadmin = false,
                CreatedAt = now,
                UpdatedAt = now
            };
            // Log first, like every whitelist write: the row carries the version the mesh is told.
            var version = await eventLogger.LogWhitelistAddAsync(entry);
            entry.LamportTs = version.LamportTs;
            entry.SourceNodeId = version.SourceNodeId;
            if (await whitelist.GetByNodeIdAsync(code.NodeId, includeDeleted: true) is null)
                await whitelist.CreateAsync(entry);
            else
                await whitelist.UpdateAsync(entry);
        }
        pins.Invalidate();

        var package = await sp.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true, ct);
        var me = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");
        var seederProof = BlindSeederProof.Compute(code.Secret, package.Manifest.SeedId, me.NodeId, me.Ed25519PublicKey);
        try
        {
            await UploadAsync(http, code.Address, package, req =>
            {
                // Until the row with its pin is in the whitelist, the code is what says which key is
                // the blind node's. The secret itself is never sent: this node names itself and its
                // signing key under it (BlindSeederProof).
                req.Options.Set(SpkiPinRegistry.ExplicitPin, code.TlsSpki);
                req.Headers.Add(BlindSeederProof.NodeIdHeader, me.NodeId.ToString());
                req.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(me.Ed25519PublicKey));
                req.Headers.Add(BlindSeederProof.MacHeader, seederProof);
            }, ct);
        }
        finally
        {
            File.Delete(package.FilePath);
        }
        await sp.GetRequiredService<BlindState>().ClearAdoptedCheckpointAsync(code.NodeId);
        await StartPushingFromAsync(sp, code.NodeId, package.Manifest.CpSequence);
        logger.LogInformation("Blind node {NodeId} at {Address} added and seeded (cp {Cp})",
            code.NodeId, code.Address, package.Manifest.CpSequence);
        return new BlindNodeAdded(code.NodeId, displayName, package.Manifest.CpSequence, warnings);
    }

    /// <summary>
    /// Lossless reseed (plan 5.2): first take in everything the blind node has — what phones
    /// pushed to it may exist nowhere else — then send a package that says so, so the blind node
    /// replays only what reached it after that point.
    /// </summary>
    public async Task ReseedAsync(Guid blindId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var row = await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId);
        if (row is null || !BlindNodeId.IsBlind(blindId) || string.IsNullOrEmpty(row.ApiAddress))
            throw new KeyNotFoundException($"No active blind node {blindId} with an address.");
        var address = row.ApiAddress.TrimEnd('/');

        using var http = CreateClient();
        var includesUpTo = await PullEverythingAsync(sp, http, row, ct);

        var package = await sp.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo, producerIsSuperadmin: true, ct);
        try
        {
            var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync()
                ?? throw new InvalidOperationException("Node is not initialized.");
            var token = await PeerAuthenticator.AuthenticateAsync(
                sp.GetRequiredService<INodeAuthSigner>(), http, address, self, blindId, ct);
            await UploadAsync(http, address, package, req => req.Headers.Authorization = new("Bearer", token), ct);
        }
        finally
        {
            File.Delete(package.FilePath);
        }
        await sp.GetRequiredService<BlindState>().ClearAdoptedCheckpointAsync(blindId);
        await StartPushingFromAsync(sp, blindId, package.Manifest.CpSequence);
        logger.LogInformation("Blind node {NodeId} reseeded (includes up to {UpTo}, cp {Cp})",
            blindId, includesUpTo, package.Manifest.CpSequence);
    }

    public async Task AfterSyncAsync(WhitelistEntry peer, HttpClient http, Exception? failure, CancellationToken ct)
    {
        var reason = failure switch
        {
            PushGapException => "push gap",
            null when await AsksForReseedAsync(peer, http, ct) => "the blind node asked for it",
            _ => null
        };
        if (reason is null) return;
        logger.LogWarning("Reseeding blind node {NodeId} ({Address}): {Reason}", peer.NodeId, peer.ApiAddress, reason);
        await ReseedAsync(peer.NodeId, ct);
    }

    /// <summary>The blind node's own flag (plan 5.3) — acted on only by a node it sees as superadmin.</summary>
    private async Task<bool> AsksForReseedAsync(WhitelistEntry peer, HttpClient http, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync();
        if (self is null) return false;
        var address = peer.ApiAddress!.TrimEnd('/');
        var token = await PeerAuthenticator.AuthenticateAsync(sp.GetRequiredService<INodeAuthSigner>(), http, address, self, peer.NodeId, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{address}/api/sync/my-standing");
        req.Headers.Authorization = new("Bearer", token);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;
        var standing = await resp.Content.ReadFromJsonAsync<MyStanding>(ct);
        return standing is { ReseedNeeded: true, CallerIsSuperadmin: true };
    }

    /// <summary>Pulls from the blind node until nothing new arrives; returns how far that got.</summary>
    private static async Task<long> PullEverythingAsync(IServiceProvider sp, HttpClient http, WhitelistEntry row, CancellationToken ct)
    {
        var positions = sp.GetRequiredService<ISyncPositionRepository>();
        var client = sp.GetRequiredService<SyncClient>();
        long previous = -1;
        for (var round = 0; round < 10_000; round++)
        {
            try
            {
                await client.SyncWithPeerAsync(http, row.ApiAddress!, row.NodeId, ct);
            }
            catch (PushGapException)
            {
                // Expected when the gap detector is why we reseed: the pull ran before the push.
            }
            var current = (await positions.GetAsync(row.NodeId))?.LastSequenceNum ?? 0;
            if (current == previous) return current;
            previous = current;
        }
        return previous;
    }

    private static async Task StartPushingFromAsync(IServiceProvider sp, Guid blindId, long cp) =>
        await sp.GetRequiredService<ISyncPushPositionRepository>().UpsertAsync(new SyncPushPosition
        {
            RemoteNodeId = blindId, LastPushedSeq = cp, PushedAt = DateTime.UtcNow
        });

    /// <summary>
    /// The sync client, whose handler checks pins; with a long timeout, because the last part of a
    /// seed is answered only once the blind node has applied the whole package.
    /// </summary>
    private HttpClient CreateClient()
    {
        var http = httpClientFactory.CreateClient("SyncScheduler");
        http.Timeout = TimeSpan.FromMinutes(30);
        return http;
    }

    /// <summary>
    /// Plan 4.2 step 1: the node at the code's address must present the key pinned in the code and
    /// the identity the code names — otherwise the code was mistyped or someone else answers there.
    /// </summary>
    private static async Task VerifyIdentityAsync(HttpClient http, BlindPairCode code, CancellationToken ct)
    {
        JsonElement identity;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{code.Address}/api/sync/identity");
            req.Options.Set(SpkiPinRegistry.ExplicitPin, code.TlsSpki);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            identity = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new BlindNodeUnreachableException(
                $"Could not reach the blind node at {code.Address} with the key in its code ({ex.Message}).", ex);
        }
        if (identity.GetProperty("nodeId").GetGuid() != code.NodeId
            || identity.GetProperty("ed25519PublicKeyB64").GetString() != code.PublicKeyB64)
            throw new BlindNodeUnreachableException(
                $"The node at {code.Address} is not the one in the pair code.");
    }

    /// <summary>
    /// Sends the package in parts (plan 4.3), resuming from what the blind node reports on a
    /// broken connection or a restart of this loop.
    /// </summary>
    private static async Task UploadAsync(
        HttpClient http, string address, BlindPackage package, Action<HttpRequestMessage> authorize, CancellationToken ct)
    {
        await using var file = File.OpenRead(package.FilePath);
        var buffer = new byte[PartBytes];
        long offset = 0;
        var seedId = package.Manifest.SeedId;
        while (true)
        {
            file.Position = offset;
            var length = await file.ReadAsync(buffer, ct);
            var url = $"{address}/api/blind/seed?seedId={seedId}&offset={offset}&total={package.Length}&sha256={package.Sha256}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(buffer, 0, length)
            };
            authorize(req);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    return;
                case HttpStatusCode.Accepted:
                    offset = JsonDocument.Parse(body).RootElement.GetProperty("received").GetInt64();
                    continue;
                case HttpStatusCode.Conflict when body.Contains("OFFSET_MISMATCH"):
                    offset = JsonDocument.Parse(body).RootElement.GetProperty("received").GetInt64();
                    continue;
                default:
                    throw new BlindNodeUnreachableException(
                        $"The blind node refused the seed ({(int)resp.StatusCode}): {body}");
            }
        }
    }
}
