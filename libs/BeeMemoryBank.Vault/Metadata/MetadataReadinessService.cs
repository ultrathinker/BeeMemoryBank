using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Metadata;

public static class ReadinessStatuses
{
    public const string Ready = "ready";
    public const string NotCapable = "not_capable";
    public const string Stale = "stale";
    public const string Unknown = "unknown";
}

public static class ReadinessBlockerCodes
{
    public const string PeerNotReady = "peer_not_ready";
    public const string VaultLocked = "vault_locked";
    public const string NotSuperadmin = "not_superadmin";
    public const string RotationPending = "rotation_pending";
    public const string EventsHeld = "events_held";
    public const string AlreadyActivated = "already_activated";
}

public sealed record ReadinessBlocker(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("node_id")] Guid? NodeId = null,
    [property: JsonPropertyName("display_name")] string? DisplayName = null,
    [property: JsonPropertyName("status")] string? Status = null);

public sealed record ReadinessPeer(
    [property: JsonPropertyName("node_id")] Guid NodeId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("protocol")] int? Protocol,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("app_version")] string? AppVersion,
    [property: JsonPropertyName("seen_at")] DateTime? SeenAt,
    [property: JsonPropertyName("seen_by")] string? SeenBy);

public sealed record ReadinessActivation(
    [property: JsonPropertyName("activation_id")] string ActivationId,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("generation")] int Generation,
    [property: JsonPropertyName("activated_at")] string ActivatedAt,
    [property: JsonPropertyName("author_node_id")] string AuthorNodeId);

/// <summary>The answer of <c>GET /api/sync/readiness</c> (docs/design/step-a-spec.md 5.3).</summary>
public sealed record ReadinessReport(
    [property: JsonPropertyName("activated")] bool Activated,
    [property: JsonPropertyName("activation")] ReadinessActivation? Activation,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("blockers")] IReadOnlyList<ReadinessBlocker> Blockers,
    [property: JsonPropertyName("peers")] IReadOnlyList<ReadinessPeer> Peers);

/// <summary>
/// Whether this vault may switch metadata encryption on (docs/design/step-a-spec.md 5.3): every active peer must have
/// shown the envelope capability within 14 days, as this node saw it or as a peer it can reach saw it. A peer is asked for
/// its <c>my-standing</c> (10 s each, in parallel); what this node itself recorded counts as well; the newest observation of
/// each row decides. Besides the peers, the vault must be unlocked, this node a superadmin as every answering peer sees it,
/// no DEK rotation in flight and no event held here.
/// </summary>
public sealed class MetadataReadinessService(
    IWhitelistRepository whitelist,
    INodeIdentityRepository nodeRepo,
    SessionService session,
    ISyncProtocolPolicy policy,
    IHeldEventStatus held,
    IDekRotationStateRepository rotations,
    IDbConnectionFactory connFactory,
    IHttpClientFactory httpClientFactory,
    INodeAuthSigner authSigner,
    ILogger<MetadataReadinessService> logger)
{
    public static readonly TimeSpan Freshness = TimeSpan.FromDays(14);
    public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <param name="askPeers">False: this node's own observations only (a quick look, e.g. for a page load).</param>
    public async Task<ReadinessReport> GetReportAsync(bool askPeers = true, CancellationToken ct = default)
    {
        var activation = await ReadActivationAsync();
        var blockers = new List<ReadinessBlocker>();
        if (policy.SealsMetadata || activation is not null)
        {
            // One-way and done: nothing left to decide, and no reason to call anybody.
            blockers.Add(new ReadinessBlocker(ReadinessBlockerCodes.AlreadyActivated));
            return new ReadinessReport(true, activation, false, blockers, []);
        }

        var rows = await whitelist.GetAllActiveAsync();
        var answers = askPeers && session.IsUnlocked ? await AskPeersAsync(rows, ct) : [];

        var peers = new List<ReadinessPeer>();
        foreach (var row in rows)
        {
            var observations = new List<Observation>();
            if (row.LastCapabilitiesSeenAt is { } seen)
                observations.Add(new Observation(MetadataCapabilities.Parse(row.LastCapabilities), row.LastAppVersion,
                    row.LastProtocolVersion, seen, "self"));
            foreach (var (responder, standing, at) in answers)
            {
                if (responder == row.NodeId)
                    observations.Add(new Observation(standing.Capabilities ?? [], standing.AppVersion, standing.Protocol, at, "self"));
                else if (standing.Peers.FirstOrDefault(p => p.NodeId == row.NodeId) is { CapabilitiesSeenAt: { } peerSeen } p)
                    observations.Add(new Observation(p.Capabilities ?? [], p.AppVersion, p.LastProtocolVersion, peerSeen,
                        responder.ToString("D")));
            }

            var newest = observations.OrderByDescending(o => o.SeenAt).FirstOrDefault();
            var status = newest is null ? ReadinessStatuses.Unknown
                : !MetadataCapabilities.HasEnvelopeV1(newest.Capabilities) ? ReadinessStatuses.NotCapable
                : DateTime.UtcNow - ToUtc(newest.SeenAt) > Freshness ? ReadinessStatuses.Stale
                : ReadinessStatuses.Ready;
            peers.Add(new ReadinessPeer(row.NodeId, row.DisplayName, BlindNodeId.IsBlind(row.NodeId) ? "blind" : "full", status,
                newest?.Protocol ?? row.LastProtocolVersion, newest?.Capabilities ?? [], newest?.AppVersion,
                newest is null ? null : ToUtc(newest.SeenAt), newest?.SeenBy));
            if (status != ReadinessStatuses.Ready)
                blockers.Add(new ReadinessBlocker(ReadinessBlockerCodes.PeerNotReady, row.NodeId, row.DisplayName, status));
        }

        if (!session.IsUnlocked) blockers.Add(new ReadinessBlocker(ReadinessBlockerCodes.VaultLocked));
        // "Superadmin" is each receiver's decision (its whitelist row of this node); a peer that answered and does not see
        // this node as one would refuse the activation event.
        if (answers.Any(a => !a.Standing.CallerIsSuperadmin)) blockers.Add(new ReadinessBlocker(ReadinessBlockerCodes.NotSuperadmin));
        if ((await rotations.GetByStateAsync(DekRotationState.Proposed)).Count > 0
            || (await rotations.GetByStateAsync(DekRotationState.Committing)).Count > 0)
            blockers.Add(new ReadinessBlocker(ReadinessBlockerCodes.RotationPending));
        if (await held.AnyHeldAsync(ct)) blockers.Add(new ReadinessBlocker(ReadinessBlockerCodes.EventsHeld));

        return new ReadinessReport(false, null, blockers.Count == 0, blockers, peers);
    }

    private sealed record Observation(IReadOnlyList<string> Capabilities, string? AppVersion, int? Protocol, DateTime SeenAt, string SeenBy);

    private static DateTime ToUtc(DateTime t) => t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();

    private async Task<List<(Guid Responder, MyStanding Standing, DateTime At)>> AskPeersAsync(List<WhitelistEntry> rows, CancellationToken ct)
    {
        var self = await nodeRepo.GetAsync();
        if (self is null) return [];
        var callable = rows.Where(r => !string.IsNullOrWhiteSpace(r.ApiAddress)).ToList();
        var asked = await Task.WhenAll(callable.Select(r => AskAsync(self, r, ct)));
        return asked.Where(a => a is not null).Select(a => a!.Value).ToList();
    }

    private async Task<(Guid Responder, MyStanding Standing, DateTime At)?> AskAsync(NodeIdentity self, WhitelistEntry peer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PeerTimeout);
        try
        {
            // The pinned client of the sync scheduler: a peer with a pinned TLS key is accepted on that key only.
            var http = httpClientFactory.CreateClient(SyncScheduler.HttpClientName);
            var address = peer.ApiAddress!.TrimEnd('/');
            var token = await PeerAuthenticator.AuthenticateAsync(authSigner, http, address, self, peer.NodeId, timeout.Token, policy.Declared);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{address}/api/sync/my-standing");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, timeout.Token);
            resp.EnsureSuccessStatusCode();
            var standing = await resp.Content.ReadFromJsonAsync<MyStanding>(JsonOpts, timeout.Token);
            if (standing is null || standing.ResponderNodeId != peer.NodeId) return null;
            return (peer.NodeId, standing, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or InvalidOperationException or InvalidDataException or JsonException)
        {
            logger.LogInformation("Readiness: node {NodeId} did not answer ({Error})", peer.NodeId, ex.GetType().Name);
            return null;
        }
    }

    private async Task<ReadinessActivation?> ReadActivationAsync()
    {
        using var conn = connFactory.CreateConnection();
        var row = (await conn.QueryAsync<ActivationRow>(
            @"SELECT activation_id AS ActivationId, key_id AS KeyId, generation AS Generation, activated_at AS ActivatedAt,
                     author_node_id AS AuthorNodeId FROM tbl_metadata_activation LIMIT 1")).FirstOrDefault();
        return row is null ? null : new ReadinessActivation(row.ActivationId, row.KeyId, (int)row.Generation, row.ActivatedAt, row.AuthorNodeId);
    }

    private sealed class ActivationRow
    {
        public string ActivationId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public long Generation { get; set; }
        public string ActivatedAt { get; set; } = "";
        public string AuthorNodeId { get; set; } = "";
    }
}
