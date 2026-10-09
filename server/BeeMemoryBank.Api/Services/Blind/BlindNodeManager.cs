using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;

namespace BeeMemoryBank.Api.Services;

public sealed class BlindNodeUnreachableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A blind node as the PC's "Blind nodes" page shows it (plan 9, 5.6).</summary>
/// <param name="Alarms">The kinds of the <see cref="BlindAlarmService"/> alarms the page shows as banners: "old_protocol" — it last
/// spoke a protocol below this build's; "pc_too_old" — above it (this PC cannot apply what it holds, so the PC is the one to update);
/// "silent" — no contact in <see cref="BlindNodeManager.SilentAfter"/>.</param>
/// <param name="AdoptedCheckpoint">The checkpoint this node took from the blind node as its pull position, when it held
/// none (see <see cref="SyncClient.SyncWithPeerAsync"/>); null when it never had to.</param>
/// <param name="CreatedAt">When the row was added: a node never heard from is judged from here.</param>
/// <param name="ProtocolSeenAt">When the node last declared its protocol to this PC (it called, or answered my-standing).</param>
public sealed record BlindNodeStatus(
    Guid NodeId, string DisplayName, string? Address, int? Protocol, DateTime? LastContact,
    IReadOnlyList<string> Alarms, long? AdoptedCheckpoint = null, DateTime CreatedAt = default, DateTime? ProtocolSeenAt = null);

/// <param name="Warnings">What the pre-flight wants the operator to know, which did not stop the add.</param>
public sealed record BlindNodeAdded(Guid NodeId, string DisplayName, long PackageCp, IReadOnlyList<string> Warnings);

/// <summary>
/// The PC's side of blind nodes (plan 4.2, 5.2): add one from its pair code, reseed it, list it.
/// Also the full node's <see cref="IBlindPeerReseeder"/>: after each sync with a blind peer it
/// reseeds that peer when the gap detector fired or the peer asks for it — and only then.
/// </summary>
/// <param name="time">The clock of every contact and silence judgement; null means the system clock.</param>
/// <param name="alarms">Judges the rows (<see cref="BlindAlarmService"/>); null means one on <paramref name="time"/> that judges at once.</param>
public sealed class BlindNodeManager(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    SpkiPinRegistry pins,
    ILogger<BlindNodeManager> logger,
    TimeProvider? time = null,
    BlindAlarmService? alarms = null) : IBlindPeerReseeder
{
    /// <summary>A blind node that has not been in contact this long is flagged on the Blind nodes page (plan 5.6).</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromDays(3);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly BlindAlarmService _alarms = alarms ?? new BlindAlarmService(time ?? TimeProvider.System);

    private const int PartBytes = 32 * 1024 * 1024;

    /// <summary>The blind nodes with their banners: the alarms of <see cref="GetAlarmsAsync"/> the page shows.</summary>
    public async Task<IReadOnlyList<BlindNodeStatus>> ListAsync()
    {
        var nodes = await ReadAsync();
        var banners = _alarms.Judge(nodes).Alarms.Where(a => a.Banner).ToLookup(a => a.NodeId, a => a.Kind);
        return nodes.Select(n => n with { Alarms = banners[n.NodeId].Distinct().ToList() }).ToList();
    }

    /// <summary>Which blind nodes need attention now (<see cref="BlindAlarmService"/>).</summary>
    public async Task<BlindAlarmReport> GetAlarmsAsync() => _alarms.Judge(await ReadAsync());

    /// <summary>Every active blind row with its contact facts; no alarms yet.</summary>
    private async Task<IReadOnlyList<BlindNodeStatus>> ReadAsync()
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

        return rows.Select(r =>
        {
            // Most recent contact in either direction, or when it last authenticated to us — or, for a node this PC calls,
            // when it last answered this PC's my-standing call (AsksForReseedAsync records that as its protocol sighting).
            var latest = new[] { pulled.GetValueOrDefault(r.NodeId), pushed.GetValueOrDefault(r.NodeId), r.LastProtocolSeenAt ?? default }.Max();
            DateTime? last = latest > DateTime.MinValue ? latest : null;
            return new BlindNodeStatus(r.NodeId, r.DisplayName, r.ApiAddress, r.LastProtocolVersion, last, [],
                adopted.TryGetValue(r.NodeId, out var cp) ? cp : null, r.CreatedAt, r.LastProtocolSeenAt);
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
            var version = await eventLogger.LogWhitelistUpdateAsync(code.NodeId, code.Address, displayName: null,
                tlsSpki: code.TlsSpki, tlsTrust: BlindTrust.Pin);
            entry.ApiAddress = code.Address;
            entry.TlsSpki = code.TlsSpki;
            entry.TlsTrust = BlindTrust.Pin;
            entry.UpdatedAt = UtcNow();
            entry.LamportTs = version.LamportTs;
            entry.SourceNodeId = version.SourceNodeId;
            await whitelist.UpdateAsync(entry);
        }
        else
        {
            var now = UtcNow();
            entry = new WhitelistEntry
            {
                NodeId = code.NodeId,
                DisplayName = displayName,
                Ed25519PublicKey = Convert.FromBase64String(code.PublicKeyB64),
                ApiAddress = code.Address,
                TlsSpki = code.TlsSpki,
                TlsTrust = BlindTrust.Pin,
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
            {
                await whitelist.UpdateAsync(entry);
                // Removed and added again: the row is the old one, and its clocks ran on while it was gone.
                await whitelist.ResetLocalBookkeepingAsync(code.NodeId, entry.CreatedAt);
            }
        }
        pins.Invalidate();

        // A node this PC already pulled from (added again after a removal, or paired again) may hold events that exist
        // nowhere else, and its log may have moved past the pull position this PC kept. Take in what it has first, as a
        // reseed does, and let the package say so; a seed that skipped this would drop those events and leave a cursor
        // that no longer fits the log (every cycle would end in a 410).
        var includesUpTo = await TakeInWhatTheNodeHoldsAsync(sp, http, entry, ct);

        var package = await sp.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo, producerIsSuperadmin: true, ct);
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
        logger.LogInformation("Blind node {NodeId} at {Address} added and seeded (cp {Cp}, includes up to {UpTo})",
            code.NodeId, code.Address, package.Manifest.CpSequence, includesUpTo);
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

    /// <summary>
    /// The blind node's own flag (plan 5.3) — acted on only by a node it sees as superadmin. The answer is also this PC's
    /// evidence of contact: an authenticated round trip, made every cycle whether or not the vault changed, so it is recorded with
    /// the protocol the blind node declares in it. That is what fills "Protocol" on the Blind nodes page and arms the old / newer
    /// protocol alarms — a server blind node never calls this PC, so nothing else ever records them. The column is a local
    /// observation (<see cref="IWhitelistRepository.RecordProtocolVersionAsync"/>): no event, nothing replicated.
    /// </summary>
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
        if (standing is null) return false;
        if (standing.ResponderNodeId == peer.NodeId && standing.Protocol > 0)
            await sp.GetRequiredService<IWhitelistRepository>().RecordProtocolVersionAsync(peer.NodeId, standing.Protocol, UtcNow());
        return standing is { ReseedNeeded: true, CallerIsSuperadmin: true };
    }

    /// <summary>
    /// For a node paired before: pulls everything it has (the reseed's first step) and returns how far that got. Null when this
    /// PC holds no pull position for it, or when the position cannot be used: the node's log starts above it, or it does not
    /// let this PC in (a volume that was made again). Then the position is dropped, so that the first cycle after the seed
    /// adopts the new checkpoint as for any new pairing. A node that cannot be reached at all fails the add, as the seed would.
    /// </summary>
    private async Task<long?> TakeInWhatTheNodeHoldsAsync(IServiceProvider sp, HttpClient http, WhitelistEntry row, CancellationToken ct)
    {
        var positions = sp.GetRequiredService<ISyncPositionRepository>();
        if (await positions.GetAsync(row.NodeId) is null) return null;
        try
        {
            return await PullEverythingAsync(sp, http, row, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not HttpRequestException { StatusCode: null })
        {
            logger.LogWarning(
                "Blind node {NodeId} could not be pulled from before it is seeded again ({Reason}); its pull position is dropped and " +
                "what only it holds is not carried over", row.NodeId, ex.Message);
            // A raw statement, not a repository method: nothing else drops one peer's cursor, and ISyncPositionRepository has fakes.
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("DELETE FROM tbl_sync_position WHERE remote_node_id = @id COLLATE NOCASE", new { id = row.NodeId.ToString() });
            return null;
        }
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

    private async Task StartPushingFromAsync(IServiceProvider sp, Guid blindId, long cp) =>
        await sp.GetRequiredService<ISyncPushPositionRepository>().UpsertAsync(new SyncPushPosition
        {
            RemoteNodeId = blindId, LastPushedSeq = cp, PushedAt = UtcNow()
        });

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

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
