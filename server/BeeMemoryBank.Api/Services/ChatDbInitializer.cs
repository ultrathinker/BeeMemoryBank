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
            id              TEXT PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            role            TEXT NOT NULL,
            content_text    TEXT,
            tool_calls_json TEXT,
            tool_call_id    TEXT,
            model           TEXT,
            tokens_in       INTEGER,
            tokens_out      INTEGER,
            created_at      TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_attachment (
            id          TEXT PRIMARY KEY,
            message_id  TEXT NOT NULL,
            kind        TEXT NOT NULL,
            mime        TEXT NOT NULL,
            blob        BLOB,
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
            created_at            TEXT
        );
        """,
        // Single-row settings table (id is always 1). Holds auto_approve_writes (superadmin-only
        // opt-in that skips the human-in-the-loop confirm gate for write tools) and the three
        // pinned default-model ids (nullable GUIDs referencing chat_model.id; null = "use the
        // oldest model with the matching property"). Node-local like the rest of chat.db; never
        // synced. The old category/enabled/default_for_category columns on chat_model are kept
        // for backward compatibility but are no longer read or written by application code.
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
        // chat_globally_enabled is intentionally NOT seeded here: on an existing chat.db this
        // statement runs BEFORE EnsureColumnAsync below adds the column (CREATE TABLE IF NOT
        // EXISTS above is a no-op for a pre-existing table), so referencing it here would throw
        // "no such column" on upgrade. The column's own DEFAULT 1 (both in the CREATE TABLE for
        // fresh DBs and in the ALTER for existing ones) already gives every row the correct value.
        "INSERT OR IGNORE INTO chat_settings (id, auto_approve_writes) VALUES (1, 0);"
    ];
}
