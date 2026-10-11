using System.Data;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;

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
    ISyncTrigger syncTrigger,
    SessionService session,
    IBlobRepository blobRepo,
    // Step A (docs/design/step-a-spec.md 3.9): after this node applied the metadata activation it writes protocol 4 and
    // seals every content event with the key ring's current key. Without a policy it writes protocol 3, as 2.5.1 did.
    ISyncProtocolPolicy? protocolPolicy = null,
    IMetadataKeyRing? keyRing = null,
    // While a DEK rotation's new metadata key is not published, nothing is sealed (spec 4.4): the previous generation is one a
    // node the rotation left out can still open.
    IMetadataKeyPublishGate? publishGate = null,
    // Holds a DEK rotation's commit back while an operation that seals is in flight (spec 4.4), see BeginAuthoringAsync.
    AuthoringBarrier? barrier = null) : IEventLogger
{
    public void SignalSync() => syncTrigger.Signal();

    /// <summary>
    /// Enters the authoring barrier and refuses, before the caller changed anything, while the new metadata key of the last DEK
    /// rotation is not published. Deliberately not <c>async</c>: the barrier's ambient flow is set in the caller's context.
    /// </summary>
    public ValueTask<IDisposable> BeginAuthoringAsync()
    {
        var entering = barrier?.EnterShared() ?? new ValueTask<IDisposable>(NullAuthoringScope.Instance);
        return RefuseWhilePendingAsync(entering);
    }

    private async ValueTask<IDisposable> RefuseWhilePendingAsync(ValueTask<IDisposable> entering)
    {
        var scope = await entering;
        try
        {
            ThrowIfPublishPending();
        }
        catch
        {
            scope.Dispose();
            throw;
        }
        return scope;
    }

    /// <summary>Nothing is sealed between the DEK commit of a rotation and the publication of its new key (spec 4.4).</summary>
    private void ThrowIfPublishPending()
    {
        if (protocolPolicy is { SealsMetadata: true } && publishGate is { IsPending: true })
            throw new Core.Exceptions.MetadataKeyPublishPendingException();
    }

    /// <summary>
    /// Makes sure the bytes an event is about to reference exist in tbl_blob, in the caller's
    /// transaction, and returns their hash. For article bodies this is normally a no-op —
    /// ArticleBodyRepository.UpsertAsync already stored the blob in the same transaction — but
    /// doing it here too keeps one invariant in one place: an event this class writes never names
    /// a blob this node does not hold. For media it is the only insert: the file on disk is the
    /// media's home, and the blob row exists so the pusher can ship the bytes ahead of the event.
    /// </summary>
    private Task<string> EnsureBlobAsync(byte[] ciphertext, IDbTransaction? transaction) =>
        blobRepo.StoreAsync(ciphertext, transaction);

    public async Task LogCreateAsync(Article article, EncryptedArticleBody body, string[] conceptTags, IDbTransaction? transaction = null)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var payload = new ArticleEventPayload(
            Title: article.Title,
            TreePath: article.TreePath,
            ConceptTags: conceptTags,
            CiphertextB64: null,
            IvB64: Convert.ToBase64String(body.IV),
            EncryptedDekB64: Convert.ToBase64String(body.EncryptedDek),
            DekIvB64: Convert.ToBase64String(body.DekIV),
            Status: article.Status,
            CreatedAt: article.CreatedAt,
            UpdatedAt: article.UpdatedAt,
            DekEpoch: identity.DekEpoch,
            Protected: article.Protected,
            ProtectionHint: article.ProtectionHint,
            CiphertextSha256: await EnsureBlobAsync(body.Ciphertext, transaction)
        );

        await AppendEventAsync(identity, EventTypes.ArticleCreate, article.Id, article.LamportTs,
            JsonSerializer.Serialize(payload), transaction: transaction);
    }

    public async Task LogUpdateAsync(Article article, EncryptedArticleBody? body, string[] conceptTags, IDbTransaction? transaction = null)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        if (body == null) return;

        var payload = new ArticleEventPayload(
            Title: article.Title,
            TreePath: article.TreePath,
            ConceptTags: conceptTags,
            CiphertextB64: null,
            IvB64: Convert.ToBase64String(body.IV),
            EncryptedDekB64: Convert.ToBase64String(body.EncryptedDek),
            DekIvB64: Convert.ToBase64String(body.DekIV),
            Status: article.Status,
            CreatedAt: article.CreatedAt,
            UpdatedAt: article.UpdatedAt,
            DekEpoch: identity.DekEpoch,
            Protected: article.Protected,
            ProtectionHint: article.ProtectionHint,
            CiphertextSha256: await EnsureBlobAsync(body.Ciphertext, transaction)
        );

        await AppendEventAsync(identity, EventTypes.ArticleUpdate, article.Id, article.LamportTs,
            JsonSerializer.Serialize(payload), transaction: transaction);
    }

    public async Task<RowVersion> LogDeleteAsync(Guid articleId, IDbTransaction? transaction = null)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new ArticleDeletePayload(DeletedAt: DateTime.UtcNow);

        await AppendEventAsync(identity, EventTypes.ArticleDelete, articleId, lamportTs,
            JsonSerializer.Serialize(payload), transaction: transaction);

        return new RowVersion(lamportTs, identity.NodeId);
    }

    public async Task<RowVersion> LogWhitelistAddAsync(WhitelistEntry entry)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new WhitelistAddPayload(
            NodeId: entry.NodeId,
            DisplayName: entry.DisplayName,
            PublicKeyB64: Convert.ToBase64String(entry.Ed25519PublicKey),
            ApiAddress: entry.ApiAddress,
            CanGenerateEmbeddings: entry.CanGenerateEmbeddings,
            IsSuperadmin: entry.IsSuperadmin,
            TlsSpki: entry.TlsSpki,
            TlsTrust: entry.TlsTrust);

        await AppendEventAsync(identity, EventTypes.WhitelistAdd, null, lamportTs,
            JsonSerializer.Serialize(payload));

        return new RowVersion(lamportTs, identity.NodeId);
    }

    public async Task<RowVersion> LogWhitelistRevokeAsync(Guid nodeId)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new WhitelistRevokePayload(NodeId: nodeId);

        await AppendEventAsync(identity, EventTypes.WhitelistRevoke, null, lamportTs,
            JsonSerializer.Serialize(payload));

        return new RowVersion(lamportTs, identity.NodeId);
    }

    public async Task<RowVersion> LogWhitelistUpdateAsync(Guid nodeId, string? apiAddress, string? displayName, bool? isSuperadmin = null, string? tlsSpki = null, string? tlsTrust = null)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new WhitelistUpdatePayload(NodeId: nodeId, ApiAddress: apiAddress, DisplayName: displayName, IsSuperadmin: isSuperadmin, TlsSpki: tlsSpki, TlsTrust: tlsTrust);

        await AppendEventAsync(identity, EventTypes.WhitelistUpdate, null, lamportTs,
            JsonSerializer.Serialize(payload));

        return new RowVersion(lamportTs, identity.NodeId);
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
            JsonSerializer.Serialize(payload), folder.Path);
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
            JsonSerializer.Serialize(payload), newPath);
    }

    public async Task<RowVersion> LogFolderDeleteAsync(Guid folderId, string path, DateTime deletedAt)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new FolderDeletePayload(
            FolderId: folderId,
            Path: path,
            DeletedAt: deletedAt);

        await AppendEventAsync(identity, EventTypes.FolderDelete, null, lamportTs,
            JsonSerializer.Serialize(payload), path);

        return new RowVersion(lamportTs, identity.NodeId);
    }

    public async Task LogMediaCreateAsync(Media media, byte[] ciphertext, IDbTransaction? transaction = null)
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
            CiphertextB64: null,
            IvB64: Convert.ToBase64String(media.IV),
            EncryptedDekB64: Convert.ToBase64String(media.EncryptedDek),
            DekIvB64: Convert.ToBase64String(media.DekIV),
            CreatedAt: media.CreatedAt,
            Kind: media.Kind,
            CiphertextSha256: await EnsureBlobAsync(ciphertext, transaction));

        await AppendEventAsync(identity, EventTypes.MediaCreate, null, lamportTs,
            JsonSerializer.Serialize(payload), transaction: transaction);
    }

    public async Task LogMediaDeleteAsync(Guid mediaId, IDbTransaction? transaction = null)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new MediaDeletePayload(MediaId: mediaId, DeletedAt: DateTime.UtcNow);

        await AppendEventAsync(identity, EventTypes.MediaDelete, null, lamportTs,
            JsonSerializer.Serialize(payload), transaction: transaction);
    }

    public async Task LogConceptTagRenameAsync(string oldName, string newName)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new ConceptTagRenamePayload(OldName: oldName, NewName: newName);

        await AppendEventAsync(identity, EventTypes.ConceptTagRename, Guid.Empty, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogConceptTagMergeAsync(string source, string target)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new ConceptTagMergePayload(Source: source, Target: target);

        await AppendEventAsync(identity, EventTypes.ConceptTagMerge, Guid.Empty, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogConceptTagDeleteAsync(string name)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new ConceptTagDeletePayload(Name: name);

        await AppendEventAsync(identity, EventTypes.ConceptTagDelete, Guid.Empty, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogMediaLinkAsync(Guid mediaId, Guid articleId, long lamportTs)
    {
        var identity = await nodeRepo.GetAsync();
        if (identity == null) return;
        var payload = new MediaLinkEventPayload(mediaId, articleId);
        await AppendEventAsync(identity, EventTypes.MediaLink, articleId, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogHardDeleteAsync(string entityType, string entityIdentifier)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new HardDeleteEventPayload(
            EntityType: entityType,
            EntityIdentifier: entityIdentifier,
            DeletedAt: DateTime.UtcNow);

        await AppendEventAsync(identity, EventTypes.HardDelete, null, lamportTs,
            JsonSerializer.Serialize(payload), entityIdentifier);
    }

    /// <param name="changedAt">
    /// The instant the caller recorded as this node's own last password change. Passed in rather
    /// than read from the clock here so the two agree exactly: the receiving applier drops a
    /// notice whose ChangedAt is at or before the recipient's own last change, and a second
    /// UtcNow would make this node's broadcast strictly later than what it stored about itself.
    /// </param>
    public async Task LogMasterPasswordChangedAsync(DateTime changedAt)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new MasterPasswordChangedPayload(
            ChangedAt: changedAt,
            NodeName: identity.DisplayName);

        await AppendEventAsync(identity, EventTypes.MasterPasswordChanged, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    public async Task LogSnapshotCheckpointAsync(long cpSeq, int eventsRemoved, string snapshotFileName, string snapshotSha256, string? prevCheckpointSha256, DateTime producedAt)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var lamportTs = clock.Tick();
        var payload = new SnapshotCheckpointPayload(
            CpSeq: cpSeq,
            EventsRemoved: eventsRemoved,
            SnapshotFileName: snapshotFileName,
            SnapshotSha256: snapshotSha256,
            PrevCheckpointSha256: prevCheckpointSha256,
            ProducedAt: producedAt);

        await AppendEventAsync(identity, EventTypes.SnapshotCheckpoint, null, lamportTs,
            JsonSerializer.Serialize(payload));
    }

    private async Task AppendEventAsync(
        NodeIdentity identity,
        string eventType,
        Guid? articleId,
        long lamportTs,
        string payloadJson,
        string? entityId = null,
        IDbTransaction? transaction = null)
    {
        var now = DateTime.UtcNow;
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = identity.NodeId,
            LamportTs = lamportTs,
            EventType = eventType,
            ArticleId = articleId,
            // Derived, never supplied: the same rule the receiving side applies in EventApplier,
            // so what we write locally and what a peer reconstructs from the signed fields cannot
            // drift apart. The entityId parameter stays only as the caller's declaration of intent
            // — it is cross-checked below.
            EntityId = EventEntityId.Derive(eventType, articleId, payloadJson),
            Payload = payloadJson,
            Signature = [],
            // What this node declares: 3 until it applied the metadata activation, 4 after (control events stay clear).
            ProtocolVersion = protocolPolicy?.Declared ?? SyncProtocolVersion.Current,
            CreatedAt = now,
            ActorType = actorProvider.ActorType,
            ActorName = actorProvider.ActorName,
            ViaAgentName = actorProvider.ViaAgentName
        };

        // A caller that names an entity the payload does not is writing an event whose identity a
        // peer will reconstruct differently — the hard-delete gate would then key on one value here
        // and another there. That is a bug in the caller, and a silent one, so refuse it at the
        // source rather than shipping an event nobody can agree about.
        if (entityId != null && entityId != evt.EntityId)
            throw new InvalidOperationException(
                $"Event of type {eventType} was given entity id '{entityId}', but its signed fields " +
                $"derive '{evt.EntityId ?? "(none)"}'. Put the identifier in the payload instead.");

        // The source half of the authorship ban (plan 3.2; EventApplier enforces the receiving
        // half): a blind node authors nothing. Refusing here keeps a stray local write on a blind
        // node from entering its log, where every peer that pulls it would reject it again. This
        // is also why event signing has no v=2 (external key) branch — only blind nodes have one.
        if (BlindNodeId.IsBlind(identity.NodeId))
            throw new InvalidOperationException(
                $"Refusing to log a {eventType} event: this is a blind node, and blind nodes never author events.");

        // After activation a content event travels sealed (spec 3.9): the event just built is the inner one, the row
        // written and signed is the outer `sealed` event. This node's own activity views keep the inner type and article
        // as local columns, the entity id and actor columns as before; none of them is ever served.
        var written = evt;
        EventRowLocal? local = null;
        // Sealing and appending are one step as far as a DEK rotation is concerned (spec 4.4): the rotation cannot commit between
        // them, or the row would be appended under a generation the rotation retired. Reentrant when the caller's operation holds it.
        IDisposable? authoring = null;
        try
        {
            if (protocolPolicy is { SealsMetadata: true } && EventClasses.IsSealable(eventType))
            {
                authoring = await BeginAuthoringAsync();
                written = Seal(evt);
                local = new EventRowLocal(LocalType: evt.EventType, LocalArticleId: evt.ArticleId);
            }

            var sigPayload = EventSignature.BuildPayload(written);
            if (identity.Ed25519PrivateKeyV == 0)
            {
                // Legacy v=0 row (plaintext seed) — sign without needing master DEK.
                written.Signature = NodeIdentityVault.SignWithIdentity(
                    identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                    identity.NodeId, Array.Empty<byte>(), sigPayload);
            }
            else
            {
                var masterDek = session.GetMasterDek();
                try
                {
                    written.Signature = NodeIdentityVault.SignWithIdentity(
                        identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                        identity.NodeId, masterDek, sigPayload);
                }
                finally
                {
                    Array.Clear(masterDek);
                }
            }

            await eventLogRepo.AppendAsync(written, transaction, local);
            if (transaction == null)
            {
                syncTrigger.Signal();
            }
        }
        finally
        {
            authoring?.Dispose();
        }
    }

    /// <summary>
    /// The outer event of a content event (spec 3.9), sealed with the current key (spec 4.8). No key this node can open:
    /// the write fails. Clear content is never written after activation.
    /// </summary>
    private SyncEvent Seal(SyncEvent inner)
    {
        if (keyRing?.Current is not { } current)
            throw new InvalidOperationException(
                $"Refusing to log a {inner.EventType} event: metadata is encrypted on this network and this node holds no metadata key it can open now.");
        var eventKey = keyRing.DeriveKey(current.KeyId, MetadataKeyCrypto.EventKeyInfo, ReadOnlySpan<byte>.Empty);
        try
        {
            var outer = MetadataEnvelope.Seal(inner, current.KeyId, eventKey);
            outer.EntityId = inner.EntityId;
            outer.ActorType = inner.ActorType;
            outer.ActorName = inner.ActorName;
            outer.ViaAgentName = inner.ViaAgentName;
            return outer;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(eventKey);
        }
    }
}
