using System.Globalization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.SharedDoors;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>
/// Step S2 of a removal with the doors list (release 2.12.0): the accounts node publishes <c>superadmin_doors_set</c> without the people being
/// removed, and each shared full node confirms in its <c>node_state</c> that it applied that list version (<c>applied_doors_version</c>) and keeps no
/// superadmin door of its own any more (<c>local_superadmin_door_count</c> = 0). The list goes out only from the accounts node: when the accounts node
/// is another node, or none is set, the step waits and says what to set (<see cref="WhyNotPublishingAsync"/>).
/// </summary>
public sealed class SharedDoorsStep(
    SuperadminDoorsPublisher publisher,
    IAccountsHomeRepository homes,
    INodeIdentityRepository nodes,
    IWhitelistRepository whitelist,
    NodeStateStore states,
    SessionService session,
    IDbConnectionFactory db) : ISharedDoorsStep
{
    public const string NoAccountsNode =
        "No accounts node is set. Make this node the accounts node (Admin > Devices, Accounts node): the shared nodes take their list of superadmins " +
        "from it, and the list without this person then goes out from here.";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public async Task<string?> WhyNotPublishingAsync(CancellationToken ct = default)
    {
        var self = (await nodes.GetAsync())?.NodeId;
        if (self is null) return "This node is not set up.";
        var home = await homes.GetAsync();
        if (home is null) return NoAccountsNode;
        if (home.NodeId != self)
        {
            var row = await whitelist.GetByNodeIdAsync(home.NodeId, includeDeleted: true);
            var name = string.IsNullOrWhiteSpace(row?.DisplayName) ? home.NodeId.ToString("D") : row!.DisplayName;
            // An accounts node of a person being removed, or one such a person made the accounts node: the administrator is
            // never sent there to hand anything over.
            if (row?.Status != WhitelistStatuses.Revoked && await IsTheRemovedPersonsAsync(home.NodeId, row?.OwnerPersonId))
                return $"The accounts node, {name}, belongs to a person being removed, or such a person made it the accounts node. Do not hand " +
                       "anything over there: revoke it (its item in the review below, or Admin > Devices), then make this node the accounts node.";
            return row?.Status == WhitelistStatuses.Revoked
                ? $"The accounts node, {name}, was revoked. Make this node the accounts node (Admin > Devices, Accounts node); the list without this " +
                  "person then goes out from here."
                : $"The shared nodes take their list of superadmins from the accounts node, {name}. Hand the accounts node over to this node there " +
                  "(Admin > Devices, Accounts node), or run the removal on that node.";
        }
        if (!session.IsUnlocked) return "This node is locked. Open this node once (sign in or unlock it): the list is signed with its key.";
        var behind = (await whitelist.GetAllActiveAsync())
            .Where(p => p.NodeId != self && !BlindNodeId.IsBlind(p.NodeId) && OwnershipKinds.NormalizeOrNull(p.Ownership) == OwnershipKinds.Shared
                        && !MetadataCapabilities.HasSharedDoorsV1(MetadataCapabilities.Parse(p.LastCapabilities)))
            .Select(p => string.IsNullOrWhiteSpace(p.DisplayName) ? p.NodeId.ToString("D") : p.DisplayName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return behind.Count > 0
            ? $"These shared nodes run a version that cannot take the list yet: {string.Join(", ", behind)}. Update them, or revoke them."
            : null;
    }

    /// <summary>Whether <paramref name="nodeId"/> is a device of a person being (or already) removed, or one such a person made the accounts node.</summary>
    private async Task<bool> IsTheRemovedPersonsAsync(Guid nodeId, Guid? ownerPersonId)
    {
        using var conn = db.CreateConnection();
        if (ownerPersonId is { } owner && await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn,
                @"SELECT (SELECT COUNT(*) FROM tbl_offboarding WHERE person_id = @p COLLATE NOCASE)
                       + (SELECT COUNT(*) FROM tbl_removed_person WHERE person_id = @p COLLATE NOCASE)",
                new { p = owner.ToString("D") }) > 0)
            return true;
        // The latest designation of that node in this node's audit, by a user whose removal is on record here.
        var details = await Dapper.SqlMapper.ExecuteScalarAsync<string?>(conn,
            @"SELECT details FROM tbl_audit_log WHERE action = 'accounts_home_set' AND entity_id = @n COLLATE NOCASE ORDER BY id DESC LIMIT 1",
            new { n = nodeId.ToString("D") });
        var by = details is null ? null : System.Text.RegularExpressions.Regex.Match(details, @"\bby user #?(\d+)\b");
        return by is { Success: true } && int.TryParse(by.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)
            && await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM tbl_offboarding WHERE user_id = @u", new { u = uid }) > 0;
    }

    public async Task<long> PublishWithoutAsync(IReadOnlyCollection<Guid> removedPersonIds, CancellationToken ct = default)
    {
        if (await WhyNotPublishingAsync(ct) is not null) return 0;
        return await publisher.PublishForRemovalAsync(removedPersonIds, ct);
    }

    /// <summary>
    /// Confirms that the shared node applied a list THIS node wrote: the event it reports as applied
    /// is a <c>superadmin_doors_set</c> in this node's own log, written here (<c>origin_peer IS NULL</c>), with a version at least the one S2
    /// published; and it keeps no superadmin door of its own and holds no mirror of anybody being removed. A version number alone is not enough
    /// (a list forged with a stolen seed of this node can carry any version), but a forged list never enters this node's log: the pull path skips
    /// an event signed with this node's key that it did not write, and the push path refuses it. A newer genuine list - a password change, a
    /// second removal, a shared node joining - therefore confirms as well and never wedges the removal.
    /// </summary>
    public async Task<OffboardingNodeReport> ReportAsync(Guid nodeId, long listVersion, CancellationToken ct = default)
    {
        var state = await states.GetAsync(nodeId);
        if (state?.Extra is null || listVersion <= 0) return new OffboardingNodeReport(nodeId, false);
        var applied = Number(state.Extra, SharedDoorsNodeStateContributor.AppliedDoorsVersionKey);
        var own = Number(state.Extra, SharedDoorsNodeStateContributor.LocalSuperadminDoorCountKey);
        var removed = Number(state.Extra, SharedDoorsNodeStateContributor.RemovedMirrorsKey);
        var appliedEvent = state.Extra.TryGetValue(SharedDoorsNodeStateContributor.AppliedDoorsEventKey, out var e) && Guid.TryParse(e, out var g) ? g : (Guid?)null;
        var exact = appliedEvent is { } reported && await OwnListVersionAsync(reported) is { } ownVersion && ownVersion >= listVersion;
        if (exact && own == 0 && removed == 0) return new OffboardingNodeReport(nodeId, true);
        if (!exact && applied < listVersion)
            return new OffboardingNodeReport(nodeId, false,
                $"It has not applied the new list of superadmins yet (version {listVersion}; it reports {Math.Max(applied, 0)}). It does so when it is open.");
        if (!exact)
            return new OffboardingNodeReport(nodeId, false,
                $"It reports a list of superadmins that this node did not write, or one older than the list this removal published (version {listVersion}; it reports {applied}). A list signed " +
                "with this node's old key may be in place there. It takes the published list at its next sync once this node has its new identity key; " +
                "if it does not, revoke it.");
        if (own != 0)
            return new OffboardingNodeReport(nodeId, false, own < 0
                ? "It does not report whether it keeps superadmin doors of its own. Update it, or revoke it."
                : $"It applied the list but still keeps {own} superadmin door(s) of its own. They go when a usable list is applied there; see the Devices page of that node.");
        return new OffboardingNodeReport(nodeId, false, removed < 0
            ? "It does not report whether it still holds the account of the person being removed. Update it, or revoke it."
            : "It still holds the account of a person being removed. It goes when that node applies the published list or the removal's tombstone.");
    }

    /// <summary>The version of the list <paramref name="eventId"/> when it is one THIS node wrote (in its log, written here); null otherwise.</summary>
    private async Task<long?> OwnListVersionAsync(Guid eventId)
    {
        var self = (await nodes.GetAsync())?.NodeId;
        if (self is null) return null;
        using var conn = db.CreateConnection();
        return await Dapper.SqlMapper.ExecuteScalarAsync<long?>(conn,
            @"SELECT CAST(json_extract(payload, '$.version') AS INTEGER) FROM tbl_event
              WHERE event_id = @Id COLLATE NOCASE AND event_type = @Type AND node_id = @Self COLLATE NOCASE AND origin_peer IS NULL",
            new { Id = eventId.ToString("D"), Type = EventTypes.SuperadminDoorsSet, Self = self.Value.ToString("D") });
    }

    private static long Number(IReadOnlyDictionary<string, string> extra, string key) =>
        extra.TryGetValue(key, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
}
