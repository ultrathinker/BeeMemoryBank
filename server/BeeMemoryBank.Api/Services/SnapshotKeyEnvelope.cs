using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Crypto;
using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The key envelope of the pre-encryption snapshot (docs/design/step-b-spec.md 10.7): <c>key-envelope.json</c>, an extra entry of the snapshot's
/// tar.gz that carries copies of the key slots a password or the recovery key opens, the Argon2id-wrapped bytes a stolen disk already shows as the
/// doors of the sealed node. Why it exists: the snapshot's database is a BMBDB2 container under the master DEK of the day, a DEK rotation or a
/// node reset leaves no way to that DEK, and the envelope carries it wrapped under the password and the recovery key of the snapshot's day. It
/// survives any later password change, rotation, restart or reset, and dies only with the snapshot.
/// <para>Contents: <c>v</c>, <c>created_at</c>, the <c>sentinel</c> of the vault, and for each slot only its type, wrapped DEK, IV, salt and
/// Argon2 parameters. <c>user</c> slots of active superadmins, <c>recovery</c> and the legacy <c>password</c> slot - never
/// <c>os_auto_unlock</c>, <c>server_auto_unlock</c> or an agent's wrapped DEK, and no user id, user name or slot id.</para>
/// </summary>
public static class SnapshotKeyEnvelope
{
    public const string EntryName = "key-envelope.json";
    private const int Version = 1;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = true };

    private sealed record Slot(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("wrapped")] string Wrapped,
        [property: JsonPropertyName("iv")] string Iv,
        [property: JsonPropertyName("salt")] string Salt,
        [property: JsonPropertyName("argon_memory")] int ArgonMemory,
        [property: JsonPropertyName("argon_iterations")] int ArgonIterations,
        [property: JsonPropertyName("argon_parallelism")] int ArgonParallelism);

    private sealed record Document(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("created_at")] string CreatedAt,
        [property: JsonPropertyName("sentinel")] string Sentinel,
        [property: JsonPropertyName("slots")] List<Slot> Slots);

    /// <summary>
    /// The envelope of the vault <paramref name="connection"/> is open on (unlocked or not: it reads wrapped bytes only). Null when the vault has no
    /// sentinel or no slot a password opens: there is nothing to carry.
    /// </summary>
    public static byte[]? Build(IDbConnection connection, DateTime? createdAtUtc = null)
    {
        var sentinel = connection.ExecuteScalar<byte[]?>("SELECT sentinel_value FROM tbl_node_identity LIMIT 1");
        if (sentinel is null) return null;
        var slots = connection.Query<(string Type, byte[] Wrapped, byte[] Iv, byte[]? Salt, long? Mem, long? It, long? Par)>(
            @"SELECT s.slot_type, s.encrypted_master_dek, s.iv, s.salt, s.argon_memory, s.argon_iterations, s.argon_parallelism
              FROM tbl_key_slot s
              WHERE (s.slot_type = 'user' AND EXISTS (SELECT 1 FROM tbl_user u WHERE u.key_slot_id = s.slot_id AND u.is_active = 1 AND u.role = 'superadmin'))
                 OR s.slot_type IN ('recovery', 'password')
              ORDER BY s.slot_type, s.slot_id")
            .Where(s => s.Salt is not null && s.Mem is not null && s.It is not null && s.Par is not null)
            .Select(s => new Slot(s.Type, Convert.ToBase64String(s.Wrapped), Convert.ToBase64String(s.Iv), Convert.ToBase64String(s.Salt!),
                (int)s.Mem!.Value, (int)s.It!.Value, (int)s.Par!.Value))
            .ToList();
        if (slots.Count == 0) return null;
        return JsonSerializer.SerializeToUtf8Bytes(
            new Document(Version, (createdAtUtc ?? DateTime.UtcNow).ToString("o"), Convert.ToBase64String(sentinel), slots), Json);
    }

    /// <summary>
    /// The master DEK of the snapshot's day: each slot is tried under the same Argon2id bounds a restore applies to the slots of a staged database,
    /// and the DEK that passes the vault's sentinel check is returned (the caller clears it, and never logs it). Null when no slot opens under
    /// <paramref name="password"/>, the envelope is not one this version reads, or it is damaged.
    /// </summary>
    public static byte[]? TryOpen(ReadOnlySpan<byte> envelope, string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(envelope, Json); }
        catch (JsonException) { return null; }
        if (document is not { V: Version, Slots: { } slots } || document.Sentinel is null) return null;

        byte[] sentinel;
        try { sentinel = Convert.FromBase64String(document.Sentinel); }
        catch (FormatException) { return null; }

        foreach (var slot in slots)
        {
            try { KeyDerivation.ValidateUntrustedParameters(slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism); }
            catch (CryptographicException) { continue; }

            byte[]? kek = null;
            byte[]? dek = null;
            try
            {
                kek = KeyDerivation.DeriveKek(password, Convert.FromBase64String(slot.Salt), slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism);
                dek = MasterKeyManager.UnwrapMasterDek(Convert.FromBase64String(slot.Wrapped), Convert.FromBase64String(slot.Iv), kek);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // Not this slot's password, or a damaged slot.
            }
            finally
            {
                if (kek is not null) CryptographicOperations.ZeroMemory(kek);
            }

            if (dek is null) continue;
            if (MasterKeyManager.VerifySentinel(sentinel, dek)) return dek;
            CryptographicOperations.ZeroMemory(dek);
        }
        return null;
    }
}
