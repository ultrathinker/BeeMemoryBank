using BeeMemoryBank.Core.Services;
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
/// <see cref="CreateAsync"/> now encrypts it (AES-256-GCM, under the node chat key — see
/// <see cref="ChatDataProtector"/> for why not the master DEK) before the row is written; every read
/// method decrypts it back, so callers only ever see plaintext bytes. A NULL <c>iv</c> column means
/// a legacy row written before this fix — its <c>blob</c> is read as-is; <c>iv</c> set with a NULL
/// <c>key_v</c> means a legacy row sealed directly under the master DEK. Both are moved onto the
/// chat key by <see cref="MigrateLegacyBatchAsync"/>.</para>
/// </summary>
public sealed class ChatAttachmentRepository(ChatDbConnectionFactory factory, ChatDataProtector protector)
    : ChatRepositoryBase(factory)
{
    // Distinct from ChatMessageRepository's ContentAad and every other AAD tag in the codebase.
    private static readonly byte[] BlobAad = "bmb-chat-attachment-blob-v1"u8.ToArray();

    /// <summary>
    /// Attachments whose bytes still need moving onto the chat key (legacy plaintext, or legacy
    /// master-DEK ciphertext). Shared verbatim by the partial index in <see cref="ChatDbInitializer"/>
    /// and the scan in <see cref="MigrateLegacyBatchAsync"/>.
    /// </summary>
    internal const string LegacyKeyPredicate = "key_v IS NULL AND blob IS NOT NULL AND length(blob) > 0";

    private const string Cols = @"a.id AS Id, a.message_id AS MessageId, a.kind AS Kind,
        a.mime AS Mime, a.blob AS Blob, a.iv AS Iv, a.key_v AS KeyVersion, a.created_at AS CreatedAt";

    public async Task CreateAsync(Models.ChatAttachment attachment, SessionService session)
    {
        byte[] blob = attachment.Blob ?? [];
        byte[]? iv = null;
        int? keyVersion = null;
        if (blob.Length > 0)
        {
            using var key = await protector.AcquireAsync();
            (blob, iv) = key.EncryptBytes(blob, BlobAad);
            keyVersion = ChatDataProtector.ChatKeyVersion;
        }

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_attachment (id, message_id, kind, mime, blob, iv, key_v, created_at)
              VALUES (@Id, @MessageId, @Kind, @Mime, @Blob, @Iv, @KeyVersion, @CreatedAt)",
            new
            {
                attachment.Id, attachment.MessageId, attachment.Kind, attachment.Mime,
                Blob = blob, Iv = iv, KeyVersion = keyVersion, attachment.CreatedAt
            });
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
        await DecryptInPlaceAsync(rows);
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
            await DecryptInPlaceAsync([row]);
        return row;
    }

    private async Task DecryptInPlaceAsync(List<Models.ChatAttachment> rows)
    {
        if (!rows.Any(r => r.Iv is { Length: > 0 }))
            return;

        var needsChatKey = rows.Any(r => r.Iv is { Length: > 0 } && r.KeyVersion == ChatDataProtector.ChatKeyVersion);
        using var key = needsChatKey ? await protector.TryAcquireForReadAsync() : null;

        foreach (var row in rows)
        {
            if (row.Iv is not { Length: > 0 } || row.Blob is not { Length: > 0 })
            {
                row.KeyVersion = null;
                continue; // legacy plaintext row, or no bytes to decrypt
            }

            // A blob sealed under a key this node no longer has is blanked out rather than served
            // as garbage bytes posing as an "image", or thrown, failing the whole list/read.
            row.Blob = protector.TryDecryptBytes(key, row.Blob, row.Iv, row.KeyVersion, BlobAad) ?? [];
            row.Iv = null;
            row.KeyVersion = null;
        }
    }

    /// <summary>
    /// Moves up to <paramref name="batchSize"/> legacy attachment blobs onto the chat key — plaintext
    /// ones (<c>iv IS NULL</c>, written before the H3 fix) and ones sealed directly under the master
    /// DEK. Mirrors <c>ChatMessageRepository.MigrateLegacyBatchAsync</c>, including the <c>-1</c>
    /// marker for a ciphertext no available master DEK opens and the idempotency guard; see its doc
    /// comment. Returns the number of rows looked at (0 = nothing left).
    /// </summary>
    public async Task<int> MigrateLegacyBatchAsync(int batchSize, SessionService session, CancellationToken ct)
    {
        using var conn = OpenConnection();
        var legacyRows = (await conn.QueryAsync<LegacyAttachmentRow>(
            $@"SELECT id AS Id, blob AS Blob, iv AS Iv FROM chat_attachment
               WHERE {LegacyKeyPredicate}
               LIMIT @batchSize",
            new { batchSize })).ToList();

        if (legacyRows.Count == 0)
            return 0;

        using var key = await protector.AcquireAsync(ct);
        foreach (var row in legacyRows)
        {
            if (ct.IsCancellationRequested) break;

            var plaintext = row.Iv is null
                ? row.Blob!
                : protector.TryDecryptBytes(null, row.Blob!, row.Iv, keyVersion: null, BlobAad);
            if (plaintext is null)
            {
                await conn.ExecuteAsync(
                    "UPDATE chat_attachment SET key_v = @Marker WHERE id = @Id AND key_v IS NULL",
                    new { row.Id, Marker = ChatDataProtector.LegacyUnreadable });
                continue;
            }

            var (ciphertext, iv) = key.EncryptBytes(plaintext, BlobAad);
            if (row.Iv is not null) Array.Clear(plaintext);
            await conn.ExecuteAsync(
                "UPDATE chat_attachment SET blob = @Ciphertext, iv = @Iv, key_v = @Version WHERE id = @Id AND key_v IS NULL",
                new { row.Id, Ciphertext = ciphertext, Iv = iv, Version = ChatDataProtector.ChatKeyVersion });
        }

        return legacyRows.Count;
    }

    /// <summary>Row shape for <see cref="MigrateLegacyBatchAsync"/>'s scan query.</summary>
    private sealed class LegacyAttachmentRow
    {
        public Guid Id { get; set; }
        public byte[]? Blob { get; set; }
        public byte[]? Iv { get; set; }
    }
}
