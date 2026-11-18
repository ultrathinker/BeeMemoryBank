using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.SharedDoors;

/// <summary>
/// Manages the replicated <c>accounts_home</c> setting and answers shared-node queries
/// (whether this node is shared, where accounts live, and which shared peers lack <c>shared-doors-v1</c>).
/// </summary>
public sealed class AccountsHomeService(
    INodeIdentityRepository nodeRepo,
    IAccountsHomeRepository accountsHomeRepo,
    ISharedDoorsStateRepository sharedDoorsStateRepo,
    IWhitelistRepository whitelistRepo,
    IWhitelistRepository whitelistRepoWrite,
    IUserRepository userRepo,
    IEventLogRepository eventLogRepo,
    IEventLogger eventLogger,
    ILamportClock clock,
    SessionService session,
    EventApplier applier,
    ISyncTrigger syncTrigger,
    ISyncProtocolPolicy policy,
    ISuperadminDoorsNotifier notifier,
    IDbConnectionFactory connFactory,
    ILogger<AccountsHomeService> logger,
    IAuditLogRepository? auditLogRepo = null,
    NodeStatePublisher? stateAnnouncer = null)
{
    /// <summary>
    /// A superadmin acknowledges that the accounts node changed: the <c>accounts_home_changed</c> attention flag goes, and an audit
    /// line says who saw it. A later change from another node raises it again. Returns false when the flag was not raised.
    /// </summary>
    public async Task<bool> AcknowledgeHomeChangeAsync(string? actor, string actorType = "user")
    {
        var state = await sharedDoorsStateRepo.GetAsync();
        if (!state.AccountsHomeChanged) return false;
        await sharedDoorsStateRepo.ClearAccountsHomeChangedAsync();
        if (auditLogRepo is not null)
            await auditLogRepo.LogAsync("accounts_home", (await accountsHomeRepo.GetAsync())?.NodeId.ToString("D") ?? "", "accounts_home_change_acknowledged",
                actorType, $"Change of the accounts node acknowledged by user {actor ?? "system"}");
        return true;
    }

    public async Task<Guid?> GetAccountsHomeNodeIdAsync() =>
        (await accountsHomeRepo.GetAsync())?.NodeId;

    /// <summary>Records self-ownership right after a join completes (e.g. <c>--shared</c>).</summary>
    public async Task RecordSelfOwnershipAtJoinAsync(string ownership, long lamportTs = 0, Guid? sourceNodeId = null)
    {
        var normalized = OwnershipKinds.NormalizeOrNull(ownership);
        if (OwnershipKinds.IsKnown(normalized))
            await sharedDoorsStateRepo.SetSelfOwnershipAsync(normalized!, lamportTs, sourceNodeId);
    }

    public async Task<(Guid? NodeId, string? DisplayName)> ResolveHomeAsync()
    {
        var home = await accountsHomeRepo.GetAsync();
        if (home is null) return (null, null);
        var identity = await nodeRepo.GetAsync();
        if (identity is not null && home.NodeId == identity.NodeId)
            return (home.NodeId, identity.DisplayName);
        var peer = await whitelistRepo.GetByNodeIdAsync(home.NodeId, includeDeleted: true);
        return (home.NodeId, peer?.DisplayName);
    }

    public async Task<bool> IsThisNodeAccountsHomeAsync()
    {
        var identity = await nodeRepo.GetAsync();
        if (identity is null || BlindNodeId.IsBlind(identity.NodeId)) return false;
        var home = await accountsHomeRepo.GetAsync();
        return home is not null && home.NodeId == identity.NodeId;
    }

    public async Task<bool> IsThisNodeSharedAsync()
    {
        var identity = await nodeRepo.GetAsync();
        if (identity is null || BlindNodeId.IsBlind(identity.NodeId)) return false;
        var home = await accountsHomeRepo.GetAsync();
        if (home is not null && home.NodeId == identity.NodeId) return true;
        return await IsPeerOwnedSharedAsync(sharedDoorsStateRepo, connFactory, identity.NodeId);
    }

    internal static async Task<bool> IsPeerOwnedSharedAsync(
        ISharedDoorsStateRepository stateRepo,
        IDbConnectionFactory connFactory,
        Guid selfNodeId)
    {
        var state = await stateRepo.GetAsync().ConfigureAwait(false);
        if (state.SelfOwnership == OwnershipKinds.Shared) return true;
        var resolved = await WhitelistHistory.ResolveSelfOwnershipAsync(connFactory, selfNodeId).ConfigureAwait(false);
        return resolved?.Ownership == OwnershipKinds.Shared;
    }

    public async Task<SharedDoorsStatus> GetStatusAsync()
    {
        var identity = await nodeRepo.GetAsync();
        var (homeId, homeName) = await ResolveHomeAsync();
        var isHome = identity is not null && !BlindNodeId.IsBlind(identity.NodeId) && homeId == identity.NodeId;
        var isShared = await IsThisNodeSharedAsync();

        var peers = await whitelistRepo.GetAllActiveAsync();
        var sharedPeers = peers
            .Where(p => !BlindNodeId.IsBlind(p.NodeId)
                        && (p.NodeId == homeId || OwnershipKinds.NormalizeOrNull(p.Ownership) == OwnershipKinds.Shared))
            .ToList();

        var blocked = sharedPeers
            .Where(p => p.NodeId != identity?.NodeId
                        && !MetadataCapabilities.HasSharedDoorsV1(MetadataCapabilities.Parse(p.LastCapabilities)))
            .Select(p => new SharedDoorPeerBlocker(p.NodeId, p.DisplayName))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var skipped = sharedPeers
            .Where(p => p.NodeId != identity?.NodeId && !IsValidRecipientPublicKey(p.Ed25519PublicKey))
            .Select(p => new SharedDoorPeerBlocker(p.NodeId, p.DisplayName))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var users = await userRepo.ListActiveAsync();
        var superadminCount = users.Count(u => string.Equals(u.Role, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase));
        var shouldOffer = superadminCount >= 2
                          || sharedPeers.Count > 0
                          || isShared
                          || homeId.HasValue;

        var state = await sharedDoorsStateRepo.GetAsync();
        return new SharedDoorsStatus(homeId, homeName, isHome, isShared, shouldOffer, blocked, state, skipped);
    }

    private static bool IsValidRecipientPublicKey(byte[] ed25519PublicKey)
    {
        try
        {
            _ = DekEnvelope.Ed25519PublicKeyToX25519PublicKey(ed25519PublicKey);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the network's single accounts node to <paramref name="targetNodeId"/>, publishes
    /// <c>accounts_home_set</c>, ensures the target node is treated as <c>shared</c>, and triggers
    /// <c>superadmin_doors_set</c> if this node is the new accounts home.
    /// </summary>
    public async Task<AccountsHomeRecord> SetAsync(Guid targetNodeId, CancellationToken ct = default, string? actor = null, string actorType = "system")
    {
        if (!session.IsUnlocked)
            throw new InvalidOperationException("Unlock the vault before setting the accounts node.");

        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");
        if (BlindNodeId.IsBlind(identity.NodeId))
            throw new InvalidOperationException("A blind node cannot manage or author accounts_home.");
        if (targetNodeId == Guid.Empty)
            throw new ArgumentException("Accounts node id must not be empty.", nameof(targetNodeId));
        if (BlindNodeId.IsBlind(targetNodeId))
            throw new InvalidOperationException("A blind node cannot be the accounts node.");

        var currentHome = await accountsHomeRepo.GetAsync();
        if (currentHome is not null && currentHome.NodeId != identity.NodeId)
        {
            var currentPeer = await whitelistRepo.GetByNodeIdAsync(currentHome.NodeId, includeDeleted: true);
            if (currentPeer?.Status != WhitelistStatuses.Revoked)
            {
                throw new InvalidOperationException(
                    $"Only the current accounts node ({currentHome.NodeId:D}) can hand over accounts_home while it is active.");
            }
        }

        if (targetNodeId == identity.NodeId)
        {
            var localState = await sharedDoorsStateRepo.GetAsync();
            if (localState.SelfOwnership == OwnershipKinds.Personal)
                throw new InvalidOperationException("A personal node cannot become the accounts node.");

            var users = await userRepo.ListActiveAsync();
            var hasLocalSuperadmin = users.Any(u =>
                !u.Replicated && string.Equals(u.Role, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase));
            var hasReplicated = users.Any(u => u.Replicated);
            if (hasReplicated && !hasLocalSuperadmin)
            {
                throw new InvalidOperationException(
                    "This shared node has only replicated superadmin accounts; set accounts_home from a node with local superadmin accounts.");
            }
        }
        else
        {
            var peer = await whitelistRepo.GetByNodeIdAsync(targetNodeId)
                ?? throw new InvalidOperationException("The accounts node must be an active whitelisted node.");
            // only the current accounts node hands over to a node that holds mirrors alone; a take-over or a first designation must
            // name a node where a superadmin has an account of their own (its node_state says so), or every node would refuse the change.
            var handingOver = currentHome is not null && currentHome.NodeId == identity.NodeId;
            if (!handingOver)
            {
                using var conn = connFactory.CreateConnection();
                var extra = await Dapper.SqlMapper.ExecuteScalarAsync<string?>(conn,
                    "SELECT extra_json FROM tbl_node_state WHERE node_id = @Id COLLATE NOCASE", new { Id = targetNodeId.ToString("D") });
                var doors = EventApplier.OwnSuperadminDoors(extra);
                if (doors < 0)
                    throw new InvalidOperationException("That node has not reported its accounts here yet. Let it sync once, then try again.");
                if (doors == 0)
                    throw new InvalidOperationException(
                        "That node holds no superadmin account of its own (only copies of another node's list), so it cannot manage the accounts. " +
                        "Choose a node where a superadmin has an account of their own.");
            }
            if (peer.Ownership != OwnershipKinds.Shared)
            {
                var version = await eventLogger.LogWhitelistUpdateAsync(
                    targetNodeId, apiAddress: null, displayName: null, ownership: OwnershipKinds.Shared);
                peer.Ownership = OwnershipKinds.Shared;
                peer.OwnerPersonId = null;
                peer.OwnerLabel = null;
                peer.NeedsReview = false;
                peer.UpdatedAt = DateTime.UtcNow;
                peer.SetVersionOf(WhitelistConcerns.Ownership, version);
                peer.SetRowVersion(version);
                await whitelistRepoWrite.UpdateAsync(peer);
            }
        }

        if (targetNodeId == identity.NodeId && stateAnnouncer is not null)
        {
            // the other nodes accept a take-over or a first designation only for a node that reported a superadmin account of its
            // own. This node's report goes out first, so it is there before the event that names this node.
            try
            {
                if (await stateAnnouncer.BuildDraftAsync(ct) is { } draft) await stateAnnouncer.PublishAsync(draft, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("The node state could not be announced before naming this node the accounts node ({Type}); the announcer repeats it.",
                    ex.GetType().Name);
            }
        }
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(new AccountsHomeSetPayload(targetNodeId));
        // An own event: signed and logged inside the event-write gate, with the identity read inside it: an identity re-key holds the
        // gate while it lists the node's own events and swaps the key, so this event is either on the rotation's list or signed by the new key.
        using var writeGate = await BeeMemoryBank.Sync.EventWriteGate.Instance.EnterAsync(ct);
        using var ownerFlow = BeeMemoryBank.Sync.EventWriteGate.EnterOwnerFlow();
        identity = await nodeRepo.GetAsync() ?? identity;
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = identity.NodeId,
            LamportTs = clock.Tick(),
            EventType = EventTypes.AccountsHomeSet,
            Payload = payloadJson,
            Signature = [],
            ProtocolVersion = policy.Declared,
            CreatedAt = DateTime.UtcNow,
            ActorType = "system",
            ActorName = identity.DisplayName,
        };
        evt.Signature = NodeIdentityVault.SignWithIdentityOrGetDek(
            identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
            identity.NodeId, session.GetMasterDek, EventSignature.BuildPayload(evt));

        await applier.ApplyOwnAccountsHomeAsync(evt);
        if (targetNodeId == identity.NodeId)
        {
            var localState = await sharedDoorsStateRepo.GetAsync();
            if (localState.SelfOwnership != OwnershipKinds.Shared)
                await sharedDoorsStateRepo.SetSelfOwnershipAsync(OwnershipKinds.Shared, evt.LamportTs, evt.NodeId);
        }
        await eventLogRepo.AppendAsync(evt);
        syncTrigger.Signal();

        if (currentHome is null || currentHome.NodeId != targetNodeId)
        {
            var (_, displayName) = await ResolveHomeAsync();
            var details = $"Accounts node set to '{displayName ?? targetNodeId.ToString("D")}' ({targetNodeId:D}) by user {actor ?? "system"}";
            if (auditLogRepo is not null)
            {
                await auditLogRepo.LogAsync("accounts_home", targetNodeId.ToString("D"), "accounts_home_set", actorType, details);
            }
            else
            {
                using var conn = connFactory.CreateConnection();
                await Dapper.SqlMapper.ExecuteAsync(
                    conn,
                    @"INSERT INTO tbl_audit_log (entity_type, entity_id, action, actor_type, created_at, details)
                      VALUES ('accounts_home', @EntityId, 'accounts_home_set', @ActorType, @CreatedAt, @Details)",
                    new { EntityId = targetNodeId.ToString("D"), ActorType = actorType, CreatedAt = DateTime.UtcNow.ToString("O"), Details = details });
            }
        }

        if (targetNodeId == identity.NodeId)
            await notifier.NotifyChangedAsync(immediate: true, ct);

        logger.LogInformation("Set accounts_home to {NodeId} (event {EventId})", targetNodeId, evt.EventId);
        return (await accountsHomeRepo.GetAsync())!;
    }
}
