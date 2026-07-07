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
                var embedding = embeddingGenerator.Generate(p.NewName);
                var bytes = new byte[embedding.Length * 4];
                Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
                await conceptTagRepo.UpdateEmbeddingAsync(p.NewName, bytes, "hash-v1");
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

    private static EncryptedArticleBody PayloadToBody(Guid articleId, ArticleEventPayload p) =>
        new()
        {
            ArticleId = articleId,
            Ciphertext = Convert.FromBase64String(p.CiphertextB64),
            IV = Convert.FromBase64String(p.IvB64),
            EncryptedDek = Convert.FromBase64String(p.EncryptedDekB64),
            DekIV = Convert.FromBase64String(p.DekIvB64)
        };

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
