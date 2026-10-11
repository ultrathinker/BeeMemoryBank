using System.Text.Json;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Vault.Metadata;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Sync;

/// <summary>
/// What a joiner does with the database entry of a join archive once the host is activated (docs/design/step-a-spec.md 11): the entry is a
/// BMBCH1 container under the master DEK the joiner has just been handed, opened here and only then imported. A joiner that knows the host
/// declares protocol 4, or finds activation rows in a plain database, refuses a plain archive: a host that seals its metadata never sends
/// one, so a plain archive from it is a downgrade, and the join stops (fail closed).
/// </summary>
public static class JoinSnapshotCipher
{
    /// <summary>The <c>dbCipher</c> value of the manifest of an encrypted join archive.</summary>
    public const string Cipher = "bmbch1";

    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    /// <summary>The <c>dbCipher</c> the (already signature-checked) manifest names, or null.</summary>
    public static string? CipherOf(JsonElement manifestRoot) =>
        manifestRoot.TryGetProperty("dbCipher", out var cipher) && cipher.ValueKind == JsonValueKind.String ? cipher.GetString() : null;

    /// <summary>
    /// Leaves a plain SQLite file at <paramref name="dbPath"/>. An encrypted archive is opened with <paramref name="masterDek"/> in place;
    /// a plain one is checked against the downgrade rules. Throws <see cref="InvalidOperationException"/> with a sentence a person can act on.
    /// </summary>
    /// <param name="requireEncrypted">The joiner knows the host runs with encrypted metadata (it declared protocol 4).</param>
    public static async Task OpenAsync(string? manifestCipher, string dbPath, byte[]? masterDek, bool requireEncrypted, CancellationToken ct = default)
    {
        if (manifestCipher is null)
        {
            if (requireEncrypted)
                throw new InvalidOperationException(
                    "The other computer encrypts its notes' metadata but sent a plain snapshot; the join was stopped. Update that computer and try again.");
            if (HasActivationRows(dbPath))
                throw new InvalidOperationException(
                    "The snapshot is plain but belongs to a vault that encrypts its metadata; the join was stopped. Update the other computer and try again.");
            return;
        }

        if (manifestCipher != Cipher)
            throw new InvalidOperationException($"The snapshot is encrypted in a way this version does not know ('{manifestCipher}'); update this device.");
        if (masterDek is not { Length: 32 })
            throw new InvalidOperationException("The snapshot is encrypted but this join has no master key to open it with.");

        var fingerprint = DekFingerprint.Of(masterDek);
        var openedPath = dbPath + ".opened";
        try
        {
            try
            {
                await using (var container = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                await using (var opened = new FileStream(openedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await ChunkedContainer.DecryptAsync(container, opened, ChunkedContainer.PurposeJoin,
                        h => h.Key == ChunkedContainer.DekKeyLabel(fingerprint) ? masterDek : null, ct);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException(
                    "The snapshot could not be opened with this vault's master key (it was made by another vault, or it was damaged on the way); nothing was imported.", ex);
            }

            await using (var check = File.OpenRead(openedPath))
            {
                var head = new byte[SqliteMagic.Length];
                if (await check.ReadAsync(head, ct) != head.Length || !head.AsSpan().SequenceEqual(SqliteMagic))
                    throw new InvalidOperationException("The opened snapshot is not a database; nothing was imported.");
            }
            File.Move(openedPath, dbPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(openedPath)) File.Delete(openedPath);
        }
    }

    /// <summary>True when the plain database holds a metadata activation: its host seals metadata and never sends a plain archive.</summary>
    private static bool HasActivationRows(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var exists = conn.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'tbl_metadata_activation'";
        if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return false;
        using var rows = conn.CreateCommand();
        rows.CommandText = "SELECT COUNT(*) FROM tbl_metadata_activation";
        return Convert.ToInt64(rows.ExecuteScalar()) > 0;
    }
}
