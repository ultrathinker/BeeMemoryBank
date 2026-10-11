using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>
/// Opens SQLCipher 4 files under a raw 256-bit page key (docs/design/step-b-spec.md 7.1).
///
/// The key is NEVER written into a connection string. Microsoft.Data.Sqlite keeps every connection string it is given in its pool
/// registry, pooled or not, and neither <c>ClearPool</c> nor <c>ClearAllPools</c> removes it (measured with Microsoft.Data.Sqlite 10.0.11:
/// the entry, the password and the string stay in the registry until the provider's private prune timer has run three times). A
/// <c>Password=</c> in the string would therefore keep the key reachable after the vault closed. Here the connection string is key-free
/// and the key goes to the native connection through <c>sqlite3_key_v2</c>, as 67 ASCII bytes (<c>x'&lt;64 hex&gt;'</c>) in a byte array
/// that is zeroed straight after; no .NET string ever holds it.
/// </summary>
public static class SealedSqlite
{
    private const int NotADatabase = 26;

    /// <summary>
    /// Opens <paramref name="path"/> with its own, not pooled, connection (<c>Pooling=False</c>), sets the page key and checks it. A key
    /// that does not open the file (a wrong key, or a plain file) throws <see cref="VaultKeyMismatchException"/>; nothing is ever
    /// retried unkeyed. <paramref name="busyTimeoutSeconds"/> is how long a locked file is waited for. The caller disposes the connection and clears its own copy of the key.
    /// <paramref name="immutable"/> (read-only reads of a file nothing writes, such as a vault the re-key only reads, spec 13.2): the engine takes no
    /// lock and opens no WAL or shared-memory file, so the directory is not touched at all; a WAL that is not empty is not read.
    /// </summary>
    public static SqliteConnection OpenKeyed(string path, ReadOnlySpan<byte> pageKey, bool readOnly = false, int busyTimeoutSeconds = 30,
        bool immutable = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = SqlCipherScheme.DataSource(path) + (immutable ? "&immutable=1" : ""),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = busyTimeoutSeconds,
        }.ToString());
        try
        {
            connection.Open();
            ApplyKey(connection, pageKey);
            ConfigureMemoryTempStore(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sets the page key on an open connection that has not read the file yet and proves it by reading the schema. The connection's
    /// string must name the cipher (<see cref="SqlCipherScheme.DataSource"/>). <see cref="VaultKeyMismatchException"/> when the key does
    /// not open the file; the message and the exception carry no key.
    /// </summary>
    public static void ApplyKey(SqliteConnection connection, ReadOnlySpan<byte> pageKey)
    {
        if (pageKey.Length != 32) throw new ArgumentException("A page key is exactly 32 bytes.", nameof(pageKey));
        var handle = connection.Handle ?? throw new InvalidOperationException("The connection is not open.");

        var text = new byte[67];
        try
        {
            text[0] = (byte)'x';
            text[1] = (byte)'\'';
            for (var i = 0; i < 32; i++)
            {
                text[2 + 2 * i] = HexDigit(pageKey[i] >> 4);
                text[3 + 2 * i] = HexDigit(pageKey[i] & 0xF);
            }
            text[66] = (byte)'\'';
            var rc = raw.sqlite3_key_v2(handle, utf8z.FromString("main"), text);
            if (rc != raw.SQLITE_OK) throw new InvalidOperationException($"The engine refused the page key (SQLite result {rc}).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
            cmd.ExecuteScalar();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == NotADatabase)
        {
            // Deliberately without the inner exception: its text is the engine's and carries nothing, but a key never rides along.
            throw new VaultKeyMismatchException("The database key does not open this file.");
        }
    }

    /// <summary>
    /// Configures and verifies the sealed connection's transient storage before callers can run a sort, VACUUM, or other operation
    /// that creates temporary SQLite files. The compile-option check is native and deliberately fail-closed: a library compiled to
    /// force disk storage, or a library whose temp-store policy cannot be identified, is not safe for a sealed database.
    /// </summary>
    internal static void ConfigureMemoryTempStore(SqliteConnection connection)
    {
        _ = connection.Handle ?? throw new InvalidOperationException("The connection is not open.");
        var allowsMemory = raw.sqlite3_compileoption_used("TEMP_STORE=2") != 0
                           || raw.sqlite3_compileoption_used("TEMP_STORE=3") != 0;
        var forcesDisk = raw.sqlite3_compileoption_used("TEMP_STORE=1") != 0;
        if (!allowsMemory || forcesDisk)
            throw new InvalidOperationException("The native SQLite build cannot guarantee memory-only temporary storage for a sealed database.");

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA temp_store=MEMORY;";
            cmd.ExecuteNonQuery();
        }

        using var verifyTemp = connection.CreateCommand();
        verifyTemp.CommandText = "PRAGMA temp_store;";
        var tempStore = Convert.ToInt32(verifyTemp.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (tempStore != 2)
            throw new InvalidOperationException("The native SQLite connection did not establish memory-only temporary storage for a sealed database.");
    }

    private static byte HexDigit(int v) => (byte)(v < 10 ? '0' + v : 'a' + (v - 10));

    /// <summary>The native connections one pool generation has keyed, by the identity of their handle object (no secret in it).</summary>
    internal sealed class KeyedHandles
    {
        private readonly ConditionalWeakTable<sqlite3, object> _keyed = new();

        /// <summary>True for a pooled connection that was keyed when it was new; a fresh physical connection is not in the table.</summary>
        public bool Contains(SqliteConnection connection) => connection.Handle is { } h && _keyed.TryGetValue(h, out _);

        public void Add(SqliteConnection connection)
        {
            if (connection.Handle is { } h) _keyed.AddOrUpdate(h, Marker);
        }

        private static readonly object Marker = new();
    }
}
