using System.Data;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Vault.Metadata;

/// <summary>
/// The durable record that a DEK rotation created a metadata key whose <c>metadata_key_set</c> event is not logged yet
/// (docs/design/step-a-spec.md 4.4): one row of <c>tbl_migration_marker</c>, key <see cref="Key"/>, value the key id.
/// The rotation's own transaction sets it together with the key row; the transaction that logs the event deletes it.
/// While it stands this node writes no sealed metadata and the rotation is not complete; it survives a crash, so the next
/// unlock finishes the step.
///
/// <para>Local bookkeeping like the other markers of that table: a snapshot carries the table but never imports it over the
/// local rows (see <c>SnapshotTables.SchemaMeta</c>), so a node that joins or restores from this node's snapshot never inherits it.</para>
/// </summary>
public static class MetadataKeyPublishMarker
{
    public const string Key = "mek_publish_pending";

    /// <summary>The key id whose publication is outstanding, or null.</summary>
    public static string? Read(IDbConnection conn, IDbTransaction? tx = null) =>
        conn.ExecuteScalar<string?>("SELECT value FROM tbl_migration_marker WHERE key = @Key", new { Key }, tx);

    /// <summary>Reads the marker on a connection of its own.</summary>
    public static string? Read(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        return Read(conn);
    }

    /// <summary>
    /// Sets the marker inside the caller's transaction. A marker that is already set is never overwritten: the publication it
    /// names would be lost, so the caller (a second rotation started too early) fails and its transaction rolls back.
    /// </summary>
    public static void Set(IDbConnection conn, IDbTransaction tx, string keyId)
    {
        if (Read(conn, tx) is { } pending)
            throw new InvalidOperationException(
                $"The metadata key {pending} of an earlier key rotation is not published yet; another key rotation must wait for it.");
        conn.Execute(
            "INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@Key, @keyId, @Now)",
            new { Key, keyId, Now = DateTime.UtcNow.ToString("O") }, tx);
    }

    /// <summary>Deletes the marker inside the caller's transaction; returns whether this call removed it.</summary>
    public static bool Clear(IDbConnection conn, IDbTransaction tx) =>
        conn.Execute("DELETE FROM tbl_migration_marker WHERE key = @Key", new { Key }, tx) > 0;

    /// <summary>
    /// The next generation number and the key row for it, created inside the DEK transaction of a rotation: wrapped under the
    /// <b>new</b> master DEK, with the marker set in the same transaction (spec 4.4). The event id and Lamport time of the row are
    /// filled when the event is logged. Returns the row; the open key is not kept.
    /// </summary>
    public static MetadataKeyRow CreateNextGeneration(IDbConnection conn, IDbTransaction tx, byte[] newDek, long newEpoch, string authorNodeId)
    {
        var generation = (conn.ExecuteScalar<int?>("SELECT MAX(generation) FROM tbl_metadata_key", transaction: tx) ?? 0) + 1;
        var (mek, row) = MetadataKeyCrypto.Create(generation, newDek);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(mek);
        conn.Execute(
            @"INSERT INTO tbl_metadata_key
                (key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, author_node_id, created_at, lamport_ts, event_id)
              VALUES (@KeyId, @Generation, @Fp, @Epoch, @Wrapped, @Iv, @Author, @CreatedAt, 0, '')",
            new
            {
                row.KeyId, row.Generation, Fp = row.DekFingerprint, Epoch = newEpoch, row.Wrapped, row.Iv,
                Author = authorNodeId, CreatedAt = DateTime.UtcNow.ToString("O")
            }, tx);
        Set(conn, tx, row.KeyId);
        return row;
    }
}
