using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Creates the chat.db schema once at startup. Idempotent (<c>CREATE TABLE IF NOT EXISTS</c>).
///
/// This is deliberately NOT <c>MigrationRunner</c>, does NOT live under
/// <c>Storage/Migrations/*.sql</c> (that folder is glob-embedded into the Storage assembly and
/// Ghost-Hunter-managed), and is NOT registered via <c>AddStorage()</c>. It is invoked from a
/// dedicated <c>using</c> scope block in <c>Api/Program.cs</c> placed AFTER the beedb migration
/// blocks. See plan §1 ("Chat DB") + §3 (schema) + §4 (guardrails).
/// </summary>
public sealed class ChatDbInitializer
{
    private readonly ChatDbConnectionFactory _factory;
    private readonly ILogger<ChatDbInitializer> _logger;

    public ChatDbInitializer(ChatDbConnectionFactory factory, ILogger<ChatDbInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await using var conn = (SqliteConnection)_factory.CreateConnection();

        foreach (var ddl in SchemaStatements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync();
        }

        // Additive column migrations for existing tables. The CREATE TABLE IF NOT EXISTS
        // statements above are a no-op on an existing DB, so these guarded ALTERs bring an
        // older chat.db up to the current schema. Each is skipped if the column already exists
        // (checked via PRAGMA table_info) so re-running is safe.
        await EnsureColumnAsync(conn, "chat_model", "is_text",
            "ALTER TABLE chat_model ADD COLUMN is_text INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, "chat_model", "is_vision",
            "ALTER TABLE chat_model ADD COLUMN is_vision INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, "chat_model", "is_image_gen",
            "ALTER TABLE chat_model ADD COLUMN is_image_gen INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, "chat_model", "context_window",
            "ALTER TABLE chat_model ADD COLUMN context_window INTEGER");
        await EnsureColumnAsync(conn, "chat_model", "created_at",
            "ALTER TABLE chat_model ADD COLUMN created_at TEXT");
        await EnsureColumnAsync(conn, "chat_settings", "default_text_model_id",
            "ALTER TABLE chat_settings ADD COLUMN default_text_model_id TEXT");
        await EnsureColumnAsync(conn, "chat_settings", "default_vision_model_id",
            "ALTER TABLE chat_settings ADD COLUMN default_vision_model_id TEXT");
        await EnsureColumnAsync(conn, "chat_settings", "default_image_gen_model_id",
            "ALTER TABLE chat_settings ADD COLUMN default_image_gen_model_id TEXT");
        await EnsureColumnAsync(conn, "chat_settings", "chat_globally_enabled",
            "ALTER TABLE chat_settings ADD COLUMN chat_globally_enabled INTEGER NOT NULL DEFAULT 1");
        // Homepage pinned chat: at most ONE conversation per user carries this flag (enforced
        // at the application layer by ChatConversationRepository.SetHomePinnedAsync's single
        // atomic UPDATE — chat.db has no index-based invariants, matching chat_settings).
        await EnsureColumnAsync(conn, "chat_conversation", "is_home_pinned",
            "ALTER TABLE chat_conversation ADD COLUMN is_home_pinned INTEGER NOT NULL DEFAULT 0");
        // Per-turn metrics (set only on the final assistant message of a turn).
        await EnsureColumnAsync(conn, "chat_message", "tool_calls_count",
            "ALTER TABLE chat_message ADD COLUMN tool_calls_count INTEGER");
        await EnsureColumnAsync(conn, "chat_message", "duration_ms",
            "ALTER TABLE chat_message ADD COLUMN duration_ms INTEGER");
        // H3 fix: content_text used to be stored (and read/written) as plaintext, even though it
        // routinely carries decrypted vault content (a tool result JSON is a full article body —
        // see ChatToolLoop.SafePersistToolMessage). New rows are now encrypted under the master
        // DEK (AES-256-GCM, see ChatMessageRepository) into these two columns instead, and
        // content_text is left NULL going forward. Existing rows are NOT retroactively
        // re-encrypted (there is no reliable point in startup to do that — the vault may still be
        // locked here); ChatMessageRepository reads content_ciphertext when present and falls back
        // to the legacy plaintext content_text otherwise, exactly like this codebase's other
        // lazy v0→v1 migrations (e.g. node-identity private key, agent KDF version).
        await EnsureColumnAsync(conn, "chat_message", "content_ciphertext",
            "ALTER TABLE chat_message ADD COLUMN content_ciphertext BLOB");
        await EnsureColumnAsync(conn, "chat_message", "content_iv",
            "ALTER TABLE chat_message ADD COLUMN content_iv BLOB");
        // H3b fix: tool_calls_json was left out of the original H3 fix even though it carries the
        // same class of decrypted vault content (a WRITE tool's arguments ARE the article body
        // being saved) — see ChatMessageRepository's class remarks. Same shape as content_text's
        // pair above: new rows encrypt into these two columns and leave tool_calls_json NULL;
        // ChatMessageRepository reads tool_calls_ciphertext when present and falls back to legacy
        // plaintext tool_calls_json otherwise.
        await EnsureColumnAsync(conn, "chat_message", "tool_calls_ciphertext",
            "ALTER TABLE chat_message ADD COLUMN tool_calls_ciphertext BLOB");
        await EnsureColumnAsync(conn, "chat_message", "tool_calls_iv",
            "ALTER TABLE chat_message ADD COLUMN tool_calls_iv BLOB");
        // H3 fix: chat_attachment.blob used to hold raw image bytes unencrypted. New rows encrypt
        // the blob under the master DEK and record the IV here; NULL iv (legacy rows) means the
        // blob column still holds plaintext bytes, read as-is for backward compatibility.
        await EnsureColumnAsync(conn, "chat_attachment", "iv",
            "ALTER TABLE chat_attachment ADD COLUMN iv BLOB");

        // H3a fix: partial indexes backing ChatMessageRepository/ChatAttachmentRepository's
        // BackfillLegacyPlaintextBatchAsync scans. Each index only contains rows still needing
        // migration (its WHERE clause mirrors the "still plaintext" side of the backfill query),
        // so it self-shrinks to empty as rows get migrated and stays empty forever after — a node
        // that has never had a plaintext row (or has finished backfilling) pays an empty-index
        // lookup per scan, never a full table scan, regardless of how large chat.db grows.
        // CREATE INDEX IF NOT EXISTS is unconditionally idempotent (unlike ALTER TABLE ADD COLUMN),
        // so these run every startup with no existence check needed.
        foreach (var indexDdl in new[]
        {
            "CREATE INDEX IF NOT EXISTS idx_chat_message_legacy_content ON chat_message(id) WHERE content_ciphertext IS NULL AND content_text IS NOT NULL AND content_text != ''",
            "CREATE INDEX IF NOT EXISTS idx_chat_message_legacy_toolcalls ON chat_message(id) WHERE tool_calls_ciphertext IS NULL AND tool_calls_json IS NOT NULL AND tool_calls_json != ''",
            "CREATE INDEX IF NOT EXISTS idx_chat_attachment_legacy_blob ON chat_attachment(id) WHERE iv IS NULL AND blob IS NOT NULL"
        })
        {
            await using var indexCmd = conn.CreateCommand();
            indexCmd.CommandText = indexDdl;
            await indexCmd.ExecuteNonQueryAsync();
        }

        _logger.LogInformation("chat.db schema initialized");
    }

    /// <summary>Adds a column to a table only if it doesn't already exist. Idempotent — uses
    /// PRAGMA table_info to check before issuing the ALTER, so re-running on an already-migrated
    /// DB is a no-op (never throws "duplicate column name").</summary>
    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string alterSql)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await checkCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader["name"] is string name && !string.IsNullOrEmpty(name))
                    existing.Add(name);
            }
        }

        if (existing.Contains(column))
            return;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = alterSql;
        await alterCmd.ExecuteNonQueryAsync();
    }

    // Schema per plan §3. chat_api_key stores only (ciphertext, iv) — the
    // ArticleEncryptor.Encrypt(secret, masterDek, aad) path yields exactly those two artifacts
    // (AES-256-GCM under the master DEK with a constant AAD), so there is no salt/kdf_version.
    private static readonly string[] SchemaStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS chat_conversation (
            id              TEXT PRIMARY KEY,
            user_id         INTEGER NOT NULL,
            title           TEXT NOT NULL,
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL,
            is_home_pinned  INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_message (
            id                    TEXT PRIMARY KEY,
            conversation_id       TEXT NOT NULL,
            role                  TEXT NOT NULL,
            -- content_text/tool_calls_json are legacy plaintext, kept for backward-compat reads of
            -- rows written before the H3/H3b encryption fixes. New rows leave them NULL and
            -- populate the ciphertext/iv column pairs below instead (AES-256-GCM under the master
            -- DEK, one independent AAD-bound pair per column) — see ChatMessageRepository. A fresh
            -- database created after both fixes shipped gets these columns here directly and never
            -- needs the additive ALTER path below.
            content_text          TEXT,
            content_ciphertext    BLOB,
            content_iv            BLOB,
            tool_calls_json       TEXT,
            tool_calls_ciphertext BLOB,
            tool_calls_iv         BLOB,
            tool_call_id          TEXT,
            model                 TEXT,
            tokens_in             INTEGER,
            tokens_out            INTEGER,
            tool_calls_count      INTEGER,
            duration_ms           INTEGER,
            created_at            TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_attachment (
            id          TEXT PRIMARY KEY,
            message_id  TEXT NOT NULL,
            kind        TEXT NOT NULL,
            mime        TEXT NOT NULL,
            -- blob holds ciphertext (AES-256-GCM under the master DEK) when iv is set; a NULL iv
            -- means a legacy row written before the H3 encryption fix, whose blob is still
            -- plaintext bytes — see ChatAttachmentRepository.
            blob        BLOB,
            iv          BLOB,
            created_at  TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_api_key (
            id              TEXT PRIMARY KEY,
            label           TEXT NOT NULL,
            key_prefix      TEXT NOT NULL,
            ciphertext      BLOB NOT NULL,
            iv              BLOB NOT NULL,
            enabled         INTEGER NOT NULL DEFAULT 1,
            priority        INTEGER NOT NULL DEFAULT 0,
            disabled_until  TEXT,
            last_error      TEXT,
            last_used_at    TEXT,
            created_at      TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_model (
            id                    TEXT PRIMARY KEY,
            model_id              TEXT NOT NULL,
            label                 TEXT NOT NULL,
            category              TEXT NOT NULL,
            default_for_category  INTEGER NOT NULL DEFAULT 0,
            enabled               INTEGER NOT NULL DEFAULT 1,
            is_text               INTEGER NOT NULL DEFAULT 0,
            is_vision             INTEGER NOT NULL DEFAULT 0,
            is_image_gen          INTEGER NOT NULL DEFAULT 0,
            context_window        INTEGER,
            created_at            TEXT
        );
        """,
        // Single-row settings table (id is always 1). Holds the three pinned default-model ids
        // (nullable GUIDs referencing chat_model.id; null = "use the oldest model with the
        // matching property") and the node-wide chat_globally_enabled kill switch. Node-local like
        // the rest of chat.db; never synced. The old category/enabled/default_for_category columns
        // on chat_model are kept for backward compatibility but are no longer read or written by
        // application code — same for auto_approve_writes here (see chat_user_settings below):
        // M1 fix, a single node-global auto-approve toggle removed the human confirm gate for
        // EVERY user at once (and required superadmin to touch it on everyone's behalf); the
        // column is kept only so an upgrade from an older chat.db doesn't need a destructive
        // migration, never read or written by current code.
        """
        CREATE TABLE IF NOT EXISTS chat_settings (
            id                         INTEGER PRIMARY KEY CHECK (id = 1),
            auto_approve_writes        INTEGER NOT NULL DEFAULT 0,
            chat_globally_enabled      INTEGER NOT NULL DEFAULT 1,
            default_text_model_id      TEXT,
            default_vision_model_id    TEXT,
            default_image_gen_model_id TEXT
        );
        """,
        // M1 fix: auto-approve-writes is now per-user, not a single node-global toggle — each user
        // controls only their OWN confirm-gate bypass for their OWN chat writes (still fully ACL
        // + destructive-cap + audit-tag gated regardless; this only skips the human Allow/Deny
        // click). No row for a user means "off" (see ChatSettingsRepository.GetAutoApproveWritesAsync's
        // COALESCE), so a brand-new user needs no seed row here.
        """
        CREATE TABLE IF NOT EXISTS chat_user_settings (
            user_id             INTEGER PRIMARY KEY,
            auto_approve_writes INTEGER NOT NULL DEFAULT 0
        );
        """,
        // chat_globally_enabled is intentionally NOT seeded here: on an existing chat.db this
        // statement runs BEFORE EnsureColumnAsync below adds the column (CREATE TABLE IF NOT
        // EXISTS above is a no-op for a pre-existing table), so referencing it here would throw
        // "no such column" on upgrade. The column's own DEFAULT 1 (both in the CREATE TABLE for
        // fresh DBs and in the ALTER for existing ones) already gives every row the correct value.
        "INSERT OR IGNORE INTO chat_settings (id, auto_approve_writes) VALUES (1, 0);"
    ];
}
