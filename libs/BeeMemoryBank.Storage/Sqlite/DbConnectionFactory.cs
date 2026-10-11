using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Security.Cryptography;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// The connection factory of one SQLite file. With no key source (or a key source in mode Plain) it is exactly the factory of 2.5: a
/// plain file, pooled. With a key source in a mode other than Plain the file is a sealed SQLCipher 4 file (docs/design/step-b-spec.md
/// 7.1-7.3): every connection is keyed with the page key of <c>purpose</c> while the key source is open, a closed key source throws
/// <see cref="VaultSealedException"/> before any file is touched, and nothing ever falls back to an unkeyed open.
/// </summary>
public class DbConnectionFactory : IDbConnectionFactory, ISealableConnections, IDisposable
{
    private readonly string _connectionString;
    private readonly string _path;
    private readonly IVaultKeySource? _keys;
    private readonly string _purpose;
    private SqliteConnection? _keepAlive; // keeps in-memory DB alive

    // What the keyed side holds, all under _keyLock, and none of it is secret: the key-free pooled connection string of the current open
    // generation (a non-secret nonce makes the provider's pool one per factory and generation), the table of native connections this
    // generation has keyed, and the generation CloseForSealAsync ended. The page key itself is read from the key source for each fresh
    // physical connection, applied natively and zeroed (SealedSqlite): no field of this class, no connection string and no pool of the
    // provider ever holds it (spec 3.3, 7.2).
    private readonly object _keyLock = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private string? _pooledConnectionString;
    private long _pooledGeneration;
    private SealedSqlite.KeyedHandles _keyed = new();
    private long _closedGeneration = long.MinValue;

    /// <param name="path">The database file, or a folder that holds <c>beememorybank.db</c>.</param>
    /// <param name="keys">The page keys of an open sealed vault; null on a blind host and wherever nothing is sealed.</param>
    /// <param name="purpose">Which page key opens this file: <see cref="DbPurposes.Vault"/> or <see cref="DbPurposes.Chat"/>.</param>
    public DbConnectionFactory(string path, IVaultKeySource? keys = null, string purpose = DbPurposes.Vault)
    {
        string dbPath;
        // A path with the extension .db is the database file; so is a path that already is a file (a migration target such as
        // beememorybank.db.seal-new, spec 10.4, which no folder can be named); anything else is the folder that holds beememorybank.db.
        if (Path.GetExtension(path)?.Equals(".db", StringComparison.OrdinalIgnoreCase) == true || File.Exists(path))
        {
            dbPath = path;
        }
        else
        {
            dbPath = Path.Combine(path, "beememorybank.db");
        }

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _path = dbPath;
        _connectionString = $"Data Source={dbPath}";
        _keys = keys;
        _purpose = purpose;
    }

    private DbConnectionFactory(string path, string connectionString, IVaultKeySource? keys)
    {
        _path = path;
        _connectionString = connectionString;
        _keys = keys;
        _purpose = DbPurposes.Vault;
    }

    private string? _tempFilePath;

    /// <summary>
    /// Creates a factory backed by a temporary file SQLite database (for tests).
    /// Deliberately not shared-cache in-memory (Mode=Memory;Cache=Shared): that does NOT support
    /// VACUUM INTO (silently produces an empty target file), which any test going through
    /// SnapshotService.CreateAsync needs. /tmp is typically tmpfs on Linux so the
    /// performance hit is negligible. The file is auto-deleted on Dispose.
    /// </summary>
    public static DbConnectionFactory CreateInMemory(string name = "bmb_test")
    {
        var path = Path.Combine(Path.GetTempPath(), $"bmb_test_{name}_{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path}";
        var factory = new DbConnectionFactory(path, cs, null) { _tempFilePath = path };
        factory._keepAlive = new SqliteConnection(cs);
        factory._keepAlive.Open();
        return factory;
    }

    /// <summary>
    /// Creates a factory over a temporary SEALED file for tests: <see cref="CreateInMemory"/> with a key source, a random page key by
    /// default (<see cref="InMemoryVaultKeySource"/>, open). The file is created by the first connection and deleted on Dispose.
    /// </summary>
    public static DbConnectionFactory CreateSealedForTests(string name = "bmb_test", IVaultKeySource? keys = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bmb_test_{name}_{Guid.NewGuid():N}.db");
        return new DbConnectionFactory(path, $"Data Source={path}", keys ?? new InMemoryVaultKeySource()) { _tempFilePath = path };
    }

    /// <summary>The database file this factory opens (a path, not a secret).</summary>
    public string FilePath => _path;

    /// <summary>
    /// What distinguishes one database from another here; it is not a secret. Plain: the connection string (a local file path). Sealed: the
    /// key-free data source (<see cref="SqlCipherScheme.DataSource"/>). MUST NOT contain key material in any mode (spec 7.2): it is a
    /// cache key (FolderAccessService) and is opened directly by the blind seed.
    /// </summary>
    public string DatabaseId
    {
        get
        {
            if (!IsKeyed) return _connectionString;
            try { return SqlCipherScheme.DataSource(_path); }
            catch (NotSupportedException) { return _connectionString; } // a UNC path: no connection can be built for it either
        }
    }

    /// <summary>True when this file is sealed: a key source in a mode other than Plain (Broken counts, and then nothing opens).</summary>
    private bool IsKeyed => _keys is { } keys && keys.Mode != VaultStoreMode.Plain;

    /// <summary>
    /// True when this factory opens a sealed (encrypted) file. The code that stages copies of the vault (snapshots, restores) asks this to
    /// decide whether a plain image may be written at all (docs/design/step-b-spec.md 8.1): on a sealed node it may not.
    /// </summary>
    public bool IsSealed => IsKeyed;

    /// <summary>
    /// A copy of the 32-byte page key of this factory's file, for the page codec (<c>PlainImageReader</c>, <c>PlainImageImporter</c>); the
    /// caller clears it. <see cref="VaultSealedException"/> while the vault is closed; null on a plain factory.
    /// </summary>
    public byte[]? CopyPageKey() => IsKeyed ? _keys!.GetPageKey(_purpose) : null;

    /// <summary>
    /// A database file this node did not make itself, opened for reading or editing (spec 7.5): one with the SQLite header is a package or a
    /// backup copy of a blind node, plain by design, and gets a plain connection; any other file is a staging copy of this vault and is opened like
    /// <see cref="OpenFile"/>. On a plain factory every file is opened plain. Not pooled; the caller disposes it.
    /// </summary>
    public SqliteConnection OpenPackageFile(string path, bool readOnly = true)
    {
        if (!IsKeyed || !VaultStoreFiles.HasSqliteMagic(path)) return OpenFile(path, readOnly);
        var plain = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            plain.Open();
            return plain;
        }
        catch
        {
            plain.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The <c>ATTACH DATABASE</c> statement that attaches <paramref name="path"/> as <paramref name="alias"/> to a connection of this factory
    /// (spec 7.4). On a plain factory it is the statement every caller has always written. On a sealed factory a file with the SQLite header
    /// (a plain package database) is attached with <c>KEY ''</c>; any other file is a staging copy under this vault's page key and is attached
    /// with that key and the cipher of the vault in the URI. The key clause is explicit even though a <c>VACUUM INTO</c> copy would attach
    /// without one: a copy written by the page codec has a salt of its own, and the engine then refuses it (error 26) unless it is given the key.
    /// <b>On a sealed factory the text holds the page key</b>: run it once and drop it, never log it, never keep it (spec 3.3).
    /// </summary>
    public string AttachStatement(string path, string alias)
    {
        if (!IsKeyed) return $"ATTACH DATABASE '{path.Replace("'", "''")}' AS {alias}";
        if (VaultStoreFiles.HasSqliteMagic(path)) return $"ATTACH DATABASE '{path.Replace("'", "''")}' AS {alias} KEY ''";
        var key = _keys!.GetPageKey(_purpose);
        try
        {
            return $"ATTACH DATABASE '{SqlCipherScheme.DataSource(path).Replace("'", "''")}' AS {alias} KEY \"{SqlCipherScheme.Password(key)}\"";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // Connection lifetimes, for a switch of the database file (BeginQuiesce): how many connections
    // this factory handed out are open now, whether new opens have to wait, and the flow that switches.
    private readonly object _lifetimes = new();
    private int _open;
    private bool _quiesced;
    private TaskCompletionSource? _drained;
    private static readonly AsyncLocal<DbConnectionFactory?> Quiescer = new();

    public IDbConnection CreateConnection()
    {
        if (IsKeyed) ThrowIfClosed(); // before the file or the switch gate is touched
        var counted = EnterOpen();
        var keyed = IsKeyed; // decided after the gate: a switch of plain to sealed happens quiesced
        var connectionString = _connectionString;
        SealedSqlite.KeyedHandles? handles = null;
        long generation = 0;
        if (keyed)
        {
            try
            {
                (connectionString, handles, generation) = KeyedPool();
            }
            catch
            {
                if (counted) ExitOpen();
                throw;
            }
        }

        var connection = new SqliteConnection(connectionString);
        if (counted)
        {
            var released = 0;
            void Release()
            {
                if (Interlocked.Exchange(ref released, 1) == 0) ExitOpen();
            }
            connection.StateChange += (_, e) => { if (e.CurrentState == ConnectionState.Closed) Release(); };
            connection.Disposed += (_, _) => Release();
            try
            {
                connection.Open();
            }
            catch
            {
                Release();
                throw;
            }
        }
        else connection.Open();

        try
        {
            if (keyed) EnsureKeyed(connection, handles!, generation);
            Initialize(connection, keyed);
            return connection;
        }
        catch
        {
            // The lease is released for failures AFTER the open too, not just for the open itself
            // (review l-root5 #1). A PRAGMA that fails on a transient lock - journal_mode=WAL needs
            // a moment of exclusivity - used to leave the connection open and _open permanently
            // incremented: the caller saw an error, but every later seed then waited the whole
            // drain timeout for a connection that no longer existed and failed with 400.
            //
            // Dispose covers both cases: it closes the handle and releases the lease through the
            // Disposed handler, and Release is idempotent, so the StateChange that closing also
            // raises cannot release twice. For a quiesced flow (counted == false) there is no lease
            // to release, and the connection still has to go.
            //
            // Closing is not enough on its own (orchestrator, 28.09): Microsoft.Data.Sqlite pools
            // by connection string, so the failed connection goes into the pool and its native
            // handle - and on Windows the file lock - stays open for a database the caller never
            // received. The pool is cleared straight after, which is what this factory already does
            // before it touches a moved file and in Dispose, for the same reason. (A sealed pool
            // string is key-free, so clearing it from here is safe too.)
            connection.Dispose();
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));
            throw;
        }
    }

    /// <summary>
    /// A non-pooled connection to another file (a staging copy, a downloaded database) that opens like this factory's own file
    /// (spec 7.2): keyed with this factory's page key when sealed, plain otherwise. Sealed: counted in the drain until it is disposed,
    /// <see cref="VaultSealedException"/> when closed, <see cref="VaultKeyMismatchException"/> when the key does not open the file.
    /// The caller disposes it.
    /// </summary>
    public SqliteConnection OpenFile(string path, bool readOnly = false)
    {
        if (!IsKeyed)
        {
            var plain = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            try
            {
                plain.Open();
                return plain;
            }
            catch
            {
                plain.Dispose();
                throw;
            }
        }

        ThrowIfClosed();
        var counted = EnterOpen();
        SqliteConnection? connection = null;
        byte[]? key = null;
        try
        {
            ThrowIfClosed();
            key = _keys!.GetPageKey(_purpose);
            connection = SealedSqlite.OpenKeyed(path, key, readOnly);
            if (counted)
            {
                var released = 0;
                void Release()
                {
                    if (Interlocked.Exchange(ref released, 1) == 0) ExitOpen();
                }
                connection.StateChange += (_, e) => { if (e.CurrentState == ConnectionState.Closed) Release(); };
                connection.Disposed += (_, _) => Release();
            }
            return connection;
        }
        catch
        {
            connection?.Dispose();
            if (counted) ExitOpen();
            throw;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Throws <see cref="VaultSealedException"/> unless the key source is open and this generation has not been ended.</summary>
    private void ThrowIfClosed()
    {
        var keys = _keys!;
        if (!keys.IsOpen) throw new VaultSealedException();
        lock (_keyLock)
        {
            if (keys.OpenGeneration == _closedGeneration) throw new VaultSealedException();
        }
    }

    /// <summary>
    /// The key-free pooled connection string of the current open generation, built once per generation; a new generation clears the
    /// previous generation's pool before its string is used (spec 7.2).
    /// </summary>
    private (string ConnectionString, SealedSqlite.KeyedHandles Handles, long Generation) KeyedPool()
    {
        var keys = _keys!;
        lock (_keyLock)
        {
            if (!keys.IsOpen) throw new VaultSealedException();
            var generation = keys.OpenGeneration;
            if (generation == _closedGeneration) throw new VaultSealedException();
            if (_pooledConnectionString is null || _pooledGeneration != generation)
            {
                if (_pooledConnectionString is { } old) ClearPoolOf(old);
                _pooledConnectionString = new SqliteConnectionStringBuilder
                {
                    // The nonce is not a secret; it only makes the provider's pool one per factory and generation.
                    DataSource = SqlCipherScheme.DataSource(_path) + "&bmbpool=" + _instance + "-" + generation,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = true,
                }.ToString();
                _pooledGeneration = generation;
                _keyed = new SealedSqlite.KeyedHandles();
            }
            return (_pooledConnectionString, _keyed, generation);
        }
    }

    /// <summary>Keys a connection the pool just created; a connection it handed back again was keyed when it was new.</summary>
    private void EnsureKeyed(SqliteConnection connection, SealedSqlite.KeyedHandles handles, long generation)
    {
        if (handles.Contains(connection)) return;
        var key = _keys!.GetPageKey(_purpose);
        try
        {
            if (_keys.OpenGeneration != generation) throw new VaultSealedException(); // closed or re-keyed since KeyedPool
            SealedSqlite.ApplyKey(connection, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        handles.Add(connection);
    }

    private static void ClearPoolOf(string connectionString)
    {
        SqliteConnection.ClearPool(new SqliteConnection(connectionString));
    }

    private static void Initialize(SqliteConnection connection, bool keyed)
    {
        using var cmd = connection.CreateCommand();
        // temp_store=FILE: the native SQLite is SQLite3 Multiple Ciphers since 2.5.3, compiled with SQLITE_TEMP_STORE=2 (temp data in
        // memory unless asked otherwise); the stock build before it used files. An in-place VACUUM or a big sort would keep the whole
        // database in RAM (VACUUM of a vault that shrinks from 351 to 175 MiB: 257 MiB peak working set, 57 MiB with FILE; measured),
        // so the factory asks for the old behaviour.
        // A sealed connection keeps temp data in memory instead (spec 7.1): SQLite3 Multiple Ciphers does not encrypt temporary
        // files, so a sort or an in-place VACUUM must not spill to the disk of a sealed node. ConfigureMemoryTempStore adds the
        // native compile-option guard and verifies the per-connection state after this common initialization.
        cmd.CommandText = keyed
            ? "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"
            : "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA temp_store=FILE;";
        cmd.ExecuteNonQuery();
        if (keyed) SealedSqlite.ConfigureMemoryTempStore(connection);
        connection.CreateFunction("unicode_contains", (string? text, string? search) =>
            text != null && search != null && text.Contains(search, StringComparison.OrdinalIgnoreCase));
        // Content addressing for tbl_blob. SQLite ships no hash function, and migration 016 has to
        // key existing article bodies and versions by the hash of their ciphertext to fold the
        // duplicates together — that is not expressible in plain SQL, and this project runs
        // migrations only through the app, so registering the function here is the way to keep the
        // migration a .sql file like every other one. Deterministic and pure, as SQLite requires of
        // a function usable in an index or a constraint.
        //
        // Returns lowercase hex rather than a BLOB so hashes stay greppable in a sqlite3 shell and
        // compare as ordinary TEXT — the volume is 64 bytes per row against bodies measured in
        // kilobytes, so the encoding overhead is irrelevant here.
        connection.CreateFunction("sha256", (byte[]? data) =>
            data == null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant());
    }

    /// <summary>
    /// Starts a switch of this database's file (a blind node's seed, plan 5.2): from now on a new
    /// connection waits until the returned handle is disposed, except in the flow that called this,
    /// which goes on opening connections for its own work. Follow it with <see cref="WaitDrainedAsync"/>
    /// before the file is touched: a connection opened before the switch keeps the old file open, and
    /// on Linux it would go on reading — and writing — the file the switch moved away (review l-root4 #1).
    /// </summary>
    public IDisposable BeginQuiesce()
    {
        lock (_lifetimes)
        {
            if (_quiesced) throw new InvalidOperationException("This database is already being switched.");
            _quiesced = true;
        }
        // Set here, in a synchronous method, so it stays set in the caller's flow.
        Quiescer.Value = this;
        return new QuiesceHandle(this);
    }

    /// <summary>
    /// Waits until every connection opened outside the switching flow is closed, then closes the idle
    /// pooled handles too. Throws <see cref="TimeoutException"/> if one is still open after
    /// <paramref name="timeout"/>: the caller must then not switch the file.
    /// </summary>
    public async Task WaitDrainedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Task drained;
        lock (_lifetimes)
        {
            if (!_quiesced) throw new InvalidOperationException("BeginQuiesce first.");
            drained = _open == 0
                ? Task.CompletedTask
                : (_drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await drained.WaitAsync(timeout, ct);
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        string? pooled;
        lock (_keyLock) pooled = _pooledConnectionString;
        if (pooled is not null) ClearPoolOf(pooled);
    }

    /// <summary>
    /// Ends the open generation of a sealed file (spec 5.4 step 3, 7.2): from here on no connection is handed out for it, the connections
    /// still open are waited for up to <paramref name="timeout"/>, the provider's pool is flushed and the pool string dropped. It returns
    /// only after all of that, so the gate may then publish Closed. False when a connection was still in use at the limit (it is closed
    /// when its owner returns it; the caller logs the warning). Run it inside the flow's own <see cref="BeginQuiesce"/> (the gate keeps
    /// the quiesce for its checkpoint connection) or alone, when it quiesces by itself. Plain: nothing to close, true.
    /// </summary>
    public async Task<bool> CloseForSealAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsKeyed) return true;
        // The generation ends first, whoever else holds the quiesce: from this line no connection is handed out for it and the drain below converges
        // without a barrier of our own. A close must never wait for, or give up because of, a file switch that is under way (review round 1, H4).
        lock (_keyLock) _closedGeneration = _keys!.OpenGeneration;
        IDisposable? ownQuiesce = null;
        if (Quiescer.Value != this)
        {
            try { ownQuiesce = BeginQuiesce(); }
            catch (InvalidOperationException) { /* another flow is switching the file: its barrier stands, and ours is not needed */ }
        }
        var drained = true;
        try
        {
            try
            {
                if (ownQuiesce is not null || Quiescer.Value == this) await WaitDrainedAsync(timeout, ct).ConfigureAwait(false);
                else await WaitNoConnectionsAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                drained = false;
            }
        }
        finally
        {
            string? pooled;
            lock (_keyLock)
            {
                pooled = _pooledConnectionString;
                _pooledConnectionString = null;
                _keyed = new SealedSqlite.KeyedHandles();
            }
            if (pooled is not null) ClearPoolOf(pooled);
            ownQuiesce?.Dispose();
        }
        return drained;
    }

    /// <summary>Waits until no connection is open, without holding a barrier: for a generation that has already ended, where nothing new can open.</summary>
    private async Task WaitNoConnectionsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (OpenConnections > 0)
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException();
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    /// <summary>True while a <see cref="BeginQuiesce"/> handle is alive (in any flow).</summary>
    public bool IsQuiescing
    {
        get { lock (_lifetimes) return _quiesced; }
    }

    /// <summary>Open connections this factory is tracking (outside a switching flow).</summary>
    public int OpenConnections
    {
        get { lock (_lifetimes) return _open; }
    }

    private bool EnterOpen()
    {
        if (Quiescer.Value == this) return false;
        lock (_lifetimes)
        {
            while (_quiesced) Monitor.Wait(_lifetimes);
            _open++;
            return true;
        }
    }

    private void ExitOpen()
    {
        lock (_lifetimes)
        {
            if (--_open == 0 && _drained is { } drained)
            {
                _drained = null;
                drained.TrySetResult();
            }
        }
    }

    private sealed class QuiesceHandle(DbConnectionFactory factory) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (factory._lifetimes)
            {
                factory._quiesced = false;
                factory._drained = null;
                Monitor.PulseAll(factory._lifetimes);
            }
            if (Quiescer.Value == factory) Quiescer.Value = null;
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        // Idempotent: the same instance is registered under both DbConnectionFactory and
        // IDbConnectionFactory, so the container can capture and dispose it twice.
        if (_disposed) return;
        _disposed = true;

        _keepAlive?.Dispose();
        _keepAlive = null;

        // Disposing a SqliteConnection only returns it to the pool; the native handle stays open on
        // the database file. Every connection this factory ever handed out is still pooled under
        // _connectionString, so without this the file remains locked after the factory is gone.
        //
        // On Linux that is harmless (unlink works on an open file) and the process usually exits
        // anyway. In-process it is not: the temp-file deletes below would silently fail and leak a
        // DB per test, and removing the data directory afterwards throws IOException on
        // beememorybank.db -- both look like flakes but are a leaked handle.
        try { SqliteConnection.ClearPool(new SqliteConnection(_connectionString)); } catch { }
        string? pooled;
        lock (_keyLock)
        {
            pooled = _pooledConnectionString;
            _pooledConnectionString = null;
        }
        if (pooled is not null) { try { ClearPoolOf(pooled); } catch { } }

        if (_tempFilePath != null)
        {
            // SQLite WAL leftover side files: -wal, -shm, -journal
            foreach (var ext in new[] { "", "-wal", "-shm", "-journal" })
            {
                try { if (File.Exists(_tempFilePath + ext)) File.Delete(_tempFilePath + ext); } catch { }
            }
        }
    }
}
