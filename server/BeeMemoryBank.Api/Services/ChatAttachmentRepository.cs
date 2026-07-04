using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// CRUD for <c>chat_attachment</c> (chat.db). Node-local — never synced, never snapshotted.
/// Holds both user-uploaded images for vision turns (<c>kind='user-upload'</c>) and images
/// produced by image-gen models (<c>kind='generated-image'</c>), linked to their owning
/// <c>chat_message</c> by <c>message_id</c>. The schema (created in Phase 0 by
/// <see cref="ChatDbInitializer"/>) is reused unchanged: <c>id, message_id, kind, mime, blob,
/// created_at</c> — Phase 5 added NO migration and NO additive column.
///
/// <para>chat.db has no ACL system of its own; ownership is enforced by joining
/// <c>chat_attachment → chat_message → chat_conversation(user_id)</c> and filtering on the
/// caller's <c>user_id</c>, mirroring <see cref="ChatConversationRepository.GetByIdForUserAsync"/>.
/// A foreign conversation's attachment id yields null, never a leak.</para>
/// </summary>
public sealed class ChatAttachmentRepository(ChatDbConnectionFactory factory) : ChatRepositoryBase(factory)
{
    private const string Cols = @"a.id AS Id, a.message_id AS MessageId, a.kind AS Kind,
        a.mime AS Mime, a.blob AS Blob, a.created_at AS CreatedAt";

    public async Task CreateAsync(Models.ChatAttachment attachment)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_attachment (id, message_id, kind, mime, blob, created_at)
              VALUES (@Id, @MessageId, @Kind, @Mime, @Blob, @CreatedAt)",
            attachment);
    }

    /// <summary>All attachments for a conversation (used to attach image metadata to the
    /// transcript when reopening a conversation). Not ownership-filtered here — the caller has
    /// already resolved the conversation under the caller's user_id.</summary>
    public async Task<List<Models.ChatAttachment>> ListByConversationAsync(Guid conversationId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatAttachment>(
            $@"SELECT {Cols} FROM chat_attachment a
               JOIN chat_message m ON m.id = a.message_id
               WHERE m.conversation_id = @conversationId
               ORDER BY a.created_at ASC",
            new { conversationId })).ToList();
    }

    /// <summary>Ownership-checked single-attachment read (joins through message → conversation
    /// and filters on user_id). Returns null if the attachment does not exist OR belongs to a
    /// different user — so the GET endpoint can enforce ownership with one lookup.</summary>
    public async Task<Models.ChatAttachment?> GetByIdForUserAsync(Guid id, int userId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Models.ChatAttachment>(
            $@"SELECT {Cols} FROM chat_attachment a
               JOIN chat_message m ON m.id = a.message_id
               JOIN chat_conversation c ON c.id = m.conversation_id
               WHERE a.id = @id AND c.user_id = @userId",
            new { id, userId });
    }
}
