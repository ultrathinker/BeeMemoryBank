using System.Security.Cryptography;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// CRUD for <c>chat_attachment</c> (chat.db). Node-local — never synced, never snapshotted.
/// Holds both user-uploaded images for vision turns (<c>kind='user-upload'</c>) and images
/// produced by image-gen models (<c>kind='generated-image'</c>), linked to their owning
/// <c>chat_message</c> by <c>message_id</c>.
///
/// <para>chat.db has no ACL system of its own; ownership is enforced by joining
/// <c>chat_attachment → chat_message → chat_conversation(user_id)</c> and filtering on the
/// caller's <c>user_id</c>, mirroring <see cref="ChatConversationRepository.GetByIdForUserAsync"/>.
/// A foreign conversation's attachment id yields null, never a leak.</para>
///
/// <para><b>H3 fix:</b> <c>blob</c> used to be stored and served as plaintext image bytes.
/// <see cref="CreateAsync"/> now encrypts it under the master DEK (AES-256-GCM) before the row is
/// written; every read method decrypts it back, so callers only ever see plaintext bytes. A NULL
/// <c>iv</c> column means a legacy row written before this fix — its <c>blob</c> is read as-is
/// (backward compat, not retroactively re-encrypted, mirroring ChatMessageRepository).</para>
/// </summary>
public sealed class ChatAttachmentRepository(ChatDbConnectionFactory factory) : ChatRepositoryBase(factory)
{
    // Distinct from ChatMessageRepository's ContentAad and every other AAD tag in the codebase.
    private static readonly byte[] BlobAad = "bmb-chat-attachment-blob-v1"u8.ToArray();

    private const string Cols = @"a.id AS Id, a.message_id AS MessageId, a.kind AS Kind,
        a.mime AS Mime, a.blob AS Blob, a.iv AS Iv, a.created_at AS CreatedAt";

    public async Task CreateAsync(Models.ChatAttachment attachment, SessionService session)
    {
        byte[] blob = attachment.Blob ?? [];
        byte[]? iv = null;
        if (blob.Length > 0)
        {
            var masterDek = session.GetMasterDek();
            try { (blob, iv) = MediaEncryptor.Encrypt(blob, masterDek, BlobAad); }
            finally { Array.Clear(masterDek); }
        }

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_attachment (id, message_id, kind, mime, blob, iv, created_at)
              VALUES (@Id, @MessageId, @Kind, @Mime, @Blob, @Iv, @CreatedAt)",
            new { attachment.Id, attachment.MessageId, attachment.Kind, attachment.Mime, Blob = blob, Iv = iv, attachment.CreatedAt });
    }

    /// <summary>All attachments for a conversation (used to attach image metadata to the
    /// transcript when reopening a conversation, and to re-include prior user-uploaded images in
    /// a multi-turn vision request). Not ownership-filtered here — the caller has already resolved
    /// the conversation under the caller's user_id.</summary>
    public async Task<List<Models.ChatAttachment>> ListByConversationAsync(Guid conversationId, SessionService session)
    {
        using var conn = OpenConnection();
        var rows = (await conn.QueryAsync<Models.ChatAttachment>(
            $@"SELECT {Cols} FROM chat_attachment a
               JOIN chat_message m ON m.id = a.message_id
               WHERE m.conversation_id = @conversationId
               ORDER BY a.created_at ASC",
            new { conversationId })).ToList();
        DecryptInPlace(rows, session);
        return rows;
    }

    /// <summary>Ownership-checked single-attachment read (joins through message → conversation
    /// and filters on user_id). Returns null if the attachment does not exist OR belongs to a
    /// different user — so the GET endpoint can enforce ownership with one lookup.</summary>
    public async Task<Models.ChatAttachment?> GetByIdForUserAsync(Guid id, int userId, SessionService session)
    {
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<Models.ChatAttachment>(
            $@"SELECT {Cols} FROM chat_attachment a
               JOIN chat_message m ON m.id = a.message_id
               JOIN chat_conversation c ON c.id = m.conversation_id
               WHERE a.id = @id AND c.user_id = @userId",
            new { id, userId });
        if (row != null)
            DecryptInPlace([row], session);
        return row;
    }

    private static void DecryptInPlace(List<Models.ChatAttachment> rows, SessionService session)
    {
        if (!rows.Any(r => r.Iv is { Length: > 0 }))
            return;

        var masterDek = session.GetMasterDek();
        try
        {
            foreach (var row in rows)
            {
                if (row.Iv is not { Length: > 0 } || row.Blob is not { Length: > 0 })
                    continue; // legacy plaintext row, or no bytes to decrypt
                try
                {
                    row.Blob = MediaEncryptor.Decrypt(row.Blob, row.Iv, masterDek, BlobAad);
                }
                catch (CryptographicException)
                {
                    // Most likely a DEK rotation since this attachment was saved. Blank it out
                    // rather than serving garbage bytes as an "image" or throwing and failing the
                    // whole list/read.
                    row.Blob = [];
                }
                row.Iv = null;
            }
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }
}
