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
/// <para>Kept per peer: the state is the peer's, so the success of ANOTHER peer in the same or a later cycle does not end
/// it - only the stuck peer answering again (or leaving the whitelist) does. The state is required while any peer is stuck.</para>
///
/// <para>Also kept in <c>tbl_migration_marker</c> (key <see cref="Key"/>), because <c>bmb status</c> is another process
/// than the one whose scheduler saw the 410. One row, naming one of the stuck peers. Local bookkeeping, never imported over the local rows.</para>
/// </summary>
public class SnapshotRequiredState(IDbConnectionFactory? db = null)
{
    public const string Key = "sync_snapshot_required";

    private readonly object _gate = new();
    private readonly Dictionary<Guid, SnapshotRequiredException> _stuck = [];
    private volatile SnapshotRequiredException? _lastException;

    // The row an earlier run left is looked at once, the first time a success has to decide about it.
    private bool _rowLoaded;
    private bool _rowExists;
    private Guid? _rowPeer;

    public bool IsRequired => _lastException != null;
    public SnapshotRequiredException? LastException => _lastException;

    /// <summary><paramref name="peerNodeId"/> is the peer that answered 410; without it the state is not tied to one peer.</summary>
    public void Set(SnapshotRequiredException ex, Guid? peerNodeId = null)
    {
        lock (_gate)
        {
            _stuck[peerNodeId ?? Guid.Empty] = ex;
            _lastException = ex;
            if (db == null) return;
            Persist(ex, peerNodeId);
            _rowLoaded = _rowExists = true;
            _rowPeer = peerNodeId;
        }
    }

    /// <summary>
    /// <paramref name="peerNodeId"/> answered a sync: ends what was recorded for that peer only. Without a peer, ends everything.
    /// A row an earlier run left for ANOTHER peer stays until that peer answers or leaves the whitelist.
    /// </summary>
    public void Clear(Guid? peerNodeId = null)
    {
        lock (_gate)
        {
            if (peerNodeId is null) _stuck.Clear();
            else _stuck.Remove(peerNodeId.Value);
            _lastException = _stuck.Count == 0 ? null : _stuck.Values.Last();
            SettleRow(peerNodeId);
        }
    }

    /// <summary>The whitelist now holds only <paramref name="activePeers"/>: a peer that is gone cannot be what this node is stuck on.</summary>
    public void RetainOnly(IReadOnlySet<Guid> activePeers)
    {
        lock (_gate)
        {
            var gone = _stuck.Keys.Where(k => k != Guid.Empty && !activePeers.Contains(k)).ToList();
            foreach (var peer in gone) _stuck.Remove(peer);
            if (gone.Count > 0) _lastException = _stuck.Count == 0 ? null : _stuck.Values.Last();
            if (db == null) return;
            LoadRow();
            if (_rowExists && _rowPeer is { } rowPeer && !activePeers.Contains(rowPeer))
                SettleRow(rowPeer);
        }
    }

    // Brings the row in line with memory after the state for `cleared` ended (null: everything ended).
    private void SettleRow(Guid? cleared)
    {
        if (db == null) return;
        LoadRow();
        if (!_rowExists) return;
        if (cleared is not null && _rowPeer is { } rowPeer && rowPeer != cleared) return; // another peer's row
        if (_stuck.Count == 0)
        {
            TryWrite(conn => conn.Execute("DELETE FROM tbl_migration_marker WHERE key = @Key", new { Key }));
            _rowExists = false;
            _rowPeer = null;
        }
        else
        {
            var (peer, ex) = _stuck.Last();
            Persist(ex, peer == Guid.Empty ? null : peer);
            _rowPeer = peer == Guid.Empty ? null : peer;
        }
    }

    private void LoadRow()
    {
        if (_rowLoaded) return;
        _rowLoaded = true;
        try
        {
            var row = Read(db!);
            _rowExists = row != null;
            _rowPeer = row?.PeerNodeId;
        }
        catch (Exception)
        {
            _rowExists = true; // unknown: a delete is harmless, a missed one would keep a stale report
        }
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

    private void Persist(SnapshotRequiredException ex, Guid? peerNodeId)
    {
        var value = new Persisted(ex.RemoteUrl, ex.LastCompactionCp, ex.CurrentHeadSeq, DateTime.UtcNow, peerNodeId);
        TryWrite(conn => conn.Execute(
            "INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@Key, @Value, @Now)",
            new { Key, Value = JsonSerializer.Serialize(value), Now = DateTime.UtcNow.ToString("O") }));
    }

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

    public sealed record Persisted(string RemoteUrl, long LastCompactionCp, long CurrentHeadSeq, DateTime SinceUtc, Guid? PeerNodeId = null);
}
