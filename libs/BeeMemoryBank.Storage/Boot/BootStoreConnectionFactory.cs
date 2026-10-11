using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>
/// Opens the boot store (docs/design/step-b-spec.md 2.3): plain, WAL, <c>foreign_keys=ON</c>, <c>busy_timeout=5000</c>,
/// <c>temp_store=FILE</c>. Deliberately NOT an <c>IDbConnectionFactory</c>: no repository of the vault can be handed this file by a
/// registration mistake. Connections are not pooled, so a boot store in a temporary or backed-up folder is never held open by the
/// provider after the last connection is disposed (the file is a few kilobytes and opening costs microseconds).
/// </summary>
public sealed class BootStoreConnectionFactory(string path)
{
    public string FilePath { get; } = path;

    /// <summary>An open connection. The caller disposes it. A missing file is created empty (use <see cref="BootStore.EnsureCreatedAsync"/>).</summary>
    public SqliteConnection Open() => Open(readOnly: false);

    /// <summary>A read-only connection (status readers); the file must exist.</summary>
    public SqliteConnection OpenReadOnly() => Open(readOnly: true);

    private SqliteConnection Open(bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            // The command timeout; the engine's busy handler is set below so both agree.
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA temp_store = FILE;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
