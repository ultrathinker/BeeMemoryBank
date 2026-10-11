using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>
/// The content hash of a door (docs/design/step-b-spec.md 4.2): lowercase hex SHA-256 of <c>LP(kind) LP(source_id as decimal)
/// LP(slot_type or "") LP(key_hash or "") LP(kdf_version or "") LP(argon_memory or "") LP(argon_iterations or "")
/// LP(argon_parallelism or "") LB(salt or empty) LB(encrypted_master_dek) LB(iv)</c>, where <c>LP(s)</c> is the int32 little-endian
/// byte length of UTF-8(s) followed by the bytes and <c>LB(b)</c> the same for raw bytes. The same formula is the <c>slot_sha256</c>
/// of the server auto-unlock enable record (spec 11.2).
/// </summary>
public static class BootDoorHash
{
    public static string Of(string kind, long sourceId, string? slotType, string? keyHash, int? kdfVersion, int? argonMemory,
        int? argonIterations, int? argonParallelism, byte[]? salt, byte[] encryptedMasterDek, byte[] iv)
    {
        using var ms = new MemoryStream();
        WriteText(ms, kind);
        WriteText(ms, sourceId.ToString(CultureInfo.InvariantCulture));
        WriteText(ms, slotType ?? "");
        WriteText(ms, keyHash ?? "");
        WriteText(ms, kdfVersion?.ToString(CultureInfo.InvariantCulture) ?? "");
        WriteText(ms, argonMemory?.ToString(CultureInfo.InvariantCulture) ?? "");
        WriteText(ms, argonIterations?.ToString(CultureInfo.InvariantCulture) ?? "");
        WriteText(ms, argonParallelism?.ToString(CultureInfo.InvariantCulture) ?? "");
        WriteBytes(ms, salt ?? []);
        WriteBytes(ms, encryptedMasterDek);
        WriteBytes(ms, iv);
        return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
    }

    /// <summary>The hash of the key slot row <paramref name="slotId"/> (a door of kind <c>slot</c>).</summary>
    public static string OfSlot(long slotId, string slotType, byte[] encryptedMasterDek, byte[] iv, byte[]? salt, int? argonMemory,
        int? argonIterations, int? argonParallelism) =>
        Of(BootDoorKinds.Slot, slotId, slotType, null, null, argonMemory, argonIterations, argonParallelism, salt, encryptedMasterDek, iv);

    /// <summary>The hash of an auto-unlock agent row (a door of kind <c>agent</c>).</summary>
    public static string OfAgent(long agentId, string keyHash, int? kdfVersion, byte[]? salt, byte[] encryptedMasterDek, byte[] iv) =>
        Of(BootDoorKinds.Agent, agentId, null, keyHash, kdfVersion, null, null, null, salt, encryptedMasterDek, iv);

    private static void WriteText(MemoryStream ms, string s) => WriteBytes(ms, Encoding.UTF8.GetBytes(s));

    private static void WriteBytes(MemoryStream ms, byte[] b)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, b.Length);
        ms.Write(length);
        ms.Write(b);
    }
}

/// <summary>The doors of the boot store (track 2): read by the unlock (track 3), written only through the mirror (spec 4.3).</summary>
public sealed class BootDoorStore(BootStoreConnectionFactory factory) : IBootDoorStore
{
    internal const string Columns =
        "door_id AS DoorId, kind AS Kind, source_id AS SourceId, slot_type AS SlotType, encrypted_master_dek AS EncryptedMasterDek, iv AS Iv, " +
        "salt AS Salt, argon_memory AS ArgonMemory, argon_iterations AS ArgonIterations, argon_parallelism AS ArgonParallelism, " +
        "key_hash AS KeyHash, kdf_version AS KdfVersion, content_sha256 AS ContentSha256, dek_fp AS DekFingerprint, pending AS Pending";

    public async Task<IReadOnlyList<BootDoor>> ListAsync(CancellationToken ct = default)
    {
        using var connection = factory.Open();
        return await ListAsync(connection, null);
    }

    internal static async Task<IReadOnlyList<BootDoor>> ListAsync(SqliteConnection connection, SqliteTransaction? transaction) =>
        (await connection.QueryAsync<DoorRecord>($"SELECT {Columns} FROM tbl_boot_door ORDER BY door_id", transaction: transaction))
            .Select(r => r.ToDoor()).ToList();

    /// <summary>The row as the engine returns it (integers are 64-bit); a positional record would need exact column types.</summary>
    private sealed class DoorRecord
    {
        public long DoorId { get; set; }
        public string Kind { get; set; } = "";
        public long SourceId { get; set; }
        public string? SlotType { get; set; }
        public byte[] EncryptedMasterDek { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public byte[]? Salt { get; set; }
        public long? ArgonMemory { get; set; }
        public long? ArgonIterations { get; set; }
        public long? ArgonParallelism { get; set; }
        public string? KeyHash { get; set; }
        public long? KdfVersion { get; set; }
        public string ContentSha256 { get; set; } = "";
        public string? DekFingerprint { get; set; }
        public long Pending { get; set; }

        public BootDoor ToDoor() => new(DoorId, Kind, SourceId, SlotType, EncryptedMasterDek, Iv, Salt, (int?)ArgonMemory, (int?)ArgonIterations,
            (int?)ArgonParallelism, KeyHash, (int?)KdfVersion, ContentSha256, DekFingerprint, Pending != 0);
    }

    public async Task<IReadOnlyList<BootDoor>> FindAgentAsync(string keyHash, CancellationToken ct = default)
    {
        using var connection = factory.Open();
        return (await connection.QueryAsync<DoorRecord>(
            $"SELECT {Columns} FROM tbl_boot_door WHERE kind = 'agent' AND key_hash = @keyHash ORDER BY door_id", new { keyHash }))
            .Select(r => r.ToDoor()).ToList();
    }

    public async Task SetDekFingerprintAsync(long doorId, string dekFingerprint, CancellationToken ct = default)
    {
        using var connection = factory.Open();
        await connection.ExecuteAsync("UPDATE tbl_boot_door SET dek_fp = @dekFingerprint WHERE door_id = @doorId", new { doorId, dekFingerprint });
    }

    /// <summary>
    /// Adds a door as <c>pending</c> (spec 4.3 rule 1) unless a row with the same kind, source and content hash exists already (then that
    /// row stays as it is, current or pending). Returns the door id.
    /// </summary>
    public async Task<long> AddPendingAsync(BootDoor door, CancellationToken ct = default)
    {
        using var connection = factory.Open();
        await connection.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_boot_door
                (kind, source_id, slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, key_hash,
                 kdf_version, content_sha256, dek_fp, pending, written_at)
              VALUES (@Kind, @SourceId, @SlotType, @EncryptedMasterDek, @Iv, @Salt, @ArgonMemory, @ArgonIterations, @ArgonParallelism, @KeyHash,
                      @KdfVersion, @ContentSha256, @DekFingerprint, 1, @now)",
            new
            {
                door.Kind, door.SourceId, door.SlotType, door.EncryptedMasterDek, door.Iv, door.Salt, door.ArgonMemory, door.ArgonIterations,
                door.ArgonParallelism, door.KeyHash, door.KdfVersion, door.ContentSha256, door.DekFingerprint, now = DateTime.UtcNow.ToString("o"),
            });
        return await connection.ExecuteScalarAsync<long>(
            "SELECT door_id FROM tbl_boot_door WHERE kind = @Kind AND source_id = @SourceId AND content_sha256 = @ContentSha256",
            new { door.Kind, door.SourceId, door.ContentSha256 });
    }

    /// <summary>Deletes every copy of one door (spec 4.3 rule 2).</summary>
    public async Task RetireAsync(string kind, long sourceId, CancellationToken ct = default)
    {
        using var connection = factory.Open();
        await connection.ExecuteAsync("DELETE FROM tbl_boot_door WHERE kind = @kind AND source_id = @sourceId", new { kind, sourceId });
    }
}
