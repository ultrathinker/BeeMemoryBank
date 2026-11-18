using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Boot;
using Dapper;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>The three kinds of door that belong to nobody, which a removal revokes by default (plan WS-B2 / E4 step S3).</summary>
public static class UnknownOwnerDoorKinds
{
    /// <summary>A recovery key issued before 2.12, whose slot names no owner.</summary>
    public const string Recovery = "recovery";
    /// <summary>The un-migrated legacy single <c>password</c> slot of a node from before the password unification.</summary>
    public const string LegacyPassword = "legacy-password";
    /// <summary>An agent whose owner is missing (<c>owner_user_id &lt;= 0</c>).</summary>
    public const string Agent = "agent";
}

/// <summary>One door of the unknown-owner set: its kind, its row id (slot id or agent id) and a label/name for display. Never key material.</summary>
public sealed record UnknownOwnerDoor(string Kind, int Id, string? Label);

/// <summary>
/// The unknown-owner set (release 2.12, plan WS-B2): the ways into a vault that belong to NOBODY — recovery keys issued before 2.12 (no owner on
/// record), the un-migrated legacy <c>password</c> slot, and agents whose owner is missing. A removal revokes these by default, because nobody can
/// say whose they are and a leaver might hold them. The synthetic "admin" account a migrated vault gets is an ordinary PERSON (its slot and agents
/// have an owner), so it is never in this set.
/// </summary>
public static class UnknownOwnerDoors
{
    /// <summary>Lists every door of the unknown-owner set. Reads no key material.</summary>
    public static async Task<List<UnknownOwnerDoor>> ListAsync(IKeySlotRepository slots, IAgentRepository agents)
    {
        var list = new List<UnknownOwnerDoor>();
        foreach (var s in await slots.GetAllAsync())
        {
            if (s.SlotType == KeySlotTypes.Recovery && s.OwnerUserId is null)
                list.Add(new UnknownOwnerDoor(UnknownOwnerDoorKinds.Recovery, s.SlotId, s.Label));
            else if (s.SlotType == KeySlotTypes.Password)
                list.Add(new UnknownOwnerDoor(UnknownOwnerDoorKinds.LegacyPassword, s.SlotId, null));
        }
        foreach (var a in await agents.ListActiveAsync())
            if (a.OwnerUserId <= 0)
                list.Add(new UnknownOwnerDoor(UnknownOwnerDoorKinds.Agent, a.Id, a.Name));
        return list.OrderBy(d => d.Kind, StringComparer.Ordinal).ThenBy(d => d.Id).ToList();
    }

    /// <summary>
    /// Revokes every door of the unknown-owner set in ONE transaction, in the boot-store order (copies retired before the commit). Refused as a
    /// whole if it would leave the node with no door a person can open. Each revocation is audited; no entry holds key material.
    /// </summary>
    /// <returns>The number of doors revoked.</returns>
    public static async Task<int> RevokeAllAsync(IDbConnectionFactory db, IKeySlotRepository slots, IAgentRepository agents, IUserRepository users,
        IUnlockMaterialMirror? mirror, IAuditLogRepository? audit = null, string? actor = null, CancellationToken ct = default)
    {
        var items = await ListAsync(slots, agents);
        if (items.Count == 0) return 0;

        var removingSlotIds = items.Where(i => i.Kind != UnknownOwnerDoorKinds.Agent).Select(i => i.Id).ToList();
        var agentIds = items.Where(i => i.Kind == UnknownOwnerDoorKinds.Agent).Select(i => i.Id).ToList();

        // The guard is check-then-act: serialise it with the deletes against every other revoke path (SlotDoors.RevokeGate).
        await SlotDoors.RevokeGate.WaitAsync();
        try
        {
            // All-or-nothing: after these doors are gone, a person must still have a way in.
            await SlotDoors.EnsureAPersonDoorRemainsAsync(slots, users, removingSlotIds);

            using (var conn = db.CreateConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (mirror != null)
                    {
                        foreach (var slotId in removingSlotIds) await mirror.RetireDoorAsync(BootDoorKinds.Slot, slotId, ct);
                        foreach (var agentId in agentIds) await mirror.RetireDoorAsync(BootDoorKinds.Agent, agentId, ct);
                    }
                    foreach (var slotId in removingSlotIds) await slots.DeleteAsync(slotId, tx);
                    foreach (var agentId in agentIds)
                        await conn.ExecuteAsync(
                            @"UPDATE tbl_agent
                                 SET status = 'D', encrypted_dek = NULL, dek_iv = NULL, salt = NULL, kdf_version = 0, door_public_key = NULL, dek_fp = NULL
                               WHERE id = @agentId", new { agentId }, tx);

                    // Audit INSIDE the transaction: the entries commit with the revoke, and a failed audit rolls the whole
                    // revoke back instead of reporting an already-done revoke as failed. The repository opens its own connection, which cannot
                    // nest in this write transaction, so the rows are written here on the transaction's connection.
                    if (audit != null)
                    {
                        var actorType = actor is null ? "system" : "web";
                        var by = actor is null ? "a local caller" : "user " + actor;
                        foreach (var item in items)
                        {
                            switch (item.Kind)
                            {
                                case UnknownOwnerDoorKinds.Recovery:
                                    await SlotDoors.AuditAsync(conn, tx, "key_slot", item.Id.ToString(), "recovery_key_revoked", actorType,
                                        $"Recovery key (slot #{item.Id}, owner unknown (issued before 2.12)) revoked by {by}");
                                    break;
                                case UnknownOwnerDoorKinds.LegacyPassword:
                                    await SlotDoors.AuditAsync(conn, tx, "key_slot", item.Id.ToString(), "legacy_slot_revoked", actorType,
                                        $"Legacy password slot #{item.Id} (owner unknown) revoked by {by}");
                                    break;
                                case UnknownOwnerDoorKinds.Agent:
                                    await SlotDoors.AuditAsync(conn, tx, "agent", item.Id.ToString(), "agent_deleted", actorType,
                                        $"Agent '{item.Label}' (#{item.Id}, owner unknown) revoked by {by}");
                                    break;
                            }
                        }
                    }
                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { /* SQLite may have rolled back already; do not mask the real failure */ }
                    await SlotDoors.RewriteAsync(mirror);   // the doors that were retired are copied back
                    throw;
                }
            }
            await SlotDoors.RewriteAsync(mirror);
            return items.Count;
        }
        finally { SlotDoors.RevokeGate.Release(); }
    }
}
