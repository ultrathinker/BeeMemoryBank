using System.Security.Cryptography;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
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
/// under the master DEK (AES-256-GCM, same <c>ArticleEncryptor</c> primitive as
/// <c>ChatEndpoints.KeyManagement</c>'s key store) before the row is ever written, and
/// <see cref="ListByConversationAsync"/> decrypts it back before returning — callers on both sides
/// only ever see plaintext <see cref="Models.ChatMessage.ContentText"/>, never ciphertext. Both
/// require an unlocked session (there is no DEK otherwise); callers must check
/// <see cref="SessionService.IsUnlocked"/> first, exactly like every other content-touching path.
///
/// H3b fix: <c>tool_calls_json</c> was left out of the original H3 fix, but it carries the SAME
/// class of decrypted vault content whenever the assistant calls a WRITE tool (bee_save_article,
/// bee_update_article, bee_append_to_article, bee_replace_in_article) — the tool arguments ARE the
/// article body/patch being written. It is now encrypted exactly like content_text, under its OWN
/// AAD (<see cref="ToolCallsAad"/>) so a ciphertext cannot be moved between the two columns. Pure
/// read-tool calls (no vault content in the arguments) are encrypted too — classifying "does this
/// call carry vault content" per-tool would be one more place to get wrong, and uniform encryption
/// costs nothing and cannot leak by misclassification.
///
/// H3a fix: rows written before either of the above shipped — including rows written BETWEEN the
/// two (content_text encrypted, tool_calls_json still plaintext) — are never retroactively fixed
/// by this repository on its own; see <see cref="BackfillLegacyPlaintextBatchAsync"/> and
/// <c>ChatHistoryBackfillProcessor</c> for the one-time backfill that does that.
/// </remarks>
public sealed class ChatMessageRepository(ChatDbConnectionFactory factory) : ChatRepositoryBase(factory)
{
    // Distinct from every other AAD tag in the codebase (chat_api_key, the MCP continuation
    // store, RemoteAccountService tokens, ...) even though they all share the master DEK.
    private static readonly byte[] ContentAad = "bmb-chat-message-content-v1"u8.ToArray();

    // H3b fix: distinct from ContentAad (and every other AAD tag) so a ciphertext captured from
    // one column can never be replayed into the other, even though both live on the same row and
    // share the same master DEK.
    private static readonly byte[] ToolCallsAad = "bmb-chat-message-toolcalls-v1"u8.ToArray();

    // H3b fix: a tool_calls_json row that fails to decrypt (most likely a DEK-rotation-era row —
    // see DecryptInPlace) must degrade to something every caller can still deserialize. Unlike
    // ContentText's free-text placeholder, this one MUST stay valid JSON matching the ChatToolCall[]
    // shape: ChatEndpoints.Stream/.Confirm both do
    // JsonSerializer.Deserialize<List<ChatToolCall>>(row.ToolCallsJson, ...) with no per-row
    // try/catch, so an invalid-JSON placeholder would throw and abort loading the WHOLE transcript
    // (all rows, not just this one) instead of just this one row degrading gracefully. The
    // synthetic id deliberately can't collide with a real OpenRouter-issued tool_call id, so it is
    // never mistaken for a still-pending confirmable call.
    private const string ToolCallsDecryptFailurePlaceholder =
        """[{"id":"undecryptable","type":"function","function":{"name":"[unable to decrypt — this message may predate a DEK rotation]","arguments":"{}"}}]""";

    private const string Cols = @"id AS Id, conversation_id AS ConversationId, role AS Role,
        content_text AS ContentText, content_ciphertext AS ContentCiphertext, content_iv AS ContentIv,
        tool_calls_json AS ToolCallsJson, tool_calls_ciphertext AS ToolCallsCiphertext, tool_calls_iv AS ToolCallsIv,
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
        DecryptInPlace(rows, session);
        return rows;
    }

    public async Task CreateAsync(Models.ChatMessage message, SessionService session)
    {
        // A pure text turn has nothing to put in tool_calls_json, and a pure tool-call turn
        // (assistant → tool_calls, no text) has nothing to put in content_text — either or both
        // can be empty; ciphertext/iv for the empty side simply stay null (nothing lost either
        // way). Both are encrypted under the SAME cloned DEK to avoid two separate
        // GetMasterDek()/Clear() round-trips for one row.
        byte[]? contentCiphertext = null, contentIv = null;
        byte[]? toolCallsCiphertext = null, toolCallsIv = null;
        if (message.ContentText is { Length: > 0 } || message.ToolCallsJson is { Length: > 0 })
        {
            var masterDek = session.GetMasterDek();
            try
            {
                if (message.ContentText is { Length: > 0 })
                    (contentCiphertext, contentIv) = ArticleEncryptor.Encrypt(message.ContentText, masterDek, ContentAad);
                // H3b fix: encrypted uniformly, including pure-read tool calls — see class remarks.
                if (message.ToolCallsJson is { Length: > 0 })
                    (toolCallsCiphertext, toolCallsIv) = ArticleEncryptor.Encrypt(message.ToolCallsJson, masterDek, ToolCallsAad);
            }
            finally { Array.Clear(masterDek); }
        }

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_message
              (id, conversation_id, role, content_text, content_ciphertext, content_iv,
               tool_calls_json, tool_calls_ciphertext, tool_calls_iv, tool_call_id, model,
               tokens_in, tokens_out, tool_calls_count, duration_ms, created_at)
              VALUES (@Id, @ConversationId, @Role, NULL, @ContentCiphertext, @ContentIv,
                      NULL, @ToolCallsCiphertext, @ToolCallsIv, @ToolCallId, @Model, @TokensIn, @TokensOut,
                      @ToolCallsCount, @DurationMs, @CreatedAt)",
            new
            {
                message.Id,
                message.ConversationId,
                message.Role,
                ContentCiphertext = contentCiphertext,
                ContentIv = contentIv,
                ToolCallsCiphertext = toolCallsCiphertext,
                ToolCallsIv = toolCallsIv,
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
    /// independently per column, since a row can have either side encrypted without the other
    /// (any row written between the H3 and H3b fixes shipping has content_ciphertext but plaintext
    /// tool_calls_json). Rows written before the relevant fix have no ciphertext for that column
    /// and already carry plaintext — left untouched (legacy backward-compat path; see
    /// <see cref="BackfillLegacyPlaintextBatchAsync"/> for the one-time migration off of that
    /// path). A row that fails to decrypt (most likely: written under a master DEK that has since
    /// rotated) degrades to a visible placeholder instead of throwing and failing the whole
    /// transcript load.</summary>
    private static void DecryptInPlace(List<Models.ChatMessage> rows, SessionService session)
    {
        if (!rows.Any(r => r.ContentCiphertext is { Length: > 0 } || r.ToolCallsCiphertext is { Length: > 0 }))
            return; // nothing encrypted in this batch — avoid touching the DEK at all

        var masterDek = session.GetMasterDek();
        try
        {
            foreach (var row in rows)
            {
                if (row.ContentCiphertext is { Length: > 0 } && row.ContentIv is { Length: > 0 })
                {
                    try
                    {
                        row.ContentText = ArticleEncryptor.Decrypt(row.ContentCiphertext, row.ContentIv, masterDek, ContentAad);
                    }
                    catch (CryptographicException)
                    {
                        row.ContentText = "[unable to decrypt — this message may predate a DEK rotation]";
                    }
                    row.ContentCiphertext = null;
                    row.ContentIv = null;
                }

                if (row.ToolCallsCiphertext is { Length: > 0 } && row.ToolCallsIv is { Length: > 0 })
                {
                    try
                    {
                        row.ToolCallsJson = ArticleEncryptor.Decrypt(row.ToolCallsCiphertext, row.ToolCallsIv, masterDek, ToolCallsAad);
                    }
                    catch (CryptographicException)
                    {
                        // Must stay valid ChatToolCall[] JSON — see ToolCallsDecryptFailurePlaceholder's doc comment.
                        row.ToolCallsJson = ToolCallsDecryptFailurePlaceholder;
                    }
                    row.ToolCallsCiphertext = null;
                    row.ToolCallsIv = null;
                }
            }
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    /// <summary>
    /// H3a fix: one-time backfill for rows written before the H3/H3b encryption fixes (plaintext
    /// content_text and/or tool_calls_json, ciphertext columns still NULL). Encrypts up to
    /// <paramref name="batchSize"/> such rows in place and returns the count touched (0 = nothing
    /// left — the only thing a node that has NEVER had a plaintext row, i.e. every node created
    /// after these fixes shipped, ever sees; the SELECT below simply returns empty and no DEK is
    /// touched).
    ///
    /// A row can need migrating on only ONE side (e.g. content_text already encrypted by the
    /// original H3 fix, tool_calls_json still plaintext from before H3b) — each column is checked
    /// and migrated independently, mirroring <see cref="DecryptInPlace"/>.
    ///
    /// Crash-safe and idempotent: each row is migrated by its own single UPDATE (a crash between
    /// two rows in the same batch just leaves the remainder plaintext for the next call to pick
    /// up — no partial/corrupt state), and COALESCE keeps an already-encrypted column from ever
    /// being clobbered by a freshly-computed value, so even a wrongly-overlapping concurrent call
    /// (shouldn't happen — see ChatHistoryBackfillProcessor's single-flight guard — but this makes
    /// it harmless rather than merely unlikely) cannot re-encrypt (and silently discard the
    /// original ciphertext of) an already-migrated column.
    /// </summary>
    public async Task<int> BackfillLegacyPlaintextBatchAsync(int batchSize, SessionService session, CancellationToken ct)
    {
        using var conn = OpenConnection();
        var legacyRows = (await conn.QueryAsync<LegacyMessageRow>(
            @"SELECT id AS Id, content_text AS ContentText, tool_calls_json AS ToolCallsJson
              FROM chat_message
              WHERE (content_ciphertext IS NULL AND content_text IS NOT NULL AND content_text != '')
                 OR (tool_calls_ciphertext IS NULL AND tool_calls_json IS NOT NULL AND tool_calls_json != '')
              LIMIT @batchSize",
            new { batchSize })).ToList();

        if (legacyRows.Count == 0)
            return 0; // fresh/never-plaintext node path: one cheap SELECT, no DEK touched, no UPDATE issued

        var masterDek = session.GetMasterDek();
        try
        {
            foreach (var row in legacyRows)
            {
                if (ct.IsCancellationRequested) break;

                byte[]? contentCiphertext = null, contentIv = null;
                if (row.ContentText is { Length: > 0 })
                    (contentCiphertext, contentIv) = ArticleEncryptor.Encrypt(row.ContentText, masterDek, ContentAad);

                byte[]? toolCallsCiphertext = null, toolCallsIv = null;
                if (row.ToolCallsJson is { Length: > 0 })
                    (toolCallsCiphertext, toolCallsIv) = ArticleEncryptor.Encrypt(row.ToolCallsJson, masterDek, ToolCallsAad);

                // content_text/tool_calls_json are unconditionally nulled: if this row's side was
                // already plaintext-empty (already migrated on that side, or never had content on
                // that side), it was already NULL and this is a no-op. COALESCE on the ciphertext
                // columns is what actually prevents clobbering an already-migrated side.
                await conn.ExecuteAsync(
                    @"UPDATE chat_message
                      SET content_text = NULL,
                          content_ciphertext = COALESCE(content_ciphertext, @ContentCiphertext),
                          content_iv = COALESCE(content_iv, @ContentIv),
                          tool_calls_json = NULL,
                          tool_calls_ciphertext = COALESCE(tool_calls_ciphertext, @ToolCallsCiphertext),
                          tool_calls_iv = COALESCE(tool_calls_iv, @ToolCallsIv)
                      WHERE id = @Id",
                    new
                    {
                        row.Id,
                        ContentCiphertext = contentCiphertext,
                        ContentIv = contentIv,
                        ToolCallsCiphertext = toolCallsCiphertext,
                        ToolCallsIv = toolCallsIv
                    });
            }
        }
        finally
        {
            Array.Clear(masterDek);
        }

        return legacyRows.Count;
    }

    /// <summary>Row shape for <see cref="BackfillLegacyPlaintextBatchAsync"/>'s scan query — just
    /// the columns needed to decide what (if anything) still needs encrypting per row.</summary>
    private sealed class LegacyMessageRow
    {
        public Guid Id { get; set; }
        public string? ContentText { get; set; }
        public string? ToolCallsJson { get; set; }
    }
}
