using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// An <c>accounts_home_set</c> of a take-over or a first designation names a node that has not reported yet whether it holds a superadmin account of its
/// own: deferred, not refused, until that node's node_state is here.
/// </summary>
public sealed class AccountsHomeTargetUnreportedException(Guid eventId, Guid target)
    : UnauthorizedAccessException($"accounts_home_set {eventId}: node {target} has not reported whether it holds a superadmin account of its own yet."),
      IDeferrableSyncFailure;

public partial class EventApplier
{
    /// <summary>Applies this node's own newly signed <c>accounts_home_set</c> event through the applier's rules.</summary>
    public Task ApplyOwnAccountsHomeAsync(SyncEvent evt) =>
        evt.EventType == EventTypes.AccountsHomeSet
            ? ApplyAccountsHomeSetAsync(evt, isLocalSet: true)
            : throw new ArgumentException($"{evt.EventType} is not an accounts_home_set event.", nameof(evt));

    private async Task ApplyAccountsHomeSetAsync(SyncEvent evt, bool isLocalSet = false)
    {
        var p = Deserialize<AccountsHomeSetPayload>(evt.Payload);
        if (p.NodeId == Guid.Empty)
            throw new InvalidDataException($"accounts_home_set {evt.EventId}: node_id must not be empty.");
        if (BlindNodeId.IsBlind(p.NodeId))
            throw new InvalidDataException($"accounts_home_set {evt.EventId}: blind node {p.NodeId} cannot be the accounts node.");

        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);
        var existing = await ReadAccountsHomeAsync();
        if (existing is not null)
        {
            var existingVer = new RowVersion(existing.LamportTs, existing.SourceNodeId ?? Guid.Empty);
            if (!ConflictResolver.IncomingWins(existingVer, incoming))
                return;
        }

        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (!isLocalSet)
        {
            if (existing is not null && evt.NodeId != existing.NodeId)
            {
                var isCurrentHomeRevoked = (localIdentity is null || existing.NodeId != localIdentity.NodeId)
                    && (await whitelistRepo.GetByNodeIdAsync(existing.NodeId, includeDeleted: true))?.Status == WhitelistStatuses.Revoked;
                if (!isCurrentHomeRevoked)
                {
                    throw new UnauthorizedAccessException(
                        $"accounts_home_set {evt.EventId}: only the current accounts node {existing.NodeId} may move accounts_home while it is active.");
                }
            }

            if (localIdentity is not null && p.NodeId == localIdentity.NodeId)
            {
                if (!await IsSelfMarkedSharedOnlyAsync(localIdentity.NodeId))
                {
                    throw new UnauthorizedAccessException(
                        $"accounts_home_set {evt.EventId}: target node {p.NodeId} (this node) is not marked shared.");
                }
            }
            else
            {
                var target = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
                // A node that names ITSELF (plan B3): the accounts node is shared by definition. It cannot publish that mark
                // itself - a node never changes its own ownership - so its row here is taken as it is: an active, non-blind, whitelisted
                // superadmin full node is enough (rule ONE was checked above: no home yet, it is the current home, or the current home is
                // revoked). Its ownership row is not written: everywhere this node asks whether a node is shared, the accounts node counts
                // as shared because it is the accounts node (IsThisNodeSharedAsync, the doors status, the Devices page), so the ownership
                // concern keeps one writer and does not diverge between receivers. One exception: a row a superadmin here marked PERSONAL -
                // somebody's own device - is an explicit answer that it is not shared, and it must not hold every superadmin's door (the case
                // the shared-mark guard covers); it is refused until that mark is changed.
                var selfDesignation = p.NodeId == evt.NodeId;
                if (target is null
                    || target.Status != WhitelistStatuses.Active
                    || BlindNodeId.IsBlind(target.NodeId)
                    || (selfDesignation
                        ? !target.IsSuperadmin || OwnershipKinds.NormalizeOrNull(target.Ownership) == OwnershipKinds.Personal
                        : target.Ownership != OwnershipKinds.Shared))
                {
                    throw new UnauthorizedAccessException(selfDesignation
                        ? $"accounts_home_set {evt.EventId}: node {p.NodeId} names itself the accounts node, but here it is not an active, non-blind, whitelisted superadmin full node, or it is marked as somebody's personal device."
                        : $"accounts_home_set {evt.EventId}: target node {p.NodeId} must be an active, non-blind, whitelisted shared full node.");
                }
                // without a shared mark, a node names itself only at bootstrap (no accounts node was ever designated here) or
                // when it is the accounts node of record already. In the window where the accounts node of record is revoked here, any superadmin
                // full node could crown itself otherwise (a leaver revokes the honest accounts node, then names a node of theirs); there the node
                // must already be marked shared here - a take-over node is a shared node, so the take-over keeps working.
                if (selfDesignation && existing is not null && existing.NodeId != evt.NodeId
                    && OwnershipKinds.NormalizeOrNull(target.Ownership) != OwnershipKinds.Shared)
                    throw new UnauthorizedAccessException(
                        $"accounts_home_set {evt.EventId}: node {p.NodeId} names itself the accounts node while the accounts node of record {existing.NodeId} is revoked here; only a node marked shared here may do that.");
            }

            // a node that holds only mirrors of another node's list would publish lists of nobody, which every shared node ignores
            // ("empty list"), so the doors it holds - a removed person's among them - would stay live everywhere, silently. Only the current accounts
            // node may hand over to such a node (it knows what it hands over); a take-over or a first designation must name a node where a superadmin
            // has an account of their own: this node by its own rows, another by its node_state report.
            var handOver = existing is not null && evt.NodeId == existing.NodeId;
            if (!handOver && sharedDoorsStateRepo is not null)
            {
                var own = await HoldsOwnSuperadminAsync(p.NodeId, localIdentity?.NodeId);
                // No report of that node here yet: held and tried again (its node_state, which the designating node sends first, is on its way).
                if (own is null) throw new AccountsHomeTargetUnreportedException(evt.EventId, p.NodeId);
                if (own == false)
                    throw new UnauthorizedAccessException(
                        $"accounts_home_set {evt.EventId}: target node {p.NodeId} holds no superadmin account of its own; only the current accounts node hands over to such a node.");
            }
        }

        var changed = existing is null || existing.NodeId != p.NodeId;
        await WriteAccountsHomeAsync(new AccountsHomeRecord(
            p.NodeId,
            evt.LamportTs,
            evt.NodeId,
            evt.EventId,
            DateTime.UtcNow));

        // a designation made HERE, by a superadmin of this node, is no surprise to this node - it clears the attention flag
        // (that is how the accounts node acknowledges a change it made itself); one that arrived from another node raises it, until a superadmin
        // acknowledges it on the Devices page.
        if (isLocalSet && sharedDoorsStateRepo is not null)
            await sharedDoorsStateRepo.ClearAccountsHomeChangedAsync();

        if (changed)
        {
            if (sharedDoorsStateRepo is not null && !isLocalSet)
                await sharedDoorsStateRepo.MarkAccountsHomeChangedAsync();
            if (!isLocalSet)
                await WriteAccountsHomeAuditAsync(p.NodeId, existing?.NodeId, evt.NodeId);
            nodeStateAnnouncer?.RequestImmediateCheck();
        }

        if (localIdentity is not null)
        {
            if (p.NodeId == localIdentity.NodeId)
            {
                if (changed && sharedDoorsStateRepo is not null)
                    await sharedDoorsStateRepo.InvalidatePublishedDigestAsync();
                if (superadminDoorsNotifier is not null)
                    await superadminDoorsNotifier.NotifyChangedAsync(immediate: true);
            }
            else if (sharedDoorsStateRepo is not null)
            {
                var state = await sharedDoorsStateRepo.GetAsync();
                if (state.PendingApply
                    && state.LatestAuthorNodeId == p.NodeId
                    && await IsThisNodeSharedAsync(p.NodeId)
                    && sharedDoorsApplier is not null)
                {
                    await sharedDoorsApplier.ApplyPendingAsync();
                }
            }
        }

        logger.LogInformation("Applied accounts_home_set: {NodeId} (ts={Ts})", p.NodeId, evt.LamportTs);
    }

    private async Task ApplySuperadminDoorsSetAsync(SyncEvent evt)
    {
        var p = Deserialize<SuperadminDoorsSetPayload>(evt.Payload);
        if (p.Version <= 0)
            throw new InvalidDataException($"superadmin_doors_set {evt.EventId}: version must be positive.");

        if (sharedDoorsStateRepo is null)
        {
            // Blind nodes do not apply door lists; they store and relay the event.
            return;
        }

        var accountsHome = await ReadAccountsHomeAsync();
        if (accountsHome is not null && evt.NodeId != accountsHome.NodeId)
        {
            logger.LogWarning(
                "Ignoring superadmin_doors_set {EventId} from {Author}: active accounts node is {Home}",
                evt.EventId, evt.NodeId, accountsHome.NodeId);
            return;
        }

        var state = await sharedDoorsStateRepo.GetAsync();
        if (accountsHome is not null
            && state.LatestAuthorNodeId == accountsHome.NodeId
            && p.Version <= state.LatestReceivedVersion
            && !await SignedByANewerKeyThanTheLatestListAsync(evt, state.LatestEventId))
        {
            return;
        }

        await sharedDoorsStateRepo.RecordReceivedAsync(p.Version, evt.EventId, evt.NodeId, evt.Payload);

        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity is null)
            return;

        if (accountsHome is not null && evt.NodeId == accountsHome.NodeId)
        {
            if (localIdentity.NodeId == accountsHome.NodeId)
            {
                await sharedDoorsStateRepo.ClearPendingAsync();
                return;
            }

            if (!await IsThisNodeSharedAsync(accountsHome.NodeId))
            {
                await sharedDoorsStateRepo.ClearPendingAsync();
                return;
            }

            if (sharedDoorsApplier is not null)
                await sharedDoorsApplier.ApplyPendingAsync();
        }
    }

    private async Task RecordSelfOwnershipIfRemoteAsync(SyncEvent evt, Guid localNodeId, string? rawOwnership, bool repairing)
    {
        if (repairing || sharedDoorsStateRepo is null || evt.NodeId == localNodeId)
            return;

        var normalized = OwnershipKinds.NormalizeOrNull(rawOwnership);
        if (!OwnershipKinds.IsKnown(normalized))
            return;

        var state = await sharedDoorsStateRepo.GetAsync();
        var existingVer = state.SelfOwnershipSourceNodeId is { } src
            ? new RowVersion(state.SelfOwnershipLamportTs, src)
            : new RowVersion(state.SelfOwnershipLamportTs, Guid.Empty);
        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);
        if (!ConflictResolver.IncomingWins(existingVer, incoming))
            return;

        await sharedDoorsStateRepo.SetSelfOwnershipAsync(normalized!, evt.LamportTs, evt.NodeId);

        if (normalized == OwnershipKinds.Personal && sharedDoorsApplier is not null)
        {
            await sharedDoorsApplier.PurgeReplicatedForPersonalAsync();
            return;
        }

        if (normalized == OwnershipKinds.Shared
            && state.LatestReceivedVersion > state.AppliedVersion
            && !string.IsNullOrEmpty(state.LatestPayloadJson)
            && sharedDoorsApplier is not null)
        {
            var home = await ReadAccountsHomeAsync();
            if (home is not null && state.LatestAuthorNodeId == home.NodeId)
                await sharedDoorsApplier.ApplyPendingAsync();
        }
    }

    /// <summary>
    /// a list signed by the author's CURRENT key replaces the latest list when that one was signed by a key the author has
    /// retired since, whatever their versions. Somebody who held the accounts node's old key could plant a list with a huge version; once the
    /// accounts node re-keyed (S1) its next list wins here, and the version gate starts again from that list.
    /// </summary>
    private async Task<bool> SignedByANewerKeyThanTheLatestListAsync(SyncEvent incoming, Guid? latestEventId)
    {
        if (latestEventId is not { } latestId) return false;
        var author = await whitelistRepo.GetByNodeIdAsync(incoming.NodeId);
        if (author is not { Ed25519PublicKey.Length: > 0 }) return false;
        var current = author.Ed25519PublicKey;
        if (!NodeEventVerifier.SignatureVerifies(current, EventSignature.BuildPayload(incoming), incoming.Signature)) return false;
        var latest = await eventLogRepo.GetByIdAsync(latestId.ToString("D"));
        if (latest is null) return false;
        var latestByCurrentKey = NodeEventVerifier.SignatureVerifies(current, EventSignature.BuildPayload(latest), latest.Signature);
        if (!latestByCurrentKey)
            logger.LogWarning("superadmin_doors_set {EventId} from {Author} (version {Version}) replaces a list signed by a key the author has retired",
                incoming.EventId, incoming.NodeId, JsonSerializerVersion(incoming.Payload));
        return !latestByCurrentKey;
    }

    private static long JsonSerializerVersion(string payload)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<SuperadminDoorsSetPayload>(payload)?.Version ?? 0; }
        catch (System.Text.Json.JsonException) { return 0; }
    }

    /// <summary>A whole number a node_state report carries under <paramref name="key"/>; -1 when it carries none.</summary>
    public static long NodeStateNumber(string? extraJson, string key)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return -1;
        try
        {
            var extra = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(extraJson);
            return extra is not null && extra.TryGetValue(key, out var text)
                   && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1;
        }
        catch (System.Text.Json.JsonException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Whether <paramref name="nodeId"/> holds a superadmin account of its own (not a mirror of another node's list): this node by its rows, another
    /// node by the <c>local_superadmin_door_count</c> of its latest <c>node_state</c> report; null when that node has not reported it yet.
    /// </summary>
    internal async Task<bool?> HoldsOwnSuperadminAsync(Guid nodeId, Guid? self)
    {
        using var conn = connFactory.CreateConnection();
        if (self == nodeId)
            return await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM tbl_user WHERE is_active = 1 AND replicated = 0 AND lower(role) = 'superadmin' AND account_state = 'active'") > 0;
        var extra = await conn.ExecuteScalarAsync<string?>(
            "SELECT extra_json FROM tbl_node_state WHERE node_id = @Id COLLATE NOCASE", new { Id = nodeId.ToString("D") });
        var doors = OwnSuperadminDoors(extra);
        return doors < 0 ? null : doors > 0;
    }

    /// <summary>The <c>local_superadmin_door_count</c> a node_state report carries; -1 when it carries none.</summary>
    public static long OwnSuperadminDoors(string? extraJson) => NodeStateNumber(extraJson, "local_superadmin_door_count");

    private async Task<bool> IsThisNodeSharedAsync(Guid? accountsHomeNodeId)
    {
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity is null || BlindNodeId.IsBlind(localIdentity.NodeId))
            return false;

        if (accountsHomeNodeId == localIdentity.NodeId)
            return true;

        var state = sharedDoorsStateRepo is not null
            ? await sharedDoorsStateRepo.GetAsync()
            : null;
        if (state?.SelfOwnership == OwnershipKinds.Shared)
            return true;

        var resolved = await WhitelistHistory.ResolveSelfOwnershipAsync(connFactory, localIdentity.NodeId);
        if (resolved?.Ownership == OwnershipKinds.Shared)
        {
            if (sharedDoorsStateRepo is not null && resolved.Version is { } ver)
                await sharedDoorsStateRepo.SetSelfOwnershipAsync(OwnershipKinds.Shared, ver.LamportTs, ver.SourceNodeId);
            return true;
        }

        return false;
    }

    private async Task<bool> IsSelfMarkedSharedOnlyAsync(Guid localNodeId)
    {
        var state = sharedDoorsStateRepo is not null
            ? await sharedDoorsStateRepo.GetAsync()
            : null;
        if (state?.SelfOwnership == OwnershipKinds.Shared)
            return true;

        var resolved = await WhitelistHistory.ResolveSelfOwnershipAsync(connFactory, localNodeId);
        if (resolved?.Ownership == OwnershipKinds.Shared)
        {
            if (sharedDoorsStateRepo is not null && resolved.Version is { } ver)
                await sharedDoorsStateRepo.SetSelfOwnershipAsync(OwnershipKinds.Shared, ver.LamportTs, ver.SourceNodeId);
            return true;
        }

        return false;
    }

    private async Task WriteAccountsHomeAuditAsync(Guid newHomeId, Guid? previousHomeId, Guid authorNodeId)
    {
        using var conn = connFactory.CreateConnection();
        var details = previousHomeId is { } prev
            ? $"Accounts node changed from {prev:D} to {newHomeId:D} (received from node {authorNodeId:D})"
            : $"Accounts node set to {newHomeId:D} (received from node {authorNodeId:D})";
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_audit_log (entity_type, entity_id, action, actor_type, created_at, details)
              VALUES ('accounts_home', @EntityId, 'accounts_home_set', 'remote-peer', @CreatedAt, @Details)",
            new
            {
                EntityId = newHomeId.ToString("D"),
                CreatedAt = DateTime.UtcNow.ToString("O"),
                Details = details,
            });
    }

    private async Task<AccountsHomeRecord?> ReadAccountsHomeAsync()
    {
        if (accountsHomeRepo is not null)
            return await accountsHomeRepo.GetAsync();

        using var conn = connFactory.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<(string NodeId, long LamportTs, string? SourceNodeId, string? EventId, string UpdatedAt)?>(
            "SELECT node_id AS NodeId, lamport_ts AS LamportTs, source_node_id AS SourceNodeId, event_id AS EventId, updated_at AS UpdatedAt FROM tbl_accounts_home WHERE id = 1");
        if (row is not { } r || !Guid.TryParseExact(r.NodeId, "D", out var nodeId))
            return null;
        Guid? sourceNodeId = Guid.TryParseExact(r.SourceNodeId, "D", out var src) ? src : null;
        Guid? eventId = Guid.TryParseExact(r.EventId, "D", out var evt) ? evt : null;
        var updatedAt = DateTime.TryParse(r.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTime.UtcNow;
        return new AccountsHomeRecord(nodeId, r.LamportTs, sourceNodeId, eventId, updatedAt);
    }

    private async Task WriteAccountsHomeAsync(AccountsHomeRecord record)
    {
        if (accountsHomeRepo is not null)
        {
            await accountsHomeRepo.UpsertAsync(record);
            return;
        }

        using var conn = connFactory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_accounts_home (id, node_id, lamport_ts, source_node_id, event_id, updated_at)
              VALUES (1, @NodeId, @LamportTs, @SourceNodeId, @EventId, @UpdatedAt)
              ON CONFLICT(id) DO UPDATE SET
                  node_id = excluded.node_id,
                  lamport_ts = excluded.lamport_ts,
                  source_node_id = excluded.source_node_id,
                  event_id = excluded.event_id,
                  updated_at = excluded.updated_at",
            new
            {
                NodeId = record.NodeId.ToString("D"),
                record.LamportTs,
                SourceNodeId = record.SourceNodeId?.ToString("D"),
                EventId = record.EventId?.ToString("D"),
                UpdatedAt = record.UpdatedAt.ToString("O"),
            });
    }
}
