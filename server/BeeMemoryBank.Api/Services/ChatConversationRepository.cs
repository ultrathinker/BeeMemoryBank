using BeeMemoryBank.Core.Exceptions;
using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// CRUD for <c>chat_conversation</c> (chat.db). Node-local — never synced, never snapshotted.
/// </summary>
/// <remarks>
/// A conversation's title is the first 120 characters of the user's first message, so it is
/// content, and it is sealed under the node chat key like the messages themselves
/// (<c>title_ciphertext</c>/<c>title_iv</c>, <c>title_key_v</c> as for every other chat.db column,
/// see <see cref="ChatDataProtector"/>). The plaintext <c>title</c> column is written empty; titles
/// from before that stay there until <see cref="MigrateLegacyBatchAsync"/> seals them. The AAD binds
/// a title to its conversation id, so a sealed title cannot be moved onto another row.
/// </remarks>
public sealed class ChatConversationRepository(ChatDbConnectionFactory factory, ChatDataProtector protector)
    : ChatRepositoryBase(factory)
{
    private const string Cols = @"id AS Id, user_id AS UserId, title AS Title,
        title_ciphertext AS TitleCiphertext, title_iv AS TitleIv, title_key_v AS TitleKeyVersion,
        created_at AS CreatedAt, updated_at AS UpdatedAt, is_home_pinned AS IsHomePinned";

    /// <summary>Shown instead of a sealed title while the vault is locked.</summary>
    public const string LockedTitlePlaceholder = "[locked]";

    /// <summary>Shown instead of a sealed title that opens under no key this node holds.</summary>
    public const string UndecryptableTitlePlaceholder = "[unable to decrypt title]";

    /// <summary>
    /// Titles still in the plaintext column. Shared verbatim by the partial index in
    /// <see cref="ChatDbInitializer"/> and the scan in <see cref="MigrateLegacyBatchAsync"/> — SQLite
    /// only uses a partial index whose WHERE the query's WHERE implies.
    /// </summary>
    internal const string LegacyTitlePredicate = "title_key_v IS NULL AND title != ''";

    /// <summary>User-scoped read. Returns null when the conversation does not exist OR is
    /// owned by a different user — so the conversation/message endpoints can enforce "a user must
    /// never see another user's conversations" with a single lookup. chat.db has
    /// no ACL system of its own, so this filter is the only boundary.</summary>
    public async Task<Models.ChatConversation?> GetByIdForUserAsync(Guid id, int userId)
    {
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<ConversationRow>(
            $"SELECT {Cols} FROM chat_conversation WHERE id = @id AND user_id = @userId",
            new { id, userId });
        return row is null ? null : (await OpenTitlesAsync([row]))[0];
    }

    /// <summary>The user's conversations, newest first. Works while the vault is locked: sealed
    /// titles then read as <see cref="LockedTitlePlaceholder"/>.</summary>
    public async Task<List<Models.ChatConversation>> ListByUserAsync(int userId)
    {
        using var conn = OpenConnection();
        var rows = (await conn.QueryAsync<ConversationRow>(
            $"SELECT {Cols} FROM chat_conversation WHERE user_id = @userId ORDER BY updated_at DESC",
            new { userId })).ToList();
        return await OpenTitlesAsync(rows);
    }

    /// <summary>Creates the row with its title sealed. Needs an unlocked vault.</summary>
    public async Task CreateAsync(Models.ChatConversation conversation)
    {
        var (ciphertext, iv) = await SealTitleAsync(conversation.Id, conversation.Title);
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_conversation
              (id, user_id, title, title_ciphertext, title_iv, title_key_v, created_at, updated_at)
              VALUES (@Id, @UserId, '', @ciphertext, @iv, @version, @CreatedAt, @UpdatedAt)",
            new
            {
                conversation.Id, conversation.UserId, conversation.CreatedAt, conversation.UpdatedAt,
                ciphertext, iv, version = ChatDataProtector.ChatKeyVersion
            });
    }

    /// <summary>Renames, sealing the new title. Needs an unlocked vault.</summary>
    public async Task UpdateTitleAsync(Guid id, string title)
    {
        var (ciphertext, iv) = await SealTitleAsync(id, title);
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE chat_conversation
              SET title = '', title_ciphertext = @ciphertext, title_iv = @iv, title_key_v = @version, updated_at = @now
              WHERE id = @id",
            new { id, ciphertext, iv, version = ChatDataProtector.ChatKeyVersion, now = UtcNow() });
    }

    /// <summary>
    /// Seals up to <paramref name="batchSize"/> titles still in the plaintext column and returns how
    /// many it looked at (0 = nothing left). Each row moves by one UPDATE guarded by
    /// <c>title_key_v IS NULL</c>, so a rename that got there first is never overwritten with the
    /// older title.
    /// </summary>
    public async Task<int> MigrateLegacyBatchAsync(int batchSize, CancellationToken ct)
    {
        using var conn = OpenConnection();
        var legacy = (await conn.QueryAsync<LegacyTitleRow>(
            $"SELECT id AS Id, title AS Title FROM chat_conversation WHERE {LegacyTitlePredicate} LIMIT @batchSize",
            new { batchSize })).ToList();
        if (legacy.Count == 0) return 0;

        using var key = await protector.AcquireAsync(ct);
        foreach (var row in legacy)
        {
            if (ct.IsCancellationRequested) break;
            var (ciphertext, iv) = key.EncryptText(row.Title, TitleAad(row.Id));
            await conn.ExecuteAsync(
                @"UPDATE chat_conversation
                  SET title = '', title_ciphertext = @ciphertext, title_iv = @iv, title_key_v = @version
                  WHERE id = @id AND title_key_v IS NULL",
                new { id = row.Id, ciphertext, iv, version = ChatDataProtector.ChatKeyVersion });
        }
        return legacy.Count;
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

    private async Task<(byte[] ciphertext, byte[] iv)> SealTitleAsync(Guid id, string title)
    {
        using var key = await protector.AcquireAsync();
        return key.EncryptText(title, TitleAad(id));
    }

    private async Task<List<Models.ChatConversation>> OpenTitlesAsync(List<ConversationRow> rows)
    {
        ChatKeyLease? key = null;
        var locked = false;
        if (rows.Any(r => r.TitleKeyVersion is not null))
        {
            try { key = await protector.TryAcquireForReadAsync(); }
            catch (SessionLockedException) { locked = true; }
        }

        using (key)
        {
            return rows.Select(r => new Models.ChatConversation
            {
                Id = r.Id,
                UserId = r.UserId,
                Title = r.TitleKeyVersion is null ? r.Title
                    : locked ? LockedTitlePlaceholder
                    : OpenTitle(key, r) ?? UndecryptableTitlePlaceholder,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                IsHomePinned = r.IsHomePinned
            }).ToList();
        }
    }

    // Titles are only ever sealed under the chat key, never directly under the master DEK, so any
    // other key version has nothing this node could open it with.
    private string? OpenTitle(ChatKeyLease? key, ConversationRow r) =>
        r.TitleKeyVersion == ChatDataProtector.ChatKeyVersion && r.TitleCiphertext is { Length: > 0 } && r.TitleIv is { Length: > 0 }
            ? protector.TryDecryptText(key, r.TitleCiphertext, r.TitleIv, r.TitleKeyVersion, TitleAad(r.Id))
            : null;

    private static byte[] TitleAad(Guid id) => System.Text.Encoding.UTF8.GetBytes($"bmb-chat-conversation-title-v1:{id:D}");

    private sealed class ConversationRow
    {
        public Guid Id { get; set; }
        public int UserId { get; set; }
        public string Title { get; set; } = "";
        public byte[]? TitleCiphertext { get; set; }
        public byte[]? TitleIv { get; set; }
        public int? TitleKeyVersion { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsHomePinned { get; set; }
    }

    private sealed class LegacyTitleRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
    }
}
