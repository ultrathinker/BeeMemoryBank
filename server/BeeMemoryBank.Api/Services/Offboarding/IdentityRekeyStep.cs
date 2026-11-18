using System.Collections.Concurrent;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using Dapper;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>
/// Step S1 of a removal with the re-key (release 2.12.0): this node gives itself a new identity key with the reason
/// <c>offboarding:&lt;process id&gt;</c>, and asks every shared full node to do the same with a signed <c>node_rekey_request</c> (superadmin-only,
/// from the driver; the re-key defines no request of its own). Each asked node serves the request itself (<see cref="OffboardingService.ServeRekeyRequestsAsync"/>).
/// <para><b>Idempotent per process.</b> A node re-keys for a process only while its row of that process (<c>tbl_offboarding_node</c>, step 1) still
/// shows the key it had when the step began (the row's baseline): once the key differs, the node has a new seed made after the lock-out, which is what
/// the step is for, and it is not re-keyed again. A node is asked again only when it was never asked for this process (one that appeared).</para>
/// <para><b>Confirmation</b> uses the key this node trusts for a node (its own, or the peer's whitelist key), which moves only when that node's rotation,
/// signed by its old key, is applied here. A node's <c>node_state</c> report of its key is read for the page only: it can lag behind the trusted key, so
/// a stale report would confirm a node that did not re-key.</para>
/// </summary>
public sealed class IdentityRekeyStep(
    NodeIdentityRekeyService rekey,
    INodeIdentityRepository nodes,
    IWhitelistRepository whitelist,
    NodeStateStore states,
    IEventLogger events,
    IDbConnectionFactory db,
    SessionService session,
    ILogger<IdentityRekeyStep> logger,
    NodeStateAnnouncer? announcer = null) : IIdentityRekeyStep
{
    /// <summary>The actor a removal's re-key is audited under.</summary>
    public const string Actor = "removal process";

    public const string ExternalHere =
        "This node keeps its identity key outside its database, so it cannot be given a new one here. Revoke it from another superadmin's device and " +
        "set it up again as a new node; the removal cannot finish while it keeps that key.";

    public const string ExternalThere =
        "This node keeps its identity key outside its database, so it cannot give itself a new one. Wait for the administrator to pair that node " +
        "again as a new node, or revoke it.";

    public const string DevicesThere =
        "It cannot change its identity key yet: some devices in the network run a version that cannot follow a new key. Update them, or remove them.";

    public const string BusyThere = "It could not change its identity key yet (another key operation was running there); it tries again by itself.";

    public const string EventsThere =
        "It has too many events of its own since its last compaction to change its identity key. Let a compaction run there, then it goes on.";

    // The last refusal of this node's own re-key, per process: shown on the page until a re-key goes through (process memory; the next pass says it again).
    private static readonly ConcurrentDictionary<Guid, string> SelfRefusals = new();

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public async Task StartAsync(Guid processId, IReadOnlyList<Guid> sharedNodes, DateTime notBeforeUtc, CancellationToken ct = default)
    {
        if (sharedNodes.Count > 0)
        {
            var asked = await AskedAsync(processId);
            if (sharedNodes.Any(n => !asked.Contains(n)))
            {
                await events.LogNodeRekeyRequestAsync(processId, sharedNodes);
                logger.LogInformation("Removal {Process}: {Count} shared node(s) asked for a new identity key.", processId, sharedNodes.Count);
            }
        }
        await RekeySelfForAsync(processId, ct);
    }

    public async Task<IReadOnlyList<Guid>> CannotFollowAsync(Guid self, CancellationToken ct = default) =>
        (await whitelist.GetAllActiveAsync())
            .Where(w => w.NodeId != self
                        && !BeeMemoryBank.Sync.Metadata.MetadataCapabilities.Parse(w.LastCapabilities).Contains(BeeMemoryBank.Sync.Metadata.MetadataCapabilities.IdentityRekeyV1))
            .Select(w => w.NodeId).ToList();

    public async Task<string?> CurrentKeyFingerprintAsync(Guid nodeId, CancellationToken ct = default) =>
        await OffboardingNodeKeys.FingerprintAsync(nodes, whitelist, nodeId);

    public async Task<string?> WhyWaitingAsync(Guid processId, Guid nodeId, CancellationToken ct = default)
    {
        var self = await nodes.GetAsync();
        if (self is not null && self.NodeId == nodeId)
        {
            if (self.Ed25519PrivateKeyV is not (0 or 1)) return ExternalHere;
            return SelfRefusals.TryGetValue(processId, out var sentence) ? sentence : null;
        }
        var reported = await states.GetAsync(nodeId);
        if (reported?.Extra is null || !reported.Extra.TryGetValue(NodeStateExtraKeys.IdentityRekey, out var code)) return null;
        return code switch
        {
            NodeStateExtraKeys.RekeyExternal => ExternalThere,
            NodeStateExtraKeys.RekeyDevices => DevicesThere,
            NodeStateExtraKeys.RekeyEvents => EventsThere,
            NodeStateExtraKeys.RekeyBusy => BusyThere,
            _ => null,
        };
    }

    /// <summary>
    /// This node re-keys for <paramref name="processId"/> unless it already did (its step-1 row of that process no longer shows its baseline key). The
    /// driver calls it for itself on every pass of S1; an asked shared node calls it when it serves the request. Needs this node open and its seed in
    /// its database; a refusal is kept for the page and said in <c>node_state</c>.
    /// </summary>
    public async Task RekeySelfForAsync(Guid processId, CancellationToken ct = default)
    {
        var identity = await nodes.GetAsync();
        if (identity is null || BlindNodeId.IsBlind(identity.NodeId)) return;
        var store = new OffboardingStore(db);
        var now = NodeStateDraft.IdentityFingerprint(identity.Ed25519PublicKey);
        await store.EnsureNodeAsync(processId, identity.NodeId, OffboardingSteps.Rekey, now);
        var row = (await store.NodesAsync(processId)).FirstOrDefault(r => r.NodeId == identity.NodeId && r.Step == OffboardingSteps.Rekey);
        if (row is null || row.State != "waiting") return;
        if (row.Baseline is not null && !string.Equals(row.Baseline, now, StringComparison.OrdinalIgnoreCase))
        {
            await store.SetNodeStateAsync(processId, identity.NodeId, OffboardingSteps.Rekey, "confirmed");
            return;
        }
        if (identity.Ed25519PrivateKeyV is not (0 or 1) || !session.IsUnlocked) return;   // said by WhyWaitingAsync and by node_state

        var result = await rekey.RekeyAsync(EventApplier.RemovalRekeyReason(processId), Actor, ct, despiteLaggards: true);
        if (result.Done)
        {
            SelfRefusals.TryRemove(processId, out _);
            IdentityRekeyRefusals.Clear(db.DatabaseId);
            await store.SetNodeStateAsync(processId, identity.NodeId, OffboardingSteps.Rekey, "confirmed");
            logger.LogWarning("Removal {Process}: this node has a new identity key ({Fingerprint}).", processId, result.NewFingerprint);
            return;
        }
        if (result.Refusal is { } refusal)
        {
            SelfRefusals[processId] = refusal;
            if (IdentityRekeyRefusals.CodeOf(refusal) is { } code)
            {
                IdentityRekeyRefusals.Record(db.DatabaseId, code);
                announcer?.RequestImmediateCheck();
            }
            logger.LogInformation("Removal {Process}: this node could not change its identity key yet ({Code}).", processId,
                IdentityRekeyRefusals.CodeOf(refusal) ?? "locked");
        }
    }

    /// <summary>The nodes this node already asked for a new key for <paramref name="processId"/> (its own requests in its log).</summary>
    private async Task<HashSet<Guid>> AskedAsync(Guid processId)
    {
        var self = (await nodes.GetAsync())?.NodeId ?? Guid.Empty;
        var asked = new HashSet<Guid>();
        foreach (var request in await RequestsAsync(db, self, processId))
            asked.UnionWith(request);
        return asked;
    }

    /// <summary>The node lists of every <c>node_rekey_request</c> that <paramref name="author"/> wrote for <paramref name="processId"/>.</summary>
    internal static async Task<List<List<Guid>>> RequestsAsync(IDbConnectionFactory db, Guid author, Guid processId)
    {
        using var conn = db.CreateConnection();
        var payloads = await conn.QueryAsync<string>(
            "SELECT payload FROM tbl_event WHERE event_type = @Type AND node_id = @Author COLLATE NOCASE",
            new { Type = EventTypes.NodeRekeyRequest, Author = author.ToString("D") });
        var lists = new List<List<Guid>>();
        foreach (var payload in payloads)
        {
            NodeRekeyRequestPayload? p;
            try { p = JsonSerializer.Deserialize<NodeRekeyRequestPayload>(payload); }
            catch (JsonException) { continue; }
            if (p is null || !Guid.TryParse(p.ProcessId, out var id) || id != processId) continue;
            lists.Add(p.Nodes.Select(n => Guid.TryParse(n, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList());
        }
        return lists;
    }
}
