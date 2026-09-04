using System.Text.Json;
using BeeMemoryBank.Core.Embeddings;
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
                // against OnnxEmbeddingGenerator.Version to detect embeddings from a since-replaced
                // model (e.g. after a model swap) and flag them for re-generation.
                var embedding = embeddingGenerator.GenerateQuery(p.NewName);
                var bytes = new byte[embedding.Length * 4];
                Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
                await conceptTagRepo.UpdateEmbeddingAsync(p.NewName, bytes, OnnxEmbeddingGenerator.Version);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to regenerate embedding for renamed concept tag '{NewName}'", p.NewName);
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Skipping concept_tag_rename: concept '{OldName}' not found or already renamed", p.OldName);
        }
    }

    private async Task ApplyConceptTagMergeAsync(SyncEvent evt)
    {
        var p = Deserialize<ConceptTagMergePayload>(evt.Payload);
        try
        {
            await conceptTagRepo.MergeAsync(p.Source, p.Target);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Skipping concept_tag_merge: source '{Source}' not found or already merged", p.Source);
        }
    }

    private async Task ApplyConceptTagDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<ConceptTagDeletePayload>(evt.Payload);
        try
        {
            await conceptTagRepo.DeleteAsync(p.Name);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Skipping concept_tag_delete: concept '{Name}' not found or already deleted", p.Name);
        }
    }

    private async Task ApplyMediaLinkAsync(SyncEvent evt)
    {
        var p = Deserialize<MediaLinkEventPayload>(evt.Payload);
        var existing = await mediaRepo.GetByIdAsync(p.MediaId, includeDeleted: true);
        if (existing == null) return;
        if (existing.ArticleId != null) return;
        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
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
    /// such paths (gemini review feedback). Only event types that carry
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
