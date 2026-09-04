using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// Copies tables from a snapshot database ATTACHed as <c>snap</c> into the live schema. Shared by
/// the three restore paths (SnapshotService.RestoreForJoinAsync, ApplyNetworkRestoreAsync, and
/// the mobile SnapshotJoinClient) so they cannot drift on the one rule that matters here:
///
/// <b>copy by column NAME, never by position.</b> <c>INSERT INTO t SELECT * FROM snap.t</c> maps
/// columns positionally, which is only correct while both databases have the exact same column
/// order. Migration 017 dropped <c>ciphertext</c> from tbl_article_body and tbl_article_version,
/// so a snapshot written at 016 has one more column (loud failure: "table has 5 columns but 6
/// values were supplied") and a snapshot written before 016 has the SAME count with different
/// meaning — ciphertext would land in <c>iv</c>, iv in <c>encrypted_dek</c>, and every article
/// would be silently destroyed. Name-matching on the intersection of both tables' columns makes
/// either snapshot import correctly; <see cref="AdoptLegacyInlineCiphertext"/> then folds a
/// pre-016 snapshot's inline bodies into tbl_blob so they stay readable.
/// </summary>
public static class SnapshotTableImport
{
    /// <summary>Whether <c>snap</c> has a table of this name.</summary>
    public static bool SnapshotHasTable(SqliteConnection conn, SqliteTransaction? tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM snap.sqlite_master WHERE type = 'table' AND name = @t";
        cmd.Parameters.AddWithValue("t", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// <c>INSERT [OR IGNORE] INTO [table] (cols) SELECT cols FROM snap.[table]</c> over the columns
    /// the two schemas share. Columns only the live table has are left to their defaults (NULL);
    /// columns only the snapshot has are dropped. Returns the shared column list so callers can
    /// react to what was actually copied.
    /// </summary>
    public static IReadOnlyList<string> CopyTable(SqliteConnection conn, SqliteTransaction? tx, string table, bool orIgnore)
    {
        var live = ColumnsOf(conn, tx, "main", table);
        var snap = ColumnsOf(conn, tx, "snap", table);
        var shared = live.Where(snap.Contains).ToList();
        if (shared.Count == 0)
            throw new InvalidOperationException($"Snapshot table [{table}] shares no columns with the local schema.");

        var cols = string.Join(", ", shared.Select(c => $"[{c}]"));
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT {(orIgnore ? "OR IGNORE " : "")}INTO [{table}] ({cols}) SELECT {cols} FROM snap.[{table}]";
        cmd.ExecuteNonQuery();
        return shared;
    }

    /// <summary>
    /// For a snapshot written before migration 016, whose tbl_article_body / tbl_article_version
    /// still carry the ciphertext inline: store those bytes in tbl_blob and point the freshly
    /// imported rows at them. Without this, such rows import with a NULL hash and every one of
    /// those articles opens as "blob missing". Requires the connection's <c>sha256()</c>
    /// function (DbConnectionFactory registers it). No-op for snapshots at 016 or later.
    /// </summary>
    public static void AdoptLegacyInlineCiphertext(SqliteConnection conn, SqliteTransaction? tx)
    {
        var now = DateTime.UtcNow.ToString("o");
        foreach (var (table, key) in new[] { ("tbl_article_body", "article_id"), ("tbl_article_version", "id") })
        {
            if (!SnapshotHasTable(conn, tx, table)) continue;
            if (!ColumnsOf(conn, tx, "snap", table).Contains("ciphertext")) continue;

            using var blobs = conn.CreateCommand();
            blobs.Transaction = tx;
            blobs.CommandText =
                $@"INSERT OR IGNORE INTO tbl_blob (hash, data, size, created_at)
                   SELECT sha256(s.ciphertext), s.ciphertext, length(s.ciphertext), @now
                   FROM snap.[{table}] s WHERE s.ciphertext IS NOT NULL";
            blobs.Parameters.AddWithValue("now", now);
            blobs.ExecuteNonQuery();

            using var link = conn.CreateCommand();
            link.Transaction = tx;
            link.CommandText =
                $@"UPDATE [{table}] SET ciphertext_hash =
                     (SELECT sha256(s.ciphertext) FROM snap.[{table}] s WHERE s.[{key}] = [{table}].[{key}])
                   WHERE ciphertext_hash IS NULL
                     AND EXISTS (SELECT 1 FROM snap.[{table}] s WHERE s.[{key}] = [{table}].[{key}] AND s.ciphertext IS NOT NULL)";
            link.ExecuteNonQuery();
        }
    }

    private static HashSet<string> ColumnsOf(SqliteConnection conn, SqliteTransaction? tx, string schema, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Table-valued form of PRAGMA table_info, which takes the schema as its second argument;
        // the statement form (PRAGMA snap.table_info(...)) cannot be parameterized at all.
        cmd.CommandText = "SELECT name FROM pragma_table_info(@t, @s)";
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("s", schema);
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(0));
        return cols;
    }
}
