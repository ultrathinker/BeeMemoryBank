using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The six dream_* tables of chat.db (plan 3.2), created by ChatDbInitializer beside the chat tables: chat.db is local
/// to the node, never in a snapshot or a sync event, sealed page by page on a sealed node, and its key survives a DEK
/// rotation. The vault database and its schema number are not touched; blind nodes never see these tables.
///
/// <para>Every table has a fate in RekeyTables.Chat and every sealed column a ChatColumns.All entry (the offline
/// re-key walks them with <c>SELECT id, ...</c>, so a sealed table's key is <c>id TEXT</c>, a "D" GUID, and its AAD
/// is built from that id: DreamAad). Sealed columns hold JSON sealed with ChatKeyLease.EncryptText under the chat key
/// (key_v = ChatDataProtector.ChatKeyVersion). Plaintext tables hold only numbers, enumerations and ids.</para>
///
/// <para>Times are ISO-8601 UTC text ("o"), as in the chat tables. <c>seq</c> is MAX(seq)+1 inside the insert: the
/// active run is the only writer. Idempotent: CREATE ... IF NOT EXISTS. A column added later goes into
/// <see cref="EnsureAsync"/> as a guarded ALTER, the way ChatDbInitializer adds chat columns.</para>
/// </summary>
public static class DreamSchema
{
    /// <summary>Every dream_* table, in the order the reset clears them.</summary>
    public static readonly IReadOnlyList<string> Tables =
        ["dream_advice", "dream_run_event", "dream_examined", "dream_rule", "dream_run", "dream_settings"];

    /// <summary>The tables a node reset empties (dream_settings keeps its row; the schedule is turned off).</summary>
    public static readonly IReadOnlyList<string> ResetTables =
        ["dream_advice", "dream_run_event", "dream_examined", "dream_rule", "dream_run"];

    public const string ResetSettingsSql = "UPDATE dream_settings SET schedule_enabled = 0";

    public static async Task EnsureAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        foreach (var ddl in Statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Columns added after the first build: a chat.db made before them gets them here (CREATE TABLE IF NOT EXISTS leaves an
        // existing table as it is). Runs from before use_rules ran the rules, hence its default.
        await EnsureColumnAsync(conn, "dream_run", "use_rules", "ALTER TABLE dream_run ADD COLUMN use_rules INTEGER NOT NULL DEFAULT 1", ct);
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string alterSql, CancellationToken ct)
    {
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
            check.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0) return;
        }
        await using var alter = conn.CreateCommand();
        alter.CommandText = alterSql;
        await alter.ExecuteNonQueryAsync(ct);
    }

    public static readonly IReadOnlyList<string> Statements =
    [
        // One row (id = 1), Plaintext. The defaults are DreamSettings'.
        """
        CREATE TABLE IF NOT EXISTS dream_settings (
            id                       INTEGER PRIMARY KEY CHECK (id = 1),
            schedule_enabled         INTEGER NOT NULL DEFAULT 0,
            schedule_start           TEXT NOT NULL DEFAULT '03:00',
            schedule_window_minutes  INTEGER NOT NULL DEFAULT 120,
            max_advice_scheduled     INTEGER NOT NULL DEFAULT 10,
            max_advice_manual        INTEGER NOT NULL DEFAULT 50,
            max_open_advice          INTEGER NOT NULL DEFAULT 100,
            min_score                INTEGER NOT NULL DEFAULT 40,
            manual_max_minutes       INTEGER NOT NULL DEFAULT 30,
            max_model_calls          INTEGER NOT NULL DEFAULT 200,
            max_tokens               INTEGER NOT NULL DEFAULT 1500000,
            use_model                INTEGER NOT NULL DEFAULT 0,
            analysis_model_id        TEXT,
            excluded_folder_ids      TEXT NOT NULL DEFAULT '[]',
            updated_at               TEXT,
            updated_by               INTEGER
        );
        """,
        "INSERT OR IGNORE INTO dream_settings (id) VALUES (1);",
        // Plaintext: numbers, enumerations (DreamWire names) and ids only. error_text never carries a title or article text.
        """
        CREATE TABLE IF NOT EXISTS dream_run (
            id                   TEXT PRIMARY KEY,
            trigger              TEXT NOT NULL,
            mode                 TEXT NOT NULL,
            state                TEXT NOT NULL,
            outcome              TEXT,
            phase                TEXT,
            started_by_user_id   INTEGER,
            created_at           TEXT NOT NULL,
            started_at           TEXT,
            finished_at          TEXT,
            heartbeat_at         TEXT,
            use_model            INTEGER NOT NULL DEFAULT 0,
            use_rules            INTEGER NOT NULL DEFAULT 1,
            model_id             TEXT,
            model_label          TEXT,
            prompt_versions      TEXT,
            max_advice           INTEGER NOT NULL DEFAULT 0,
            max_minutes          INTEGER NOT NULL DEFAULT 0,
            max_model_calls      INTEGER NOT NULL DEFAULT 0,
            max_tokens           INTEGER NOT NULL DEFAULT 0,
            articles_in_scope    INTEGER NOT NULL DEFAULT 0,
            articles_sent        INTEGER NOT NULL DEFAULT 0,
            candidates_examined  INTEGER NOT NULL DEFAULT 0,
            advice_created       INTEGER NOT NULL DEFAULT 0,
            advice_suppressed    INTEGER NOT NULL DEFAULT 0,
            model_calls          INTEGER NOT NULL DEFAULT 0,
            prompt_tokens        INTEGER NOT NULL DEFAULT 0,
            completion_tokens    INTEGER NOT NULL DEFAULT 0,
            errors               INTEGER NOT NULL DEFAULT 0,
            error_code           TEXT,
            error_text           TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_dream_run_created ON dream_run(created_at DESC);",
        // Resealed: titles, paths, tag names, explanations and the fingerprint are only in the payload
        // (DreamAdvicePayload, AAD bmb-dream-advice-v1:{id}).
        """
        CREATE TABLE IF NOT EXISTS dream_advice (
            id                  TEXT PRIMARY KEY,
            run_id              TEXT NOT NULL,
            seq                 INTEGER NOT NULL UNIQUE,
            kind                TEXT NOT NULL,
            source              TEXT NOT NULL,
            apply_mode          TEXT NOT NULL,
            score               INTEGER NOT NULL,
            status              TEXT NOT NULL DEFAULT 'new',
            status_at           TEXT,
            status_by_user_id   INTEGER,
            status_note         TEXT,
            trial               INTEGER NOT NULL DEFAULT 0,
            created_at          TEXT NOT NULL,
            payload_ciphertext  BLOB NOT NULL,
            payload_iv          BLOB NOT NULL,
            payload_key_v       INTEGER
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_dream_advice_run ON dream_advice(run_id, seq);",
        "CREATE INDEX IF NOT EXISTS idx_dream_advice_open ON dream_advice(status, trial, score DESC);",
        // Resealed: the fingerprint, object id, folder path or tag name and the reason are in the payload
        // (DreamRulePayload, AAD bmb-dream-rule-v1:{id}).
        """
        CREATE TABLE IF NOT EXISTS dream_rule (
            id                  TEXT PRIMARY KEY,
            scope               TEXT NOT NULL,
            kind                TEXT,
            created_at          TEXT NOT NULL,
            created_by_user_id  INTEGER,
            expires_at          TEXT,
            payload_ciphertext  BLOB NOT NULL,
            payload_iv          BLOB NOT NULL,
            payload_key_v       INTEGER
        );
        """,
        // Resealed, kept seven days: { text, articleIds[], data{} } (DreamEventPayload, AAD bmb-dream-event-v1:{id}).
        """
        CREATE TABLE IF NOT EXISTS dream_run_event (
            id                  TEXT PRIMARY KEY,
            seq                 INTEGER NOT NULL UNIQUE,
            run_id              TEXT NOT NULL,
            at                  TEXT NOT NULL,
            kind                TEXT NOT NULL,
            payload_ciphertext  BLOB NOT NULL,
            payload_iv          BLOB NOT NULL,
            payload_key_v       INTEGER
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_dream_run_event_run ON dream_run_event(run_id, seq);",
        "CREATE INDEX IF NOT EXISTS idx_dream_run_event_at ON dream_run_event(at);",
        // Plaintext: "already examined, do not pay twice". The key is a hash of the task and article/folder ids
        // (DreamFingerprint.CandidateKey); nothing here is secret.
        """
        CREATE TABLE IF NOT EXISTS dream_examined (
            candidate_key   TEXT PRIMARY KEY,
            task            TEXT NOT NULL,
            content_marker  TEXT NOT NULL,
            model_id        TEXT,
            prompt_version  TEXT NOT NULL,
            verdict         TEXT NOT NULL,
            examined_at     TEXT NOT NULL,
            run_id          TEXT
        );
        """,
    ];
}
