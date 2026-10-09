using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public partial class EventApplier
{
    private async Task ApplyConceptTagRenameAsync(SyncEvent evt)
    {
        var p = Deserialize<ConceptTagRenamePayload>(evt.Payload);
        try
        {
            await conceptTagRepo.RenameAsync(p.OldName, p.NewName);

            try
            {
                // Concept-tag matching is symmetric similarity, not asymmetric retrieval -- see
                // ConceptTagService's identical GenerateQuery usage for why. The stored version
                // must be the real active model version, not a stale placeholder: it's compared
                // against IEmbeddingGenerator.Version to detect embeddings from a since-replaced
                // model (e.g. after a model swap) and flag them for re-generation.
                var embedding = embeddingGenerator.GenerateQuery(p.NewName);
                var bytes = new byte[embedding.Length * 4];
                Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
                await conceptTagRepo.UpdateEmbeddingAsync(p.NewName, bytes, embeddingGenerator.Version);
            }
            catch (Exception ex)
            {
                // Tag names stay out of these lines (the tag name IS the identifier for tags, and
                // the log runs on blind nodes too) — the event id names the event instead. The
                // exception object stays out as well: a provider renders its message after the
                // template, and the generator's and repository's messages carry the tag name.
                // The type alone says which leg failed.
                logger.LogWarning("Event {EventId}: failed to regenerate the embedding of a renamed concept tag ({ExceptionType})",
                    evt.EventId, ex.GetType().Name);
            }
        }
        catch (InvalidOperationException)
        {
            // The exception is dropped deliberately: the repository's message is
            // "Concept tag '<name>' not found" — the name must not reach the logger, whose
            // providers render attached exceptions in plaintext.
            logger.LogWarning("Event {EventId}: skipping concept_tag_rename, the old concept was not found or already renamed", evt.EventId);
        }
    }

    private async Task ApplyConceptTagMergeAsync(SyncEvent evt)
    {
        var p = Deserialize<ConceptTagMergePayload>(evt.Payload);
        try
        {
            await conceptTagRepo.MergeAsync(p.Source, p.Target);
        }
        catch (InvalidOperationException)
        {
            // Exception dropped: its message names the tag (see the rename catch above).
            logger.LogWarning("Event {EventId}: skipping concept_tag_merge, the source concept was not found or already merged", evt.EventId);
        }
    }

    private async Task ApplyConceptTagDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<ConceptTagDeletePayload>(evt.Payload);
        try
        {
            await conceptTagRepo.DeleteAsync(p.Name);
        }
        catch (InvalidOperationException)
        {
            // Exception dropped: its message names the tag (see the rename catch above).
            logger.LogWarning("Event {EventId}: skipping concept_tag_delete, the concept was not found or already deleted", evt.EventId);
        }
    }

    private async Task ApplyMediaLinkAsync(SyncEvent evt)
    {
        var p = Deserialize<MediaLinkEventPayload>(evt.Payload);
        var existing = await mediaRepo.GetByIdAsync(p.MediaId, includeDeleted: true);
        if (existing == null) return;
        if (existing.ArticleId != null) return;
        // Symmetric with ArticleService.LinkOrphanMediaAsync: never attach media to a protected
        // article, even via a peer's media_link event. Media is master-DEK-wrapped, not passphrase-
        // wrapped, so linking it to a second-layer-protected article would expose it without the
        // passphrase. A peer that links anyway (bug or malice) must not undermine that guarantee here.
        var article = await articleRepo.GetByIdAsync(p.ArticleId, includeDeleted: true);
        if (article is { Protected: true }) return;
        if (!ConflictResolver.IncomingWins(
                RowVersion.Of(existing.LamportTs, existing.SourceNodeId),
                new RowVersion(evt.LamportTs, evt.NodeId)))
            return;
        await mediaRepo.LinkOrphansToArticleAsync(new[] { p.MediaId }, p.ArticleId, evt.LamportTs, evt.NodeId);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidDataException($"Failed to deserialize payload as {typeof(T).Name}");

    private async Task<EncryptedArticleBody> PayloadToBodyAsync(Guid articleId, ArticleEventPayload p) =>
        new()
        {
            ArticleId = articleId,
            Ciphertext = await ResolveCiphertextAsync(p.CiphertextB64, p.CiphertextSha256),
            IV = Convert.FromBase64String(p.IvB64),
            EncryptedDek = Convert.FromBase64String(p.EncryptedDekB64),
            DekIV = Convert.FromBase64String(p.DekIvB64)
        };

    /// <summary>
    /// The ciphertext an article or media payload refers to. Inline base64 (protocol 1) wins when
    /// present — those bytes are covered by the event signature directly. Otherwise the hash is
    /// looked up in the local blob store, which the transport filled before this event was handed
    /// over (pusher ships blobs first; puller fetches them first). A miss is therefore transient
    /// or a bug, never a normal state: it is thrown as <see cref="BlobMissingException"/> so the
    /// event is retried next cycle — the pusher re-checks which hashes we lack on every push, so
    /// a blob swept or lost in between is simply sent again — and quarantined only if it keeps
    /// failing, like any other apply error.
    ///
    /// No hash check on the bytes here: BlobRepository stores everything under what it actually
    /// hashes to, so whatever sits at this address IS the content the signed hash committed to.
    /// </summary>
    private async Task<byte[]> ResolveCiphertextAsync(string? inlineB64, string? sha256)
    {
        if (inlineB64 != null) return Convert.FromBase64String(inlineB64);
        if (string.IsNullOrEmpty(sha256))
            throw new InvalidDataException("Payload carries neither ciphertext nor ciphertext_sha256.");
        return await blobRepo.GetAsync(sha256)
            ?? throw new BlobMissingException(sha256);
    }

    /// <summary>
    /// True unless a tree path inside the payload contains a strictly
    /// illegal segment (".." / "." / control chars / NUL). Cosmetic
    /// non-canonical input ("//" or trailing "/") IS allowed through:
    /// dropping it would permanently diverge from peers running
    /// pre-canonicalisation code whose history legitimately contains
    /// such paths. Only event types that carry
    /// user-controlled paths are checked; others pass through.
    /// </summary>
    private static bool IsTreePathPayloadValid(SyncEvent evt)
    {
        try
        {
            switch (evt.EventType)
            {
                case EventTypes.ArticleCreate:
                case EventTypes.ArticleUpdate:
                {
                    var p = JsonSerializer.Deserialize<ArticleEventPayload>(evt.Payload);
                    return !TreePathCanonicalizer.IsIllegal(p?.TreePath);
                }
                case EventTypes.FolderCreate:
                {
                    var p = JsonSerializer.Deserialize<FolderCreatePayload>(evt.Payload);
                    if (p == null) return true;
                    return !TreePathCanonicalizer.IsIllegal(p.Path)
                        && !TreePathCanonicalizer.IsIllegal(p.ParentPath);
                }
                case EventTypes.FolderRename:
                {
                    var p = JsonSerializer.Deserialize<FolderRenamePayload>(evt.Payload);
                    if (p == null) return true;
                    return !TreePathCanonicalizer.IsIllegal(p.OldPath)
                        && !TreePathCanonicalizer.IsIllegal(p.NewPath);
                }
                default:
                    return true;
            }
        }
        catch
        {
            // Bad JSON / shape — let the per-event Deserialize<T> raise its
            // own error; don't double-fail in the validator.
            return true;
        }
    }
}
