using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>The outcome of <see cref="VaultVerifier.RunAsync"/>: <c>ok</c>, or the check that failed and one sentence for the operator.</summary>
public sealed record VaultVerifyResult(bool Ok, string Check, string Message)
{
    public static VaultVerifyResult Passed { get; } = new(true, "ok", "ok");

    public static VaultVerifyResult Failed(string check, string message) => new(false, check, message);
}

/// <summary>
/// <c>bmb vault verify</c> (docs/design/step-b-spec.md 8.7.2 step 4): the step an operator takes after putting a backup set back, before the node meets
/// its peers again. With the master password it opens the database for the checks only (no session, no gate, nothing written) and runs, in this order:
/// the integrity of the boot store, the password against the doors, the wrapper of the master key, the integrity of the vault and of the chat database
/// (the page HMACs make a cut-off or damaged page an error, not silent garbage), and that every key slot and every auto-unlock agent has a door.
/// The first check that fails is named. Every key it holds is cleared before it returns.
/// </summary>
public static class VaultVerifier
{
    public const string BrokenCheck = "broken";
    public const string NotSealedCheck = "not_sealed";
    public const string BootIntegrityCheck = "boot_integrity";
    public const string PasswordCheck = "password";
    public const string WrapperCheck = "wrapper";
    public const string KeyMismatchCheck = "key_mismatch";
    public const string VaultIntegrityCheck = "vault_integrity";
    public const string ChatIntegrityCheck = "chat_integrity";
    public const string DoorCheck = "door";

    public static async Task<VaultVerifyResult> RunAsync(string dataPath, string password, CancellationToken ct = default)
    {
        var diagnosis = VaultStoreFiles.Diagnose(dataPath);
        if (BrokenStart.From(diagnosis) is { } broken) return VaultVerifyResult.Failed(BrokenCheck, broken.Message);
        if (diagnosis.Mode != VaultStoreMode.Sealed) return VaultVerifyResult.Failed(NotSealedCheck, "This node's database is not encrypted: there is nothing to verify.");

        var vaultPath = Path.Combine(dataPath, VaultStoreFiles.Database);
        var chatPath = Path.Combine(dataPath, VaultStoreFiles.Chat);
        var boot = new BootStore(Path.Combine(dataPath, VaultStoreFiles.Boot));

        // 1. The boot store is a database of its own.
        try
        {
            using var connection = boot.Connections.OpenReadOnly();
            var rows = (await connection.QueryAsync<string>("PRAGMA integrity_check")).ToList();
            if (rows.Count != 1 || rows[0] != "ok")
                return VaultVerifyResult.Failed(BootIntegrityCheck, "The boot store failed its integrity check: " + rows.FirstOrDefault());
        }
        catch (SqliteException ex)
        {
            return VaultVerifyResult.Failed(BootIntegrityCheck, $"The boot store could not be read (SQLite result {ex.SqliteErrorCode}).");
        }

        byte[]? masterDek = null;
        byte[]? databaseKey = null;
        byte[]? vaultKey = null;
        byte[]? chatKey = null;
        try
        {
            // 2. The password opens a door.
            var doors = await new BootDoorStore(boot.Connections).ListAsync(ct);
            masterDek = TryOpenDoors(doors, password);
            if (masterDek is null)
                return VaultVerifyResult.Failed(PasswordCheck, "The password opens none of the doors of this node (a wrong password, or the boot store lacks the door of the password).");

            // 3. The wrapper of the master key unwraps the database key.
            var keyService = new DatabaseKeyService(boot, new BootDbKeyStore(boot.Connections));
            databaseKey = await keyService.TryUnwrapAsync(masterDek, ct);
            if (databaseKey is null)
                return VaultVerifyResult.Failed(WrapperCheck, "The boot store has no wrapper of the database key that this master key opens (a missing wrapper, or a boot store of another database).");
            vaultKey = keyService.DerivePageKey(databaseKey, DbPurposes.Vault);
            chatKey = keyService.DerivePageKey(databaseKey, DbPurposes.Chat);

            // 4. The vault and the chat database, page by page.
            var vaultProblem = IntegrityOf(vaultPath, vaultKey, VaultIntegrityCheck, "vault");
            if (vaultProblem is not null) return vaultProblem;
            if (File.Exists(chatPath) && IntegrityOf(chatPath, chatKey, ChatIntegrityCheck, "chat") is { } chatProblem) return chatProblem;

            // 5. Every key slot and every auto-unlock agent has a door, byte for byte.
            return await CheckDoorsAsync(vaultPath, vaultKey, doors);
        }
        finally
        {
            foreach (var secret in new[] { masterDek, databaseKey, vaultKey, chatKey })
                if (secret is not null) CryptographicOperations.ZeroMemory(secret);
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>The master DEK a door yields for <paramref name="password"/>: the password doors only (a superadmin's slot, a recovery slot, the legacy slot).</summary>
    private static byte[]? TryOpenDoors(IReadOnlyList<BootDoor> doors, string password)
    {
        foreach (var door in doors.Where(d => d.Kind == BootDoorKinds.Slot && !d.Pending && d.SlotType is "user" or "recovery" or "password"))
        {
            if (door.Salt is null || door.ArgonMemory is null || door.ArgonIterations is null || door.ArgonParallelism is null) continue;
            byte[]? kek = null;
            try
            {
                KeyDerivation.ValidateUntrustedParameters(door.ArgonMemory.Value, door.ArgonIterations.Value, door.ArgonParallelism.Value);
                kek = KeyDerivation.DeriveKek(password, door.Salt, door.ArgonMemory.Value, door.ArgonIterations.Value, door.ArgonParallelism.Value);
                return MasterKeyManager.UnwrapMasterDek(door.EncryptedMasterDek, door.Iv, kek);
            }
            catch (CryptographicException)
            {
                // Not this door's password.
            }
            finally
            {
                if (kek is not null) CryptographicOperations.ZeroMemory(kek);
            }
        }
        return null;
    }

    private static VaultVerifyResult? IntegrityOf(string path, byte[] pageKey, string check, string label)
    {
        try
        {
            using var connection = SealedSqlite.OpenKeyed(path, pageKey, readOnly: true, busyTimeoutSeconds: 5);
            var rows = connection.Query<string>("PRAGMA integrity_check").ToList();
            return rows.Count == 1 && rows[0] == "ok"
                ? null
                : VaultVerifyResult.Failed(check, $"The {label} database failed its integrity check: {rows.FirstOrDefault()}");
        }
        catch (VaultKeyMismatchException)
        {
            return VaultVerifyResult.Failed(KeyMismatchCheck, $"The key in the boot store does not open the {label} database (a boot store of another database).");
        }
        catch (SqliteException ex)
        {
            return VaultVerifyResult.Failed(check, $"The {label} database could not be read through to the end (SQLite result {ex.SqliteErrorCode}): a page is damaged or cut off.");
        }
    }

    private static async Task<VaultVerifyResult> CheckDoorsAsync(string vaultPath, byte[] vaultKey, IReadOnlyList<BootDoor> doors)
    {
        var have = doors.Where(d => !d.Pending).Select(d => d.ContentSha256).ToHashSet(StringComparer.Ordinal);
        using var connection = SealedSqlite.OpenKeyed(vaultPath, vaultKey, readOnly: true, busyTimeoutSeconds: 5);
        foreach (var s in await connection.QueryAsync<(long SlotId, string SlotType, byte[] Wrapped, byte[] Iv, byte[]? Salt, long? Mem, long? It, long? Par)>(
                     "SELECT slot_id, slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot ORDER BY slot_id"))
        {
            var hash = BootDoorHash.OfSlot(s.SlotId, s.SlotType, s.Wrapped, s.Iv, s.Salt, (int?)s.Mem, (int?)s.It, (int?)s.Par);
            if (!have.Contains(hash))
                return VaultVerifyResult.Failed(DoorCheck, $"The boot store has no door for the key slot #{s.SlotId} ({s.SlotType}): that way of opening this node does not work while it is closed.");
        }
        foreach (var a in await connection.QueryAsync<(long Id, string KeyHash, byte[]? Salt, long? Kdf, byte[] Dek, byte[] Iv)>(
                     @"SELECT id, key_hash, salt, kdf_version, encrypted_dek, dek_iv FROM tbl_agent
                       WHERE encrypted_dek IS NOT NULL AND dek_iv IS NOT NULL AND status = 'A' ORDER BY id"))
        {
            var hash = BootDoorHash.OfAgent(a.Id, a.KeyHash, (int?)a.Kdf, a.Salt, a.Dek, a.Iv);
            if (!have.Contains(hash))
                return VaultVerifyResult.Failed(DoorCheck, $"The boot store has no door for the auto-unlock agent #{a.Id}: its key cannot open this node while it is closed.");
        }
        return VaultVerifyResult.Passed;
    }
}
