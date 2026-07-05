using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// CRUD for <c>chat_conversation</c> (chat.db). Node-local — never synced, never snapshotted.
/// Persisted history is consumed starting in Phase 2; the repo is scaffolded here per plan §2.
/// </summary>
public sealed class ChatConversationRepository(ChatDbConnectionFactory factory) : ChatRepositoryBase(factory)
{
    private const string Cols = @"id AS Id, user_id AS UserId, title AS Title,
        created_at AS CreatedAt, updated_at AS UpdatedAt, is_home_pinned AS IsHomePinned";

    /// <summary>User-scoped read (Phase 2). Returns null when the conversation does not exist OR is
    /// owned by a different user — so the conversation/message endpoints can enforce "a user must
    /// never see another user's conversations" with a single lookup (plan §2 Phase 2). chat.db has
    /// no ACL system of its own, so this filter is the only boundary.</summary>
    public async Task<Models.ChatConversation?> GetByIdForUserAsync(Guid id, int userId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Models.ChatConversation>(
            $"SELECT {Cols} FROM chat_conversation WHERE id = @id AND user_id = @userId",
            new { id, userId });
    }

    public async Task<List<Models.ChatConversation>> ListByUserAsync(int userId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatConversation>(
            $"SELECT {Cols} FROM chat_conversation WHERE user_id = @userId ORDER BY updated_at DESC",
            new { userId })).ToList();
    }

    public async Task CreateAsync(Models.ChatConversation conversation)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_conversation (id, user_id, title, created_at, updated_at)
              VALUES (@Id, @UserId, @Title, @CreatedAt, @UpdatedAt)",
            conversation);
    }

    public async Task UpdateTitleAsync(Guid id, string title)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE chat_conversation SET title = @title, updated_at = @now WHERE id = @id",
            new { id, title, now = UtcNow() });
    }

    public async Task TouchAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE chat_conversation SET updated_at = @now WHERE id = @id",
            new { id, now = UtcNow() });
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = OpenConnection();
        // ON semantics are app-managed (no FKs/cascades): chat_attachment → chat_message →
        // chat_conversation. Delete the attachments that reference this conversation's messages
        // BEFORE the messages go away, otherwise their blobs are orphaned in chat.db forever.
        await conn.ExecuteAsync(
            "DELETE FROM chat_attachment WHERE message_id IN (SELECT id FROM chat_message WHERE conversation_id = @id)",
            new { id });
        await conn.ExecuteAsync("DELETE FROM chat_message WHERE conversation_id = @id", new { id });
        await conn.ExecuteAsync("DELETE FROM chat_conversation WHERE id = @id", new { id });
    }

    /// <summary>The caller's home-pinned conversation id, if any. At most one row per user
    /// ever has the flag (see SetHomePinnedAsync); LIMIT 1 is a belt-and-braces guard.</summary>
    public async Task<Guid?> GetHomePinnedIdAsync(int userId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM chat_conversation WHERE user_id = @userId AND is_home_pinned = 1 LIMIT 1",
            new { userId });
    }

    /// <summary>Pins one conversation to the user's homepage. ONE atomic UPDATE flips the flag
    /// on for the target row and off for every other row of the same user — the application-
    /// layer "at most one pin per user" invariant, with no window for two pins (chat.db has no
    /// unique-index precedent to lean on; see ChatDbInitializer). The caller must have already
    /// verified ownership of <paramref name="conversationId"/>.</summary>
    public async Task SetHomePinnedAsync(int userId, Guid conversationId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE chat_conversation
              SET is_home_pinned = CASE WHEN id = @conversationId THEN 1 ELSE 0 END
              WHERE user_id = @userId",
            new { userId, conversationId });
    }

    /// <summary>Clears the user's home pin ("Close chat"). Never deletes any data.</summary>
    public async Task ClearHomePinAsync(int userId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE chat_conversation SET is_home_pinned = 0 WHERE user_id = @userId AND is_home_pinned = 1",
            new { userId });
    }
}
