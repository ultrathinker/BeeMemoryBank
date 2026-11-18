using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Applies the tombstones of removed people (release 2.12.0, plan R1) to this node's accounts, and locks a person out while a replicated removal of
/// them is open (plan E2). Runs when a <c>person_removed</c> event applies, when the database opens (after a restore or a join the tombstones are in
/// <c>tbl_removed_person</c> before anybody signs in) and after a network restore.
/// <list type="bullet">
/// <item><description>An account a removal process on THIS node still holds is left alone: that process closes it at S5.</description></item>
/// <item><description><c>demoted</c>: where this node kept the removal's own record (the accounts node) the account is demoted as that record says; an
/// account that is already no superadmin is left as it is. Anywhere else the account is deleted: replicas hold superadmins only, and the role and
/// folders of a demotion never travel.</description></item>
/// <item><description><c>deleted</c>: the account is deleted, with its doors, even the last door a person can open while any other door opens the node
///. Only the node's very last door is kept: the account then stays locked out (no sign-in, no rights, no join, no reset) and a
/// warning says so.</description></item>
/// </list>
/// </summary>
public sealed class RemovedPersonEnforcer(
    IDbConnectionFactory db,
    IUserRepository users,
    IServiceProvider services,
    ILogger<RemovedPersonEnforcer>? logger = null) : IRemovedPersonEnforcer, IOffboardingReplicaObserver
{
    // Resolved when needed, not injected: the event applier depends on this enforcer, and the user service (through the recovery-box publisher)
    // on the applier; a constructor dependency would be a cycle the container cannot see through the factory registrations.
    private UserService UserService => (UserService)(services.GetService(typeof(UserService))
        ?? throw new InvalidOperationException("The user service is not registered on this host."));

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<int> EnforceAsync(Guid? personId = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            List<(string PersonId, string Outcome)> tombstones;
            using (var conn = db.CreateConnection())
            {
                if (!await TableExistsAsync(conn, "tbl_removed_person")) return 0;
                tombstones = (await conn.QueryAsync<(string, string)>(
                    personId is { } one
                        ? "SELECT person_id, outcome FROM tbl_removed_person WHERE person_id = @p COLLATE NOCASE"
                        : "SELECT person_id, outcome FROM tbl_removed_person",
                    new { p = personId?.ToString("D") })).ToList();
            }
            if (tombstones.Count == 0) return 0;

            var byPerson = (await users.ListActiveAsync()).ToLookup(u => u.PersonId);
            var changed = 0;
            foreach (var (pid, outcome) in tombstones)
            {
                if (!Guid.TryParse(pid, out var person)) continue;
                foreach (var user in byPerson[person])
                {
                    ct.ThrowIfCancellationRequested();
                    if (await HeldByLocalProcessAsync(person)) continue;
                    var demotion = outcome == "demoted" ? await StoredDemotionAsync(person) : null;
                    // Demoted already (the accounts node after its own S5): nothing left to do. Without the record (a replica, which holds superadmins
                    // only) any account with that person's id goes, whatever its role.
                    if (demotion is not null && user.Role != UserRoles.Superadmin && !user.IsBeingRemoved) continue;

                    var done = await UserService.EnforceTombstoneAsync(user, demotion, ct);
                    changed++;
                    if (done)
                        logger?.LogWarning("The account #{User} of a removed person was {Outcome} on this node, as the tombstone says.", user.Id,
                            demotion is null ? "deleted" : "demoted");
                    else
                        logger?.LogWarning("The account #{User} of a removed person is locked out but could not be deleted on this node: its door is the "
                                          + "last door of this node. Nobody can sign in, reset or join with it; re-join or set up this node again.", user.Id);
                }
            }
            return changed;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// A replicated removal is open (or closed): while it is open the person's account here gets no sign-in and no rights (plan E2); once it is closed
    /// the tombstone of that person, where this node holds one (the accounts node whose process was taken over elsewhere), is applied here.
    /// </summary>
    public async Task OnReplicatedStateAsync(Guid processId, Guid personId, bool open, CancellationToken ct = default)
    {
        if (!open)
        {
            await EnforceAsync(personId, ct);
            return;
        }
        using var conn = db.CreateConnection();
        var ids = (await conn.QueryAsync<int>(
            "SELECT id FROM tbl_user WHERE person_id = @p COLLATE NOCASE AND is_active = 1 AND account_state = @active",
            new { p = personId.ToString("D"), active = UserAccountStates.Active })).ToList();
        foreach (var id in ids)
        {
            await conn.ExecuteAsync(
                "UPDATE tbl_user SET account_state = @offboarding, security_stamp = lower(hex(randomblob(16))) WHERE id = @id",
                new { id, offboarding = UserAccountStates.Offboarding });
            CallerStateCache.Invalidate(id);
            logger?.LogWarning("Account #{User} is locked out: a removal of this person is open (process {Process}).", id, processId);
        }
    }

    private async Task<bool> HeldByLocalProcessAsync(Guid personId)
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_offboarding WHERE person_id = @p COLLATE NOCASE AND closed_at IS NULL AND origin = 'local'",
            new { p = personId.ToString("D") }) > 0;
    }

    /// <summary>
    /// The role and folders a removal started on this node stored for a demotion, or null where this node has no such record. Only the node that started
    /// the removal ever has them (the role and folders never travel), also once another node took its process over.
    /// </summary>
    private async Task<(string Role, IReadOnlyList<DemotionFolderRule>? Rules, bool AllowOpenRole)?> StoredDemotionAsync(Guid personId)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<(string? Role, string? Rules, long AllowOpen)?>(
            @"SELECT target_role, target_rules_json, allow_open_role FROM tbl_offboarding
               WHERE person_id = @p COLLATE NOCASE AND target = 'demote' AND target_role IS NOT NULL
               ORDER BY started_at DESC LIMIT 1",
            new { p = personId.ToString("D") });
        if (row is not { Role: { } role } r) return null;
        IReadOnlyList<DemotionFolderRule>? rules = null;
        if (!string.IsNullOrEmpty(r.Rules))
            rules = JsonSerializer.Deserialize<List<DemotionFolderRule>>(r.Rules);
        return (role, rules, r.AllowOpen != 0);
    }

    private static async Task<bool> TableExistsAsync(System.Data.IDbConnection conn, string table) =>
        await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table", new { table }) > 0;
}
