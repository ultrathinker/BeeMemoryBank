using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>Reads and writes the replicated <c>tbl_accounts_home</c> singleton setting.</summary>
public interface IAccountsHomeRepository
{
    Task<AccountsHomeRecord?> GetAsync(IDbTransaction? transaction = null);

    Task UpsertAsync(AccountsHomeRecord record, IDbTransaction? transaction = null);
}

/// <summary>Reads and writes node-local shared-doors state in <c>tbl_shared_doors_state</c>.</summary>
public interface ISharedDoorsStateRepository
{
    Task<SharedDoorsStateRecord> GetAsync(IDbTransaction? transaction = null);

    Task SetSelfOwnershipAsync(string ownership, long lamportTs, Guid? sourceNodeId, IDbTransaction? transaction = null);

    /// <summary>
    /// The next list version: one above both the last version this node issued and <paramref name="atLeast"/> (the highest version this node has
    /// seen for its own author), atomically.
    /// </summary>
    Task<long> AllocateNextVersionAsync(IDbTransaction? transaction = null, long atLeast = 0);

    Task RecordPublishedDigestAsync(string digest, IDbTransaction? transaction = null);

    Task InvalidatePublishedDigestAsync(IDbTransaction? transaction = null);

    Task RecordIssuedAsync(long version, string digest, IDbTransaction? transaction = null);

    Task RecordReceivedAsync(long version, Guid eventId, Guid authorNodeId, string payloadJson, IDbTransaction? transaction = null);

    Task ClearPendingAsync(string? lastError = null, IDbTransaction? transaction = null);

    Task RecordAppliedAsync(long appliedVersion, Guid? primaryPersonId, string? lastError, IDbTransaction? transaction = null);

    Task RecordApplyErrorAsync(string error, IDbTransaction? transaction = null);

    Task MarkAccountsHomeChangedAsync(IDbTransaction? transaction = null);

    /// <summary>Clears the <c>accounts_home_changed</c> attention flag: a superadmin acknowledged it, or made the change here.</summary>
    Task ClearAccountsHomeChangedAsync(IDbTransaction? transaction = null);
}

/// <summary>Applies a pending <c>superadmin_doors_set</c> list on a shared node once unlocked.</summary>
public interface ISharedDoorsApplier
{
    Task<bool> ApplyPendingAsync(CancellationToken ct = default);

    /// <summary>Deletes every replicated mirror row and retires its door when self ownership changes to personal.</summary>
    Task<int> PurgeReplicatedForPersonalAsync(CancellationToken ct = default);
}

/// <summary>Publishes <c>superadmin_doors_set</c> on the accounts node when superadmins, doors, hints, or shared nodes change.</summary>
public interface ISuperadminDoorsNotifier
{
    Task NotifyChangedAsync(bool immediate = false, CancellationToken ct = default);
}
