using System.Globalization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Vault.SharedDoors;

/// <summary>
/// Contributes shared-node door status (<c>applied_doors_version</c>, <c>local_superadmin_door_count</c>,
/// and error attention flag) to self-authored <c>node_state</c> events.
/// </summary>
public sealed class SharedDoorsNodeStateContributor(
    ISharedDoorsStateRepository stateRepo,
    IUserRepository userRepo,
    IKeySlotRepository keySlotRepo,
    IDbConnectionFactory? db = null) : INodeStateContributor
{
    public const string AppliedDoorsVersionKey = "applied_doors_version";
    /// <summary>The event id of the list this node applied, when that is the latest it received: the removal confirms S2 by it.</summary>
    public const string AppliedDoorsEventKey = "applied_doors_event";
    /// <summary>How many superadmin mirrors this node still holds of people being removed or removed; 0 once S2 applied.</summary>
    public const string RemovedMirrorsKey = "removed_mirrors";
    public const string LocalSuperadminDoorCountKey = "local_superadmin_door_count";
    public const string SharedDoorsErrorFlag = "shared_doors_error";
    public const string AccountsHomeChangedFlag = "accounts_home_changed";
    /// <summary>A received list still waits after its automatic retries were spent.</summary>
    public const string SharedDoorsApplyStuckFlag = "shared_doors_apply_stuck";

    public async Task ContributeAsync(NodeStateDraft draft, CancellationToken ct = default)
    {
        var state = await stateRepo.GetAsync().ConfigureAwait(false);
        var users = await userRepo.ListActiveAsync().ConfigureAwait(false);
        var slots = await keySlotRepo.GetAllAsync().ConfigureAwait(false);

        var localSuperadminUserIds = users
            .Where(u => !u.Replicated && string.Equals(u.Role, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase))
            .Select(u => u.Id)
            .ToHashSet();
        var localSuperadminSlotIds = users
            .Where(u => !u.Replicated && string.Equals(u.Role, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase) && u.KeySlotId is > 0)
            .Select(u => u.KeySlotId!.Value)
            .ToHashSet();

        var localDoorCount = slots.Count(s =>
            (s.OwnerUserId is { } oid && localSuperadminUserIds.Contains(oid))
            || localSuperadminSlotIds.Contains(s.SlotId)
            || string.Equals(s.SlotType, KeySlotTypes.Password, StringComparison.Ordinal));

        draft.Extra[AppliedDoorsVersionKey] = state.AppliedVersion.ToString(CultureInfo.InvariantCulture);
        if (!state.PendingApply && state.AppliedVersion > 0 && state.AppliedVersion == state.LatestReceivedVersion && state.LatestEventId is { } applied)
            draft.Extra[AppliedDoorsEventKey] = applied.ToString("D");
        if (db is not null)
        {
            using var conn = db.CreateConnection();
            var removed = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn,
                @"SELECT COUNT(*) FROM tbl_user u
                  WHERE u.is_active = 1 AND u.replicated = 1
                    AND (u.account_state = 'offboarding'
                         OR EXISTS (SELECT 1 FROM tbl_removed_person r WHERE r.person_id = u.person_id COLLATE NOCASE))").ConfigureAwait(false);
            draft.Extra[RemovedMirrorsKey] = removed.ToString(CultureInfo.InvariantCulture);
        }
        draft.Extra[LocalSuperadminDoorCountKey] = localDoorCount.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(state.LastError))
            draft.AttentionFlags.Add(SharedDoorsErrorFlag);
        if (state.AccountsHomeChanged)
            draft.AttentionFlags.Add(AccountsHomeChangedFlag);
        if (state.PendingApply && state.LastError == SharedDoorsApplier.ApplyStuckError)
            draft.AttentionFlags.Add(SharedDoorsApplyStuckFlag);
    }
}
