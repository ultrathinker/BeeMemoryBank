using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The door of an auto-unlock agent (a superadmin's agent that carries the master DEK): how it is sealed when the agent is made, its copy in the
/// boot store, and the upgrade of ADR 0012 at its first authenticated request.
/// </summary>
public static class AgentDoors
{
    /// <summary>Seals <paramref name="dek"/> into <paramref name="agent"/> as a v2 door for <paramref name="apiKey"/> (a new salt, kdf_version 2).</summary>
    public static void SealInto(Agent agent, string apiKey, byte[] dek)
    {
        var (door, salt) = Doors.SealAgent(apiKey, dek);
        agent.EncryptedDek = door.Box;
        agent.DekIV = door.Iv;
        agent.Salt = salt;
        agent.KdfVersion = Agent.SealedDoorKdfVersion;
        agent.DoorPublicKey = door.PublicKey;
        agent.DekFingerprint = door.DekFingerprint;
    }

    /// <summary>The boot-store door an auto-unlock agent row becomes (its own fingerprint names the DEK; null = unknown).</summary>
    public static PendingDoor PendingFor(Agent agent) =>
        new(BootDoorKinds.Agent, agent.Id, null, agent.EncryptedDek!, agent.DekIV!, agent.Salt, null, null, null, agent.KeyHash, agent.KdfVersion,
            agent.DekFingerprint, BootDoorHash.AgentDoorVersion(agent.KdfVersion), agent.DoorPublicKey);

    /// <summary>
    /// Opens an agent's door copy of the boot store with the API key, whatever its format. Throws <see cref="CryptographicException"/> for
    /// another key. The caller clears the DEK.
    /// </summary>
    public static byte[] OpenBootDoor(BootDoor door, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(door);
        if (door.KdfVersion == Agent.SealedDoorKdfVersion)
        {
            if (door.Salt is null) throw new CryptographicException("A sealed agent door without a salt.");
            var kek = Doors.AgentKek(apiKey, door.Salt);
            try
            {
                return Doors.OpenBootDoor(door, kek);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        return door.KdfVersion == 1 && door.Salt != null
            ? AgentKeyHelper.DecryptDekV1(apiKey, door.EncryptedMasterDek, door.Iv, door.Salt)
            : AgentKeyHelper.DecryptDek(apiKey, door.EncryptedMasterDek, door.Iv);
    }

    /// <summary>
    /// ADR 0012 at an auto-unlock agent's first authenticated request: a v0/v1 door that yields <paramref name="currentDek"/> is sealed again as a
    /// v2 door for the same key (a fresh salt), in the door-replacement order (pending boot door, the row while it still holds the old door, the
    /// rewrite). A v2 door with an unknown DEK gets its fingerprint. True when the row changed. Throws only what the writes throw.
    /// </summary>
    public static async Task<bool> UpgradeAsync(IDbConnectionFactory db, IUnlockMaterialMirror? mirror, Agent agent, string apiKey, byte[] currentDek,
        ILogger? logger = null)
    {
        if (!agent.CanAutoUnlock) return false;
        if (agent.KdfVersion == Agent.SealedDoorKdfVersion && agent.DekFingerprint is not null) return false;

        byte[] yielded;
        try
        {
            yielded = Doors.OpenAgent(agent, apiKey);
        }
        catch (CryptographicException)
        {
            return false;
        }
        var current = CryptographicOperations.FixedTimeEquals(yielded, currentDek);
        CryptographicOperations.ZeroMemory(yielded);
        if (!current) return false;

        if (agent.KdfVersion == Agent.SealedDoorKdfVersion)
        {
            bool named;
            using (var connection = db.CreateConnection())
            using (var tx = connection.BeginTransaction())
            {
                named = await connection.ExecuteAsync(
                    "UPDATE tbl_agent SET dek_fp = @fp WHERE id = @id AND dek_fp IS NULL AND encrypted_dek = @old",
                    new { fp = DekFingerprint.Of(currentDek), id = agent.Id, old = agent.EncryptedDek }, tx) == 1;
                // trg_umv_agent_update (baseline) does not watch dek_fp, and the baseline's triggers are compared with every database a restore
                // judges, so the count of unlock material is raised here: a crash before the rewrite below leaves the watcher a change to mirror.
                if (named)
                    await connection.ExecuteAsync(
                        @"UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                          WHERE key = 'unlock_material_version'", transaction: tx);
                tx.Commit();
            }
            // The boot copy names the DEK too (rule 3), so the wrappers no door needs can be pruned.
            if (named) await SlotDoors.RewriteAsync(mirror, logger);
            return named;
        }

        var upgraded = new Agent
        {
            Id = agent.Id,
            KeyHash = agent.KeyHash,
        };
        SealInto(upgraded, apiKey, currentDek);
        if (mirror is not null) await mirror.AddPendingDoorAsync(PendingFor(upgraded));
        int changed;
        try
        {
            using var connection = db.CreateConnection();
            changed = await connection.ExecuteAsync(
                @"UPDATE tbl_agent SET encrypted_dek = @EncryptedDek, dek_iv = @DekIV, salt = @Salt, kdf_version = @KdfVersion,
                         door_public_key = @DoorPublicKey, dek_fp = @DekFingerprint
                  WHERE id = @Id AND status = 'A' AND encrypted_dek = @old",
                new
                {
                    upgraded.EncryptedDek, upgraded.DekIV, upgraded.Salt, upgraded.KdfVersion, upgraded.DoorPublicKey, upgraded.DekFingerprint,
                    upgraded.Id, old = agent.EncryptedDek,
                });
        }
        finally
        {
            await SlotDoors.RewriteAsync(mirror, logger);
        }
        if (changed == 1)
            logger?.LogInformation("Agent {AgentId}'s door was sealed again in the door format of 2.12 at its first use.", agent.Id);
        return changed == 1;
    }
}
