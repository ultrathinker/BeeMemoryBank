using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Records local operations to the event log with Ed25519 signature.
/// Used by ArticleService for every create/update/delete operation.
/// </summary>
public class EventLogger(
    INodeIdentityRepository nodeRepo,
    IEventLogRepository eventLogRepo,
    ILamportClock clock,
    IActorProvider actorProvider,
    ISyncTrigger syncTrigger) : IEventLogger
{
    public async Task LogCreateAsync(Article article, EncryptedArticleBody body)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var payload = new ArticleEventPayload(
            Title: article.Title,
            TreePath: article.TreePath,
            Tags: [.. article.Tags],
            CiphertextB64: Convert.ToBase64String(body.Ciphertext),
            IvB64: Convert.ToBase64String(body.IV),
            EncryptedDekB64: Convert.ToBase64String(body.EncryptedDek),
            DekIvB64: Convert.ToBase64String(body.DekIV),
            Status: article.Status,
            CreatedAt: article.CreatedAt,
            UpdatedAt: article.UpdatedAt,
            DekEpoch: 1
        );

        await AppendEventAsync(identity, EventTypes.ArticleCreate, article.Id, article.LamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogUpdateAsync(Article article, EncryptedArticleBody? body)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        if (body == null) return; // Article without body — don't log event

        var payload = new ArticleEventPayload(
            Title: article.Title,
            TreePath: article.TreePath,
            Tags: [.. article.Tags],
            CiphertextB64: Convert.ToBase64String(body.Ciphertext),
            IvB64: Convert.ToBase64String(body.IV),
            EncryptedDekB64: Convert.ToBase64String(body.EncryptedDek),
            DekIvB64: Convert.ToBase64String(body.DekIV),
            Status: article.Status,
            CreatedAt: article.CreatedAt,
            UpdatedAt: article.UpdatedAt,
            DekEpoch: 1
        );

        await AppendEventAsync(identity, EventTypes.ArticleUpdate, article.Id, article.LamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogDeleteAsync(Guid articleId)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new ArticleDeletePayload(DeletedAt: DateTime.UtcNow);

        await AppendEventAsync(identity, EventTypes.ArticleDelete, articleId, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogWhitelistAddAsync(WhitelistEntry entry)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new WhitelistAddPayload(
            NodeId: entry.NodeId,
            DisplayName: entry.DisplayName,
            PublicKeyB64: Convert.ToBase64String(entry.Ed25519PublicKey),
            ApiAddress: entry.ApiAddress,
            CanGenerateEmbeddings: entry.CanGenerateEmbeddings);

        await AppendEventAsync(identity, EventTypes.WhitelistAdd, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogWhitelistRevokeAsync(Guid nodeId)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new WhitelistRevokePayload(NodeId: nodeId);

        await AppendEventAsync(identity, EventTypes.WhitelistRevoke, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogWhitelistUpdateAsync(Guid nodeId, string? apiAddress, string? displayName)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new WhitelistUpdatePayload(NodeId: nodeId, ApiAddress: apiAddress, DisplayName: displayName);

        await AppendEventAsync(identity, EventTypes.WhitelistUpdate, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogCommentCreateAsync(Comment comment)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new CommentEventPayload(
            CommentId: comment.CommentId,
            ArticleId: comment.ArticleId,
            Text: comment.Encrypted ? "" : comment.Text,
            CreatedAt: comment.CreatedAt,
            CiphertextB64: comment.Ciphertext != null ? Convert.ToBase64String(comment.Ciphertext) : null,
            IvB64: comment.IV != null ? Convert.ToBase64String(comment.IV) : null,
            Encrypted: comment.Encrypted);

        await AppendEventAsync(identity, EventTypes.CommentCreate, comment.ArticleId, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogCommentDeleteAsync(Guid commentId)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new CommentDeletePayload(CommentId: commentId);

        await AppendEventAsync(identity, EventTypes.CommentDelete, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogFolderCreateAsync(Core.Models.Folder folder)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var payload = new FolderCreatePayload(
            FolderId: folder.Id,
            Path: folder.Path,
            Name: folder.Name,
            ParentPath: folder.ParentPath,
            CreatedAt: folder.CreatedAt,
            UpdatedAt: folder.UpdatedAt);

        await AppendEventAsync(identity, EventTypes.FolderCreate, null, folder.LamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogFolderRenameAsync(Guid folderId, string oldPath, string newPath, string newName,
        string? newParentPath, long lamportTs, DateTime updatedAt)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var payload = new FolderRenamePayload(
            FolderId: folderId,
            OldPath: oldPath,
            NewPath: newPath,
            NewName: newName,
            NewParentPath: newParentPath,
            UpdatedAt: updatedAt);

        await AppendEventAsync(identity, EventTypes.FolderRename, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogFolderDeleteAsync(Guid folderId, string path, DateTime deletedAt)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new FolderDeletePayload(
            FolderId: folderId,
            Path: path,
            DeletedAt: deletedAt);

        await AppendEventAsync(identity, EventTypes.FolderDelete, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogMediaCreateAsync(Media media, byte[] ciphertext)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new MediaEventPayload(
            MediaId: media.Id,
            ArticleId: media.ArticleId,
            FileName: media.FileName,
            ContentType: media.ContentType,
            FileSize: media.FileSize,
            CiphertextB64: Convert.ToBase64String(ciphertext),
            IvB64: Convert.ToBase64String(media.IV),
            EncryptedDekB64: Convert.ToBase64String(media.EncryptedDek),
            DekIvB64: Convert.ToBase64String(media.DekIV),
            CreatedAt: media.CreatedAt);

        await AppendEventAsync(identity, EventTypes.MediaCreate, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogMediaDeleteAsync(Guid mediaId)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new MediaDeletePayload(MediaId: mediaId, DeletedAt: DateTime.UtcNow);

        await AppendEventAsync(identity, EventTypes.MediaDelete, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    private async Task AppendEventAsync(
        NodeIdentity identity,
        string eventType,
        Guid? articleId,
        long lamportTs,
        string payloadJson)
    {
        var now = DateTime.UtcNow;
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = identity.NodeId,
            LamportTs = lamportTs,
            EventType = eventType,
            ArticleId = articleId,
            Payload = payloadJson,
            Signature = [],
            ProtocolVersion = 1,
            CreatedAt = now,
            ActorType = actorProvider.ActorType,
            ActorName = actorProvider.ActorName
        };

        var sigPayload = EventSignature.BuildPayload(evt);
        evt.Signature = Ed25519Signer.Sign(identity.Ed25519PrivateKey, sigPayload);

        await eventLogRepo.AppendAsync(evt);
        syncTrigger.Signal();
    }
}
