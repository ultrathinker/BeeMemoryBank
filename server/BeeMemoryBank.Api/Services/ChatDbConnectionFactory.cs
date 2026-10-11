using System.Data;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Connection factory for the chat database (<c>{dataPath}/chat.db</c>).
///
/// This is a DISTINCT type from <c>BeeMemoryBank.Storage.DbConnectionFactory</c> (which is
/// DI-injected everywhere pointing at <c>beememorybank.db</c>). The two must NOT collide in
/// the DI container: this type intentionally does NOT implement
/// <c>Core.Interfaces.IDbConnectionFactory</c> and is registered by its concrete type only.
///
/// The chat DB is node-local only: never touched by <c>MigrationRunner</c>,
/// <c>EventLogger</c>/<c>EventApplier</c>, <c>SnapshotService</c>/<c>RestoreInitiatorService</c>,
/// or <c>SyncClient</c>. See <c>docs/ai-chat-implementation-plan.md</c> §1 ("Chat DB").
///
/// On a sealed node (docs/design/step-b-spec.md 7.2, 9) the file is page-encrypted under the chat page key: connections come from an
/// inner <see cref="DbConnectionFactory"/> of purpose <see cref="DbPurposes.Chat"/>, which keys them while the vault is open, throws
/// <see cref="BeeMemoryBank.Core.Interfaces.VaultSealedException"/> while it is closed and never opens the file unkeyed.
/// </summary>
public sealed class ChatDbConnectionFactory : ISealableConnections, IDisposable
{
    private readonly DbConnectionFactory _inner;
    private readonly IVaultKeySource? _keys;
    private readonly object _keepAliveLock = new();
    private SqliteConnection? _keepAlive;

    /// <param name="dataPath">The data directory.</param>
    /// <param name="keys">The page keys of an open sealed vault; null (or a key source in mode Plain) leaves the chat database plain.</param>
    public ChatDbConnectionFactory(string dataPath, IVaultKeySource? keys = null)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "chat.db");
        _keys = keys;
        _inner = new DbConnectionFactory(dbPath, keys, DbPurposes.Chat);
        // A held-open connection keeps WAL mode's -wal/-shm sidecars valid and prevents SQLite
        // from downgrading journal_mode when the last connection closes (matches DbConnectionFactory
        // keep-alive semantics; cheap, stateless from the app's perspective).
        // Opened here only for a plain file, as before. A sealed node's keep-alive lives for one open generation: it is opened by the first
        // connection of the generation and disposed at close (spec 7.2). A file that is encrypted although no key source was given (the
        // registration in ApiServices.cs builds this factory before the sealed-vault services replace it) is left alone, not opened.
        if (!IsKeyed && !LooksSealed(dbPath)) EnsureKeepAlive();
    }

    private bool IsKeyed => _keys is { } keys && keys.Mode != VaultStoreMode.Plain;

    private static bool LooksSealed(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true, Length: > 0 } && !VaultStoreFiles.HasSqliteMagic(path);
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>The key-free identity of the chat database file.</summary>
    public string DatabaseId => _inner.DatabaseId;

    /// <summary>Open connections this factory is tracking (the sealed keep-alive connection included).</summary>
    public int OpenConnections => _inner.OpenConnections;

    public IDbConnection CreateConnection()
    {
        var connection = _inner.CreateConnection();
        if (!_inner.IsQuiescing) EnsureKeepAlive();
        return connection;
    }

    /// <summary>A non-pooled connection to another file, keyed with the chat page key when sealed (see <see cref="DbConnectionFactory.OpenFile"/>).</summary>
    public SqliteConnection OpenFile(string path, bool readOnly = false) => _inner.OpenFile(path, readOnly);

    /// <summary>Starts the switch of the file; the keep-alive connection goes first, or the drain would wait for it for ever.</summary>
    public IDisposable BeginQuiesce()
    {
        var handle = _inner.BeginQuiesce();
        DisposeKeepAlive();
        return handle;
    }

    public Task WaitDrainedAsync(TimeSpan timeout, CancellationToken ct = default) => _inner.WaitDrainedAsync(timeout, ct);

    /// <summary>The keep-alive connection first, then the generation (see <see cref="DbConnectionFactory.CloseForSealAsync"/>).</summary>
    public Task<bool> CloseForSealAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        DisposeKeepAlive();
        return _inner.CloseForSealAsync(timeout, ct);
    }

    private void EnsureKeepAlive()
    {
        lock (_keepAliveLock)
        {
            if (_keepAlive is not null) return;
            if (IsKeyed)
            {
                // Pooled and keyed like every chat connection, counted in the drain until it is disposed.
                try { _keepAlive = (SqliteConnection)_inner.CreateConnection(); }
                catch (BeeMemoryBank.Core.Interfaces.VaultSealedException) { } // closed since the caller's own connection was made
            }
            else
            {
                var plain = new SqliteConnection(_inner.DatabaseId);
                plain.Open();
                _keepAlive = plain;
            }
        }
    }

    private void DisposeKeepAlive()
    {
        lock (_keepAliveLock)
        {
            _keepAlive?.Dispose();
            _keepAlive = null;
        }
    }

    public void Dispose()
    {
        DisposeKeepAlive();
        _inner.Dispose();
    }
}
