using System.Data;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>
/// What the initiator of a DEK rotation still has to finish once the DEK transaction committed: the completion record (audit line,
/// the <c>Completed</c> step, the post-rotation compaction), and, on an activated network, only after the new metadata key is
/// published (docs/design/step-a-spec.md 4.4). One row of <c>tbl_migration_marker</c>, key <see cref="Key"/>, the record as JSON.
/// <see cref="DekRewrapper"/> writes it INSIDE the DEK transaction, so there is no committed rotation without it; the finish deletes
/// it as its last step. A crash, a restart or a failed finish therefore leaves the work where the next unlock, the service's timer
/// or the publication finds it again, instead of in the memory of a process that is gone.
///
/// <para>Local bookkeeping like the other markers of that table: a snapshot carries the table but never imports it over the local
/// rows (see <c>SnapshotTables.SchemaMeta</c>).</para>
/// </summary>
public sealed record DekRotationCompletionRecord(
    string CommitEventId, int NewEpoch, int InitiatorId, string InitiatorName, string SnapshotFileName, int AgentsDeleted)
{
    public const string Key = "dek_rotation_completion_pending";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>The record that is waiting to be finished, or null.</summary>
    public static DekRotationCompletionRecord? Read(IDbConnection conn, IDbTransaction? tx = null)
    {
        var json = conn.ExecuteScalar<string?>("SELECT value FROM tbl_migration_marker WHERE key = @Key", new { Key }, tx);
        return json is null ? null : JsonSerializer.Deserialize<DekRotationCompletionRecord>(json, JsonOpts);
    }

    /// <summary>Reads the record on a connection of its own.</summary>
    public static DekRotationCompletionRecord? Read(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        return Read(conn);
    }

    /// <summary>
    /// Writes the record inside the caller's transaction. One that is already there is never overwritten: it is the unfinished work of an
    /// earlier rotation, so the caller (a second rotation started too early) fails and its transaction rolls back.
    /// </summary>
    public static void Write(IDbConnection conn, IDbTransaction tx, DekRotationCompletionRecord record)
    {
        if (Read(conn, tx) is { } pending)
            throw new InvalidOperationException(
                $"The key rotation {pending.CommitEventId} is not finished yet; another key rotation must wait for it.");
        conn.Execute(
            "INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@Key, @Json, @Now)",
            new { Key, Json = JsonSerializer.Serialize(record, JsonOpts), Now = DateTime.UtcNow.ToString("O") }, tx);
    }

    /// <summary>Deletes the record; returns whether this call removed it.</summary>
    public static bool Clear(IDbConnection conn, IDbTransaction? tx = null) =>
        conn.Execute("DELETE FROM tbl_migration_marker WHERE key = @Key", new { Key }, tx) > 0;
}
