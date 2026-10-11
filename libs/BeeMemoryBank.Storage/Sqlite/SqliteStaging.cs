using BeeMemoryBank.Core.IO;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// The one way a staging database is removed (every <see cref="SnapshotStaging.NewFile"/> user). A pooled connection that touched the file stays
/// open after it is disposed, holding the file and its WAL on Windows and leaving the WAL behind, so the pool of that file is cleared first.
/// </summary>
public static class SqliteStaging
{
    /// <summary>
    /// Clears the pool of <paramref name="path"/> and removes the file with its SQLite sidecars (<see cref="SnapshotStaging.TryCleanupFile"/>).
    /// False when something is left: a caller reports repair-required.
    /// </summary>
    public static bool TryCleanup(string path, Func<string, bool>? deleteFile = null)
    {
        // The pool key is the connection string a factory on this path opens with: a plain data source, no other option.
        SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()));
        return SnapshotStaging.TryCleanupFile(path, deleteFile);
    }
}
