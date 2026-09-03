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
/// </remarks>
public sealed class ChatMessageRepository(ChatDbConnectionFactory factory) : ChatRepositoryBase(factory)
{
    // Distinct from every other AAD tag in the codebase (chat_api_key, the MCP continuation
    // store, RemoteAccountService tokens, ...) even though they all share the master DEK.
    private static readonly byte[] ContentAad = "bmb-chat-message-content-v1"u8.ToArray();

    private const string Cols = @"id AS Id, conversation_id AS ConversationId, role AS Role,
        content_text AS ContentText, content_ciphertext AS ContentCiphertext, content_iv AS ContentIv,
        tool_calls_json AS ToolCallsJson, tool_call_id AS ToolCallId,
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
        // A pure tool-call turn (assistant → tool_calls, no text) has nothing to encrypt;
        // ciphertext/iv stay null and content_text stays null too (nothing lost either way).
        byte[]? ciphertext = null, iv = null;
        if (message.ContentText is { Length: > 0 })
        {
            var masterDek = session.GetMasterDek();
            try { (ciphertext, iv) = ArticleEncryptor.Encrypt(message.ContentText, masterDek, ContentAad); }
            finally { Array.Clear(masterDek); }
        }

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_message
              (id, conversation_id, role, content_text, content_ciphertext, content_iv,
               tool_calls_json, tool_call_id, model, tokens_in, tokens_out, tool_calls_count,
               duration_ms, created_at)
              VALUES (@Id, @ConversationId, @Role, NULL, @Ciphertext, @Iv,
                      @ToolCallsJson, @ToolCallId, @Model, @TokensIn, @TokensOut, @ToolCallsCount,
                      @DurationMs, @CreatedAt)",
            new
            {
                message.Id,
                message.ConversationId,
                message.Role,
                Ciphertext = ciphertext,
                Iv = iv,
                message.ToolCallsJson,
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
    /// <see cref="Models.ChatMessage.ContentText"/> for every row that has it, in place. Rows
    /// written before the H3 fix have no ciphertext and already carry plaintext in
    /// <see cref="Models.ChatMessage.ContentText"/> — left untouched (legacy backward-compat
    /// path). A row that fails to decrypt (most likely: written under a master DEK that has since
    /// rotated) degrades to a visible placeholder instead of throwing and failing the whole
    /// transcript load.</summary>
    private static void DecryptInPlace(List<Models.ChatMessage> rows, SessionService session)
    {
        if (!rows.Any(r => r.ContentCiphertext is { Length: > 0 }))
            return; // nothing encrypted in this batch — avoid touching the DEK at all

        var masterDek = session.GetMasterDek();
        try
        {
            foreach (var row in rows)
            {
                if (row.ContentCiphertext is not { Length: > 0 } || row.ContentIv is not { Length: > 0 })
                    continue;
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
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }
}
