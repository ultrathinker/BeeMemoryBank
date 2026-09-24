using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// CRUD for <c>chat_message</c> (chat.db). Node-local — never synced, never snapshotted.
/// </summary>
/// <remarks>
/// H3 fix: <c>content_text</c> routinely carries decrypted vault content — a <c>role="tool"</c>
/// row's content is a tool RESULT, and for read tools that's a full decrypted article body (see
/// <c>ChatEndpoints.ToolLoop.SafePersistToolMessage</c>). This repository is the single choke
/// point every chat_message write and read goes through, so encryption lives here rather than at
/// each call site: <see cref="CreateAsync"/> encrypts <see cref="Models.ChatMessage.ContentText"/>
/// (AES-256-GCM) before the row is ever written, and <see cref="ListByConversationAsync"/> decrypts
/// it back before returning — callers on both sides only ever see plaintext
/// <see cref="Models.ChatMessage.ContentText"/>, never ciphertext. Both require an unlocked session;
/// callers must check <see cref="SessionService.IsUnlocked"/> first, exactly like every other
/// content-touching path.
///
/// The key is the node chat key (<see cref="ChatDataProtector"/>), not the master DEK: chat.db is a
/// separate file a DEK rotation cannot re-encrypt, and rows sealed straight under the master DEK
/// became unreadable after every rotation. <c>content_key_v</c> / <c>tool_calls_key_v</c> record
/// which key a column is under; legacy master-DEK rows keep opening through the current or a retired
/// master DEK until <see cref="MigrateLegacyBatchAsync"/> moves them.
///
/// H3b fix: <c>tool_calls_json</c> was left out of the original H3 fix, but it carries the SAME
/// class of decrypted vault content whenever the assistant calls a WRITE tool (bee_save_article,
/// bee_update_article, bee_append_to_article, bee_replace_in_article) — the tool arguments ARE the
/// article body/patch being written. It is encrypted exactly like content_text, under its OWN
/// AAD (<see cref="ToolCallsAad"/>) so a ciphertext cannot be moved between the two columns. Pure
/// read-tool calls (no vault content in the arguments) are encrypted too — classifying "does this
/// call carry vault content" per-tool would be one more place to get wrong, and uniform encryption
/// costs nothing and cannot leak by misclassification.
///
/// H3a fix: rows written before either of the above shipped — including rows written BETWEEN the
/// two (content_text encrypted, tool_calls_json still plaintext) — are migrated by
/// <see cref="MigrateLegacyBatchAsync"/>, driven by <c>ChatHistoryBackfillProcessor</c>.
/// </remarks>
public sealed class ChatMessageRepository(ChatDbConnectionFactory factory, ChatDataProtector protector)
    : ChatRepositoryBase(factory)
{
    // Distinct from every other AAD tag in the codebase (chat_api_key, the MCP continuation
    // store, RemoteAccountService tokens, ...). Unchanged by the move to the chat key: the key-version
    // column, not the AAD, says which key a ciphertext is under.
    private static readonly byte[] ContentAad = "bmb-chat-message-content-v1"u8.ToArray();

    // H3b fix: distinct from ContentAad (and every other AAD tag) so a ciphertext captured from
    // one column can never be replayed into the other, even though both live on the same row and
    // share the same key.
    private static readonly byte[] ToolCallsAad = "bmb-chat-message-toolcalls-v1"u8.ToArray();

    private const string ContentDecryptFailurePlaceholder =
        "[unable to decrypt — this message was sealed under a key this node no longer has]";

    // H3b fix: a tool_calls_json row that fails to decrypt must degrade to something every caller
    // can still deserialize. Unlike ContentText's free-text placeholder, this one MUST stay valid
    // JSON matching the ChatToolCall[] shape: ChatEndpoints.Stream/.Confirm both do
    // JsonSerializer.Deserialize<List<ChatToolCall>>(row.ToolCallsJson, ...) with no per-row
    // try/catch, so an invalid-JSON placeholder would throw and abort loading the WHOLE transcript
    // (all rows, not just this one) instead of just this one row degrading gracefully. The
    // synthetic id deliberately can't collide with a real OpenRouter-issued tool_call id, so it is
    // never mistaken for a still-pending confirmable call.
    private const string ToolCallsDecryptFailurePlaceholder =
        """[{"id":"undecryptable","type":"function","function":{"name":"[unable to decrypt — this message was sealed under a key this node no longer has]","arguments":"{}"}}]""";

    /// <summary>
    /// Rows with at least one column still waiting to move onto the chat key: a legacy master-DEK
    /// ciphertext, or legacy plaintext. Shared verbatim by the partial index in
    /// <see cref="ChatDbInitializer"/> and the scan in <see cref="MigrateLegacyBatchAsync"/> —
    /// SQLite only uses a partial index whose WHERE the query's WHERE implies.
    /// </summary>
    internal const string LegacyKeyPredicate =
        "(content_key_v IS NULL AND (content_ciphertext IS NOT NULL OR (content_text IS NOT NULL AND content_text != ''))) " +
        "OR (tool_calls_key_v IS NULL AND (tool_calls_ciphertext IS NOT NULL OR (tool_calls_json IS NOT NULL AND tool_calls_json != '')))";

    private const string Cols = @"id AS Id, conversation_id AS ConversationId, role AS Role,
        content_text AS ContentText, content_ciphertext AS ContentCiphertext, content_iv AS ContentIv,
        content_key_v AS ContentKeyVersion,
        tool_calls_json AS ToolCallsJson, tool_calls_ciphertext AS ToolCallsCiphertext, tool_calls_iv AS ToolCallsIv,
        tool_calls_key_v AS ToolCallsKeyVersion,
        tool_call_id AS ToolCallId,
        model AS Model, tokens_in AS TokensIn, tokens_out AS TokensOut,
        tool_calls_count AS ToolCallsCount, duration_ms AS DurationMs, created_at AS CreatedAt";

    /// <summary>Loads a conversation's transcript, oldest first, with content_text already
    /// decrypted. <c>ORDER BY created_at, rowid</c> gives a deterministic tiebreak for messages
    /// written in the same millisecond (created_at alone is not unique enough — same-millisecond
    /// writes could otherwise render in an arbitrary/unstable order); rowid reflects true insertion
    /// order for this table (rows are never reordered or reused — conversations are deleted whole,
    /// never row-by-row).</summary>
    public async Task<List<Models.ChatMessage>> ListByConversationAsync(Guid conversationId, SessionService session)
    {
        using var conn = OpenConnection();
        var rows = (await conn.QueryAsync<Models.ChatMessage>(
            $"SELECT {Cols} FROM chat_message WHERE conversation_id = @conversationId ORDER BY created_at ASC, rowid ASC",
            new { conversationId })).ToList();
        await DecryptInPlaceAsync(rows);
        return rows;
    }

    public async Task CreateAsync(Models.ChatMessage message, SessionService session)
    {
        // A pure text turn has nothing to put in tool_calls_json, and a pure tool-call turn
        // (assistant → tool_calls, no text) has nothing to put in content_text — either or both
        // can be empty; ciphertext/iv/key_v for the empty side simply stay null (nothing lost
        // either way). Both are encrypted under ONE lease of the chat key.
        byte[]? contentCiphertext = null, contentIv = null;
        byte[]? toolCallsCiphertext = null, toolCallsIv = null;
        if (message.ContentText is { Length: > 0 } || message.ToolCallsJson is { Length: > 0 })
        {
            using var key = await protector.AcquireAsync();
            if (message.ContentText is { Length: > 0 })
                (contentCiphertext, contentIv) = key.EncryptText(message.ContentText, ContentAad);
            // H3b fix: encrypted uniformly, including pure-read tool calls — see class remarks.
            if (message.ToolCallsJson is { Length: > 0 })
                (toolCallsCiphertext, toolCallsIv) = key.EncryptText(message.ToolCallsJson, ToolCallsAad);
        }

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_message
              (id, conversation_id, role, content_text, content_ciphertext, content_iv, content_key_v,
               tool_calls_json, tool_calls_ciphertext, tool_calls_iv, tool_calls_key_v, tool_call_id, model,
               tokens_in, tokens_out, tool_calls_count, duration_ms, created_at)
              VALUES (@Id, @ConversationId, @Role, NULL, @ContentCiphertext, @ContentIv, @ContentKeyVersion,
                      NULL, @ToolCallsCiphertext, @ToolCallsIv, @ToolCallsKeyVersion, @ToolCallId, @Model,
                      @TokensIn, @TokensOut, @ToolCallsCount, @DurationMs, @CreatedAt)",
            new
            {
                message.Id,
                message.ConversationId,
                message.Role,
                ContentCiphertext = contentCiphertext,
                ContentIv = contentIv,
                ContentKeyVersion = contentCiphertext is null ? (int?)null : ChatDataProtector.ChatKeyVersion,
                ToolCallsCiphertext = toolCallsCiphertext,
                ToolCallsIv = toolCallsIv,
                ToolCallsKeyVersion = toolCallsCiphertext is null ? (int?)null : ChatDataProtector.ChatKeyVersion,
                message.ToolCallId,
                message.Model,
                message.TokensIn,
                message.TokensOut,
                message.ToolCallsCount,
                message.DurationMs,
                message.CreatedAt
            });
    }

    /// <summary>Decrypts <see cref="Models.ChatMessage.ContentCiphertext"/> into
    /// <see cref="Models.ChatMessage.ContentText"/>, and <see cref="Models.ChatMessage.ToolCallsCiphertext"/>
    /// into <see cref="Models.ChatMessage.ToolCallsJson"/>, for every row that has them, in place —
    /// independently per column, since a row can have either side encrypted without the other, and
    /// each side under a different key (chat key or legacy master DEK, per its key-version column).
    /// Rows written before the relevant fix have no ciphertext for that column and already carry
    /// plaintext — left untouched. A column that fails to decrypt degrades to a visible placeholder
    /// instead of throwing and failing the whole transcript load.</summary>
    private async Task DecryptInPlaceAsync(List<Models.ChatMessage> rows)
    {
        if (!rows.Any(r => r.ContentCiphertext is { Length: > 0 } || r.ToolCallsCiphertext is { Length: > 0 }))
            return; // nothing encrypted in this batch — avoid touching any key at all

        var needsChatKey = rows.Any(r =>
            (r.ContentCiphertext is { Length: > 0 } && r.ContentKeyVersion == ChatDataProtector.ChatKeyVersion) ||
            (r.ToolCallsCiphertext is { Length: > 0 } && r.ToolCallsKeyVersion == ChatDataProtector.ChatKeyVersion));
        using var key = needsChatKey ? await protector.TryAcquireForReadAsync() : null;

        foreach (var row in rows)
        {
            if (row.ContentCiphertext is { Length: > 0 })
            {
                row.ContentText = (row.ContentIv is { Length: > 0 }
                    ? protector.TryDecryptText(key, row.ContentCiphertext, row.ContentIv, row.ContentKeyVersion, ContentAad)
                    : null) ?? ContentDecryptFailurePlaceholder;
            }
            row.ContentCiphertext = null;
            row.ContentIv = null;
            row.ContentKeyVersion = null;

            if (row.ToolCallsCiphertext is { Length: > 0 })
            {
                // Must stay valid ChatToolCall[] JSON — see ToolCallsDecryptFailurePlaceholder's doc comment.
                row.ToolCallsJson = (row.ToolCallsIv is { Length: > 0 }
                    ? protector.TryDecryptText(key, row.ToolCallsCiphertext, row.ToolCallsIv, row.ToolCallsKeyVersion, ToolCallsAad)
                    : null) ?? ToolCallsDecryptFailurePlaceholder;
            }
            row.ToolCallsCiphertext = null;
            row.ToolCallsIv = null;
            row.ToolCallsKeyVersion = null;
        }
    }

    /// <summary>
    /// Moves up to <paramref name="batchSize"/> legacy rows onto the chat key and returns how many
    /// rows it looked at (0 = nothing left). A legacy column is either plaintext (written before the
    /// H3/H3b fixes) or ciphertext sealed directly under the master DEK (written before the chat
    /// key); both end up as chat-key ciphertext with <c>*_key_v = 1</c> and the plaintext column
    /// NULL. Each column is migrated independently, mirroring <see cref="DecryptInPlaceAsync"/>.
    ///
    /// <para>A legacy ciphertext that opens under neither the current nor a retired master DEK is
    /// left byte-for-byte as it is and marked <c>-1</c>, so it stops being rescanned — nothing this
    /// node holds will ever open it, but it must not be destroyed either. Every row the scan returns
    /// therefore leaves the scan set, so a drain always terminates.</para>
    ///
    /// <para>Crash-safe and idempotent: each column is moved by its own single UPDATE guarded by
    /// <c>*_key_v IS NULL</c>, so a crash between two columns leaves a consistent row and a
    /// concurrent run (the periodic tick racing the pre-rotation drain) cannot re-encrypt — and
    /// silently discard — an already-migrated column.</para>
    /// </summary>
    public async Task<int> MigrateLegacyBatchAsync(int batchSize, SessionService session, CancellationToken ct)
    {
        using var conn = OpenConnection();
        var legacyRows = (await conn.QueryAsync<LegacyMessageRow>(
            $@"SELECT id AS Id,
                      content_text AS ContentText, content_ciphertext AS ContentCiphertext, content_iv AS ContentIv,
                      content_key_v AS ContentKeyVersion,
                      tool_calls_json AS ToolCallsJson, tool_calls_ciphertext AS ToolCallsCiphertext,
                      tool_calls_iv AS ToolCallsIv, tool_calls_key_v AS ToolCallsKeyVersion
               FROM chat_message
               WHERE {LegacyKeyPredicate}
               LIMIT @batchSize",
            new { batchSize })).ToList();

        if (legacyRows.Count == 0)
            return 0; // steady state: one cheap indexed SELECT, no key touched, no UPDATE issued

        using var key = await protector.AcquireAsync(ct);
        foreach (var row in legacyRows)
        {
            if (ct.IsCancellationRequested) break;

            if (row.ContentKeyVersion is null)
            {
                await MigrateColumnAsync(conn, key, row.Id, row.ContentCiphertext, row.ContentIv, row.ContentText,
                    ContentAad, "content_text", "content_ciphertext", "content_iv", "content_key_v");
            }
            if (row.ToolCallsKeyVersion is null)
            {
                await MigrateColumnAsync(conn, key, row.Id, row.ToolCallsCiphertext, row.ToolCallsIv, row.ToolCallsJson,
                    ToolCallsAad, "tool_calls_json", "tool_calls_ciphertext", "tool_calls_iv", "tool_calls_key_v");
            }
        }

        return legacyRows.Count;
    }

    private async Task MigrateColumnAsync(
        System.Data.IDbConnection conn, ChatKeyLease key, Guid id,
        byte[]? legacyCiphertext, byte[]? legacyIv, string? legacyPlaintext, byte[] aad,
        string plaintextCol, string ciphertextCol, string ivCol, string keyVersionCol)
    {
        string? plaintext;
        if (legacyCiphertext is not null)
        {
            plaintext = legacyIv is { Length: > 0 }
                ? protector.TryDecryptText(null, legacyCiphertext, legacyIv, keyVersion: null, aad)
                : null;
            if (plaintext is null)
            {
                await conn.ExecuteAsync(
                    $"UPDATE chat_message SET {keyVersionCol} = @marker WHERE id = @id AND {keyVersionCol} IS NULL",
                    new { id, marker = ChatDataProtector.LegacyUnreadable });
                return;
            }
        }
        else if (legacyPlaintext is { Length: > 0 })
        {
            plaintext = legacyPlaintext;
        }
        else
        {
            return; // this side has nothing to migrate
        }

        var (ciphertext, iv) = key.EncryptText(plaintext, aad);
        await conn.ExecuteAsync(
            $@"UPDATE chat_message
                  SET {plaintextCol} = NULL, {ciphertextCol} = @ciphertext, {ivCol} = @iv, {keyVersionCol} = @version
                WHERE id = @id AND {keyVersionCol} IS NULL",
            new { id, ciphertext, iv, version = ChatDataProtector.ChatKeyVersion });
    }

    /// <summary>Row shape for <see cref="MigrateLegacyBatchAsync"/>'s scan query.</summary>
    private sealed class LegacyMessageRow
    {
        public Guid Id { get; set; }
        public string? ContentText { get; set; }
        public byte[]? ContentCiphertext { get; set; }
        public byte[]? ContentIv { get; set; }
        public int? ContentKeyVersion { get; set; }
        public string? ToolCallsJson { get; set; }
        public byte[]? ToolCallsCiphertext { get; set; }
        public byte[]? ToolCallsIv { get; set; }
        public int? ToolCallsKeyVersion { get; set; }
    }
}
