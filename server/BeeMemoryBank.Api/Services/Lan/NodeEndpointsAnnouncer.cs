using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.Network;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.Api.Services.Lan;

/// <summary>
/// This node's TLS anchors as the files of its local CA show them (no key is read): the pin of the leaf it serves and its CA's public key. What a peer
/// accepts for it on any address (docs/design/lan-peer-sync.md section 9). Null members where there is no such file (a server, a node without a CA).
/// </summary>
public static class OwnTlsAnchors
{
    public static (string? LeafPin, string? CaKey) Read(string dataPath)
    {
        string? leafPin = null, caKey = null;
        try
        {
            var leaf = Path.Combine(dataPath, "certs", "leaf.crt");
            if (File.Exists(leaf))
            {
                using var cert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(leaf));
                leafPin = BeeMemoryBank.Sync.Blind.Spki.Of(cert);
            }
            var ca = Path.Combine(dataPath, "certs", "ca.crt");
            if (File.Exists(ca))
            {
                using var cert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(ca));
                caKey = Base64Url.EncodeToString(cert.PublicKey.ExportSubjectPublicKeyInfo());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
        return (leafPin, caKey);
    }

    /// <summary>
    /// The endpoints this node's listener serves the sync surface on (<see cref="LanListenerState"/>): https, the LAN port, scope by address. Only the
    /// addresses of trusted networks are in the state file, so a café's address is never announced.
    /// </summary>
    public static List<NodeEndpointEntry> Endpoints(LanListenerSnapshot state)
    {
        var result = new List<NodeEndpointEntry>();
        if (!state.Listening || !state.Sync) return result;
        foreach (var text in state.SyncAddresses)
        {
            if (!IPAddress.TryParse(text, out var ip)) continue;
            var scope = ChannelScopes.IsCgnat(ip) || ChannelScopes.IsUniqueLocalV6(ip) ? ChannelScopes.Vpn : ChannelScopes.Lan;
            var host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
            result.Add(new NodeEndpointEntry($"https://{host}:{state.Port}", scope));
        }
        // What the applier would refuse is not announced (a loopback address outside the tests).
        return NodeEndpointsRules.Validate(result.Take(NodeEndpointsRules.MaxEndpoints).ToList(), out _) is { } valid
            ? valid
            : result.Where(e => NodeEndpointsRules.Validate([e], out _) is { Count: 1 }).Take(NodeEndpointsRules.MaxEndpoints).ToList();
    }
}

/// <summary>
/// Signs and logs this node's own <c>node_endpoints</c> event: checked by the rules a peer's copy goes through, then the log, then the row (the same
/// applier), then the sync loop is woken. The log comes before the row: a row whose event never reached the log would read as announced, and the
/// announcer would wait out its keep-alive before it tried again; a row that failed after the log is written at the next look, which announces again.
/// The record names this node only.
/// </summary>
public sealed class NodeEndpointsPublisher(
    INodeIdentityRepository nodeRepo,
    IEventLogRepository eventLogRepo,
    ILamportClock clock,
    SessionService session,
    EventApplier applier,
    ISyncTrigger syncTrigger,
    BeeMemoryBank.Sync.Metadata.ISyncProtocolPolicy protocolPolicy)
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    public async Task<SyncEvent> PublishAsync(IReadOnlyList<NodeEndpointEntry> endpoints, string? leafPin, string? caKey, int? port, long seq)
    {
        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node is not initialized.");
        var payload = new NodeEndpointsPayload(identity.NodeId.ToString("D"), seq, endpoints.ToList(), leafPin, caKey, port);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = identity.NodeId,
            LamportTs = clock.Tick(),
            EventType = EventTypes.NodeEndpoints,
            EntityId = EventEntityId.Derive(EventTypes.NodeEndpoints, null, payloadJson),
            Payload = payloadJson,
            Signature = [],
            // A control event: always clear, at the protocol this node declares.
            ProtocolVersion = protocolPolicy.Declared,
            CreatedAt = DateTime.UtcNow,
            ActorType = "system",
            ActorName = identity.DisplayName,
        };
        evt.Signature = BeeMemoryBank.Crypto.NodeIdentityVault.SignWithIdentityOrGetDek(
            identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
            identity.NodeId, session.GetMasterDek, EventSignature.BuildPayload(evt));
        if (NodeEndpointsRules.Validate(payload.Endpoints, out var refusal) is null)
            throw new InvalidOperationException($"This node's endpoints break the announcement's rules: {refusal}.");
        if ((leafPin is not null && !BeeMemoryBank.Sync.Blind.Spki.IsWellFormed(leafPin)) || (caKey is not null && !NodeEndpointsRules.IsWellFormedCaKey(caKey)))
            throw new InvalidOperationException("This node's TLS anchors are not in the announcement's form.");
        await eventLogRepo.AppendAsync(evt);
        await applier.ApplyOwnNodeEndpointsAsync(evt);
        syncTrigger.Signal();
        return evt;
    }
}

/// <summary>
/// Announces where this desktop node's LAN listener serves the sync surface, and its TLS anchors (docs/design/lan-peer-sync.md section 8): once the
/// listener is up, again when the set of endpoints or an anchor changes (after it stayed the same for <see cref="Debounce"/>, so a Wi-Fi that comes and
/// goes is one announcement, not ten), and otherwise every <see cref="KeepAlive"/>, so a node that joined after a compaction learns it from the log too.
/// Nothing while the vault is closed or locked (the record is signed with the node key), in invisible mode, or on a node that is not initialized.
/// </summary>
public sealed class NodeEndpointsAnnouncer(
    IServiceScopeFactory scopes,
    InvisibleModeService invisible,
    SessionService session,
    string dataPath,
    ILogger<NodeEndpointsAnnouncer> logger) : BackgroundService
{
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
    public static TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KeepAlive = TimeSpan.FromDays(7);

    private string? _pending;
    private DateTime _pendingSince;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await scopes.WaitUsableAsync(stoppingToken); await TickAsync(DateTime.UtcNow); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (VaultSealedException) { }
            catch (SessionLockedException) { }
            catch (Exception ex) { logger.LogWarning(ex, "Announcing this node's endpoints failed; tried again shortly"); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One look: publishes when what this node would announce differs from its last announcement and has stayed so, or the keep-alive is due.</summary>
    internal async Task<bool> TickAsync(DateTime nowUtc)
    {
        if (invisible.IsInvisible || !session.IsUnlocked) return false;
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        if (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync() is not { } identity) return false;

        var state = new LanListenerState(dataPath).Read();
        var endpoints = OwnTlsAnchors.Endpoints(state);
        var (leafPin, caKey) = OwnTlsAnchors.Read(dataPath);
        // Nothing to say yet: no listener has ever served, and there is no certificate to anchor (a node that is only starting).
        if (endpoints.Count == 0 && leafPin is null && caKey is null) return false;
        var port = state.Listening ? state.Port : (int?)null;

        var current = await sp.GetRequiredService<PeerEndpointStore>().GetAsync(identity.NodeId);
        var wanted = Fingerprint(endpoints, leafPin, caKey);
        var announced = current is { LamportTs: > 0 } ? Fingerprint(current.Endpoints, current.TlsLeafSpki, current.TlsCaKey) : null;

        if (wanted == announced)
        {
            _pending = null;
            if (current is null || nowUtc - current.UpdatedAt < KeepAlive) return false;
        }
        else
        {
            // A change is announced once it has held for the debounce: the first look after a start (nothing announced yet) waits too.
            if (_pending != wanted)
            {
                _pending = wanted;
                _pendingSince = nowUtc;
                return false;
            }
            if (nowUtc - _pendingSince < Debounce) return false;
        }

        var seq = (current?.LamportTs > 0 ? await SeqOfAsync(sp, identity.NodeId) : 0) + 1;
        await sp.GetRequiredService<NodeEndpointsPublisher>().PublishAsync(endpoints, leafPin, caKey, port, seq);
        _pending = null;
        logger.LogInformation("Announced this node's endpoints ({Count} on trusted networks, port {Port})", endpoints.Count, port);
        return true;
    }

    private static async Task<long> SeqOfAsync(IServiceProvider sp, Guid self)
    {
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn,
            "SELECT announce_seq FROM tbl_peer_endpoint WHERE node_id = @Id COLLATE NOCASE", new { Id = self.ToString("D") });
    }

    private static string Fingerprint(IEnumerable<NodeEndpointEntry> endpoints, string? leaf, string? ca) =>
        string.Join('|', endpoints.Select(e => $"{e.Scope}:{e.Url}").Order(StringComparer.Ordinal)) + "#" + leaf + "#" + ca;
}
