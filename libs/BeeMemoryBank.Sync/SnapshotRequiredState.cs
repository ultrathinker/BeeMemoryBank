using System.Data;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Sync;

/// <summary>
/// "A full peer answered our pull with 410: its log was compacted past our position, and this node cannot catch up from
/// it - wipe it and join again." Set and cleared by the <see cref="SyncScheduler"/>. Shown on the Web UI (through
/// <c>/api/sync/status</c>) and by <c>bmb status</c>; before that nothing read it, and the only trace of a node stuck this
/// way was one Critical log line (BMB-81).
///
/// <para>Also kept in <c>tbl_migration_marker</c> (key <see cref="Key"/>), because <c>bmb status</c> is another process
/// than the one whose scheduler saw the 410. Local bookkeeping, never imported over the local rows.</para>
/// </summary>
public class SnapshotRequiredState(IDbConnectionFactory? db = null)
{
    public const string Key = "sync_snapshot_required";

    private volatile SnapshotRequiredException? _lastException;

    // A row left by an earlier run may exist until the first Clear of this one; after that, only a Set writes one.
    private volatile bool _rowMayExist = true;

    public bool IsRequired => _lastException != null;
    public SnapshotRequiredException? LastException => _lastException;

    public void Set(SnapshotRequiredException ex)
    {
        _lastException = ex;
        if (db == null) return;
        Persist(new Persisted(ex.RemoteUrl, ex.LastCompactionCp, ex.CurrentHeadSeq, DateTime.UtcNow));
        _rowMayExist = true;
    }

    public void Clear()
    {
        _lastException = null;
        if (db == null || !_rowMayExist) return;
        TryWrite(conn => conn.Execute("DELETE FROM tbl_migration_marker WHERE key = @Key", new { Key }));
        _rowMayExist = false;
    }

    /// <summary>What a run of this node last recorded, for a process that is not the one syncing; null when nothing is.</summary>
    public static Persisted? Read(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        var json = conn.ExecuteScalar<string?>("SELECT value FROM tbl_migration_marker WHERE key = @Key", new { Key });
        if (json == null) return null;
        try
        {
            return JsonSerializer.Deserialize<Persisted>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The one sentence the person needs, for the Web UI and <c>bmb status</c>.</summary>
    public static string Describe(string remoteUrl, long lastCompactionCp, long currentHeadSeq) =>
        $"This node cannot catch up: {remoteUrl} compacted its log past this node's position (checkpoint {lastCompactionCp}, " +
        $"head {currentHeadSeq}). Wipe this node and join again.";

    private void Persist(Persisted value) =>
        TryWrite(conn => conn.Execute(
            "INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@Key, @Value, @Now)",
            new { Key, Value = JsonSerializer.Serialize(value), Now = DateTime.UtcNow.ToString("O") }));

    // Best effort: the state is a report, and failing to record it must never fail the sync cycle that found it.
    private void TryWrite(Action<IDbConnection> write)
    {
        try
        {
            using var conn = db!.CreateConnection();
            if (conn.State != ConnectionState.Open) conn.Open();
            write(conn);
        }
        catch (Exception)
        {
            // Only a report: the scheduler's Critical log line still says it.
        }
    }

    public sealed record Persisted(string RemoteUrl, long LastCompactionCp, long CurrentHeadSeq, DateTime SinceUtc);
}
