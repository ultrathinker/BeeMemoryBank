using BeeMemoryBank.Core.Interfaces;
using Microsoft.Data.Sqlite;
using System.Data;

namespace BeeMemoryBank.Storage.Sqlite;

public class DbConnectionFactory : IDbConnectionFactory, IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _keepAlive; // keeps in-memory DB alive

    public DbConnectionFactory(string path)
    {
        string dbPath;
        if (Path.GetExtension(path)?.Equals(".db", StringComparison.OrdinalIgnoreCase) == true)
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
        _connectionString = $"Data Source={dbPath}";
    }

    private DbConnectionFactory(string connectionString, bool _)
    {
        _connectionString = connectionString;
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
        var factory = new DbConnectionFactory(cs, true) { _tempFilePath = path };
        factory._keepAlive = new SqliteConnection(cs);
        factory._keepAlive.Open();
        return factory;
    }

    /// <summary>The connection string is what actually distinguishes one database from
    /// another here, and it is not a secret (a local file path or a temp path).</summary>
    public string DatabaseId => _connectionString;

    // Connection lifetimes, for a switch of the database file (BeginQuiesce): how many connections
    // this factory handed out are open now, whether new opens have to wait, and the flow that switches.
    private readonly object _lifetimes = new();
    private int _open;
    private bool _quiesced;
    private TaskCompletionSource? _drained;
    private static readonly AsyncLocal<DbConnectionFactory?> Quiescer = new();

    public IDbConnection CreateConnection()
    {
        var counted = EnterOpen();
        var connection = new SqliteConnection(_connectionString);
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
            Initialize(connection);
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
            // before it touches a moved file and in Dispose, for the same reason.
            connection.Dispose();
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
            throw;
        }
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        // temp_store=FILE: the native SQLite is SQLite3 Multiple Ciphers since 2.5.3, compiled with SQLITE_TEMP_STORE=2 (temp data in
        // memory unless asked otherwise); the stock build before it used files. An in-place VACUUM or a big sort would keep the whole
        // database in RAM (VACUUM of a vault that shrinks from 351 to 175 MiB: 257 MiB peak working set, 57 MiB with FILE; measured),
        // so the factory asks for the old behaviour.
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA temp_store=FILE;";
        cmd.ExecuteNonQuery();
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
