using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using Dapper;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>
/// The key material of the copy (rekey-offline.md §2 step 3, §8.2; L). In one transaction:
/// <list type="bullet">
/// <item>D_c becomes the sentinel, and <c>dek_epoch</c> moves on;</item>
/// <item>the node's identity seed is sealed under D_c (a v=1 row is opened with the old key first; a legacy
///   plaintext v=0 row is sealed for the first time). Its public key and node id do not change;</item>
/// <item>the owner's slot is written again: D_c under the owner's password, with a fresh salt;</item>
/// <item>every other slot is removed (other users, recovery codes, the OS auto-unlock, update hand-offs), and the
///   users that pointed at one lose their link to it;</item>
/// <item>every agent's wrapped master key is cleared, so no old agent key can unlock;</item>
/// <item>the chain material of past rotations and the <c>retired-master-dek:*</c> rows are removed;</item>
/// <item>recovery boxes, their bookkeeping and the rotation links are removed. The owner's device box for D_c is
///   written at the first login after the re-key.</item>
/// </list>
/// The chat key (<c>tbl_node_data_key</c> row <c>chat</c>) is ChatRekeyStep's. Notes name what was cleared, for the
/// report page: <c>cleared-slot:&lt;slot&gt; &lt;user or kind&gt;</c> and <c>cleared-agent:&lt;id&gt; &lt;name&gt;</c>.
/// </summary>
public sealed class KeyMaterialStep : IRekeyStep, IRekeyOwnerCredentialConsumer
{
    private int? _ownerSlot;
    private string? _ownerPassword;

    public string Name => "KeyMaterial";

    private static readonly string[] RecoveryTables =
        ["tbl_recovery_box", "tbl_recovery_box_check", "tbl_recovery_box_pending_retire", "tbl_dek_retired_link"];

    public void SetOwnerCredential(int ownerSlotId, string ownerPassword) => (_ownerSlot, _ownerPassword) = (ownerSlotId, ownerPassword);

    public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
    {
        if (_ownerSlot is not { } ownerSlot || _ownerPassword is not { } password)
            throw new InvalidOperationException("The key-material step needs the owner's slot and password.");

        var db = ctx.Main;
        var dc = ctx.Keys.CampaignDek;
        var counts = new Dictionary<string, long>();
        var notes = new List<string>();

        // The Argon2id work comes first, outside the transaction.
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(password, salt,
            CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism);
        byte[] ownerWrapped, ownerIv;
        try { (ownerWrapped, ownerIv) = MasterKeyManager.WrapMasterDek(dc, kek); }
        finally { CryptographicOperations.ZeroMemory(kek); }

        using var tx = db.BeginTransaction();

        // The identity seed: sealed under D_c, whatever it was sealed under before.
        var id = await db.QuerySingleAsync<(string NodeId, byte[] Pk, byte[]? Iv, long V, byte[] Pub)>(
            @"SELECT node_id, ed25519_private_key, ed25519_private_key_iv, ed25519_private_key_v, ed25519_public_key
              FROM tbl_node_identity LIMIT 1", transaction: tx);
        var nodeId = Guid.Parse(id.NodeId);
        var seed = OpenSeed(id.Pk, id.Iv, (int)id.V, nodeId, ctx.Keys.OldCandidates);
        try
        {
            if (!NodeIdentityCrypto.PublicKeyOf(seed).AsSpan().SequenceEqual(id.Pub))
                throw new InvalidOperationException("The node's identity seed does not match its public key.");
            var (pk, pkIv) = NodeIdentityCrypto.EncryptPrivateKey(seed, dc, nodeId);
            await db.ExecuteAsync(
                @"UPDATE tbl_node_identity
                  SET ed25519_private_key = @pk, ed25519_private_key_iv = @pkIv, ed25519_private_key_v = 1,
                      sentinel_value = @sentinel, dek_epoch = dek_epoch + 1",
                new { pk, pkIv, sentinel = MasterKeyManager.ComputeSentinel(dc) }, tx);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        // The owner's slot, then every other one.
        if (await db.ExecuteAsync(
                @"UPDATE tbl_key_slot SET encrypted_master_dek = @ownerWrapped, iv = @ownerIv, salt = @salt,
                         argon_memory = @mem, argon_iterations = @it, argon_parallelism = @par
                  WHERE slot_id = @ownerSlot",
                new
                {
                    ownerWrapped, ownerIv, salt, ownerSlot, mem = CryptoConstants.DefaultArgonMemory,
                    it = CryptoConstants.DefaultArgonIterations, par = CryptoConstants.DefaultArgonParallelism
                }, tx) != 1)
            throw new InvalidOperationException($"The owner's key slot {ownerSlot} is not in the copy.");

        foreach (var s in await db.QueryAsync<(long SlotId, string Type, string? User)>(
                     @"SELECT s.slot_id, s.slot_type, (SELECT u.username FROM tbl_user u WHERE u.key_slot_id = s.slot_id LIMIT 1)
                       FROM tbl_key_slot s WHERE s.slot_id <> @ownerSlot ORDER BY s.slot_id", new { ownerSlot }, tx))
            notes.Add($"cleared-slot:{s.SlotId} {s.User ?? s.Type}");
        // A user whose slot is gone must have a new password set before it can open the vault again; its old sign-in
        // cookies go with the slot.
        counts["tbl_user.key_slot_id"] = await db.ExecuteAsync(
            @"UPDATE tbl_user SET key_slot_id = NULL, security_stamp = lower(hex(randomblob(16)))
              WHERE key_slot_id IS NOT NULL AND key_slot_id <> @ownerSlot", new { ownerSlot }, tx);
        counts["tbl_key_slot"] = await db.ExecuteAsync("DELETE FROM tbl_key_slot WHERE slot_id <> @ownerSlot", new { ownerSlot }, tx);

        foreach (var a in await db.QueryAsync<(long Id, string Name)>(
                     "SELECT id, name FROM tbl_agent WHERE encrypted_dek IS NOT NULL OR dek_iv IS NOT NULL ORDER BY id", transaction: tx))
            notes.Add($"cleared-agent:{a.Id} {a.Name}");
        counts["tbl_agent"] = await db.ExecuteAsync(
            "UPDATE tbl_agent SET encrypted_dek = NULL, dek_iv = NULL WHERE encrypted_dek IS NOT NULL OR dek_iv IS NOT NULL",
            transaction: tx);

        counts["tbl_node_data_key"] = await db.ExecuteAsync(
            "DELETE FROM tbl_node_data_key WHERE key_name LIKE @prefix",
            new { prefix = IRetiredMasterDekStore.KeyNamePrefix + "%" }, tx);
        counts["tbl_dek_rotation_state"] = await db.ExecuteAsync(
            @"UPDATE tbl_dek_rotation_state SET chain_encrypted_new_dek = NULL, chain_iv = NULL
              WHERE chain_encrypted_new_dek IS NOT NULL OR chain_iv IS NOT NULL", transaction: tx);
        foreach (var table in RecoveryTables)
            counts[table] = await db.ExecuteAsync($"DELETE FROM {table}", transaction: tx);

        tx.Commit();
        ctx.Progress.Report(Name, 1, 1);
        return new RekeyStepResult(Name, counts, notes);
    }

    public async Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx)
    {
        var db = ctx.Main;
        var dc = ctx.Keys.CampaignDek;
        var problems = new List<RekeyProblem>();

        var id = await db.QuerySingleAsync<(string NodeId, byte[] Pk, byte[]? Iv, long V, byte[] Pub, byte[]? Sentinel)>(
            @"SELECT node_id, ed25519_private_key, ed25519_private_key_iv, ed25519_private_key_v, ed25519_public_key, sentinel_value
              FROM tbl_node_identity LIMIT 1");
        if (id.Sentinel == null || !MasterKeyManager.VerifySentinel(id.Sentinel, dc))
            problems.Add(new("tbl_node_identity", "sentinel_value", "the sentinel does not name D_c"));
        if (id.V != 1)
            problems.Add(new("tbl_node_identity", "ed25519_private_key", $"the identity seed is at v={id.V}, not sealed under D_c"));
        else
        {
            byte[]? seed = null;
            try
            {
                seed = NodeIdentityCrypto.GetDecryptedPrivateKey(id.Pk, id.Iv, 1, Guid.Parse(id.NodeId), dc);
                if (!NodeIdentityCrypto.PublicKeyOf(seed).AsSpan().SequenceEqual(id.Pub))
                    problems.Add(new("tbl_node_identity", "ed25519_private_key", "the seed under D_c does not match the public key"));
            }
            catch (CryptographicException)
            {
                problems.Add(new("tbl_node_identity", "ed25519_private_key", "the identity seed does not open under D_c"));
            }
            finally
            {
                if (seed != null) CryptographicOperations.ZeroMemory(seed);
            }
        }

        var slots = (await db.QueryAsync<(long SlotId, byte[] W, byte[] Iv, byte[]? Salt, long? Mem, long? It, long? Par)>(
            "SELECT slot_id, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot")).ToList();
        if (slots.Count != 1 || slots[0].SlotId != _ownerSlot)
            problems.Add(new("tbl_key_slot", "*", $"{slots.Count} slot(s) left; only the owner's slot {_ownerSlot} may remain"));
        foreach (var s in slots.Where(s => s.SlotId == _ownerSlot))
        {
            if (_ownerPassword == null || s.Salt == null || s.Mem == null || s.It == null || s.Par == null)
            {
                problems.Add(new("tbl_key_slot", s.SlotId.ToString(), "the owner's slot is incomplete"));
                continue;
            }
            var kek = KeyDerivation.DeriveKek(_ownerPassword, s.Salt, (int)s.Mem, (int)s.It, (int)s.Par);
            try
            {
                var opened = MasterKeyManager.UnwrapMasterDek(s.W, s.Iv, kek);
                if (!CryptographicOperations.FixedTimeEquals(opened, dc))
                    problems.Add(new("tbl_key_slot", s.SlotId.ToString(), "the owner's password opens the slot to another key"));
                CryptographicOperations.ZeroMemory(opened);
            }
            catch (CryptographicException)
            {
                problems.Add(new("tbl_key_slot", s.SlotId.ToString(), "the owner's password does not open the slot"));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }

        if (await db.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_user WHERE key_slot_id IS NOT NULL AND key_slot_id <> @o", new { o = _ownerSlot }) is var u and > 0)
            problems.Add(new("tbl_user", "*", $"{u} user(s) still point at a removed slot"));
        if (await db.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_agent WHERE encrypted_dek IS NOT NULL OR dek_iv IS NOT NULL") is var a and > 0)
            problems.Add(new("tbl_agent", "*", $"{a} agent(s) still carry a wrapped master key"));
        if (await db.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_node_data_key WHERE key_name LIKE @p", new { p = IRetiredMasterDekStore.KeyNamePrefix + "%" }) is var r and > 0)
            problems.Add(new("tbl_node_data_key", "retired-master-dek:*", $"{r} retired key row(s) left"));
        if (await db.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE chain_encrypted_new_dek IS NOT NULL OR chain_iv IS NOT NULL") is var c and > 0)
            problems.Add(new("tbl_dek_rotation_state", "*", $"{c} rotation(s) keep chain material"));
        foreach (var table in RecoveryTables)
            if (await db.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}") is var n and > 0)
                problems.Add(new(table, "*", $"{n} row(s) left"));
        return problems;
    }

    /// <summary>The seed in the clear: a v=1 row under whichever old key seals it, a legacy v=0 row as it is.</summary>
    private static byte[] OpenSeed(byte[] stored, byte[]? iv, int version, Guid nodeId, IReadOnlyList<byte[]> oldKeys)
    {
        if (version == 0) return NodeIdentityCrypto.GetDecryptedPrivateKey(stored, iv, 0, nodeId, []);
        if (version != 1)
            throw new InvalidOperationException(
                $"The node's identity key is at v={version}: it is kept outside the database (a blind node), and this vault cannot be re-keyed.");
        foreach (var key in oldKeys)
        {
            try { return NodeIdentityCrypto.GetDecryptedPrivateKey(stored, iv, 1, nodeId, key); }
            catch (CryptographicException) { }
        }
        throw new InvalidOperationException("The node's identity seed opens under none of the vault's keys.");
    }
}
