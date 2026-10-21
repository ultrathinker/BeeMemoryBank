using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Records local operations to the event log.
/// Implementation is in BeeMemoryBank.Sync; null implementation in Core.
/// </summary>
public interface IEventLogger
{
    /// <summary>
    /// Begins one operation that changes local state and then logs sealed content (an article, comment, folder, media or concept tag
    /// change, a hard delete), and returns its scope; dispose it when the operation, event included, is done. Where the node seals its
    /// metadata (docs/design/step-a-spec.md 4.4) this (1) throws <c>MetadataKeyPublishPendingException</c> BEFORE the caller has changed
    /// anything while the new metadata key of a DEK rotation is not published yet, and (2) keeps that rotation from committing its
    /// DEK swap until the scope ends, so an event sealed for this operation is never appended under a key the rotation retired. The
    /// scope is reentrant for one logical flow (services call each other). Call it as the first statement of the operation and await it
    /// directly: <c>using var authoring = await eventLogger.BeginAuthoringAsync();</c>. Not <c>async</c> in an implementation: see the
    /// remarks of the barrier. The default holds nothing back.
    /// </summary>
    ValueTask<IDisposable> BeginAuthoringAsync() => new(Services.NullAuthoringScope.Instance);

    Task LogCreateAsync(Article article, EncryptedArticleBody body, string[] conceptTags, IDbTransaction? transaction = null);
    Task LogUpdateAsync(Article article, EncryptedArticleBody? body, string[] conceptTags, IDbTransaction? transaction = null);
    /// <summary>
    /// Logs an article delete and returns the <see cref="RowVersion"/> it published — the caller
    /// must stamp that same version onto the row it is deleting.
    ///
    /// <para>
    /// Returned rather than passed in, so there is exactly one place the version is minted. If
    /// the row kept the version of its last EDIT instead, a concurrent peer edit with a lower
    /// Lamport would beat it at the applier's gate and bring the article back here, while the
    /// peer (comparing against our delete EVENT, not our row) deletes it: a permanent, silent
    /// divergence.
    /// </para>
    /// </summary>
    Task<RowVersion> LogDeleteAsync(Guid articleId, IDbTransaction? transaction = null);

    /// <summary>
    /// Wakes the background sync-push loop. When any of the three log methods above is given a
    /// transaction, it deliberately does NOT self-signal (it would fire before the caller's
    /// transaction commits, waking the push loop to look for a row on another connection that
    /// can't see it yet) — the caller must call this explicitly, once, strictly after its own
    /// <c>tx.Commit()</c> succeeds. Given no transaction, the three log methods self-signal and
    /// callers don't need to call this at all.
    /// </summary>
    void SignalSync();
    /// <summary>
    /// Logs a whitelist add/revoke/update and returns the <see cref="RowVersion"/> it published.
    /// The caller stamps that same version onto <c>tbl_whitelist</c>, for the reason spelled out on
    /// <see cref="IWhitelistRepository.RevokeAsync"/>: the row is what a later event is compared
    /// against, so a row carrying some earlier write's version answers that comparison wrongly.
    /// </summary>
    /// <param name="eventId">
    /// The id the event is written under, or null for a new one. A caller whose write is a step of something that can be interrupted (an invitation being
    /// confirmed) names the event in advance, so that after a crash it can ask the log "did I get as far as this event?" instead of logging a second one.
    /// </param>
    Task<RowVersion> LogWhitelistAddAsync(WhitelistEntry entry, Guid? eventId = null);

    /// <inheritdoc cref="LogWhitelistAddAsync"/>
    Task<RowVersion> LogWhitelistRevokeAsync(Guid nodeId);

    /// <inheritdoc cref="LogWhitelistAddAsync"/>
    /// <param name="tlsSpki">Null = unchanged; "" = remove the pin (for an older node, see <c>WhitelistUpdatePayload</c>).</param>
    /// <param name="tlsTrust">Null = unchanged; <see cref="BlindTrust"/> value to set the mode, <see cref="BlindTrust.None"/> to clear it.</param>
    Task<RowVersion> LogWhitelistUpdateAsync(Guid nodeId, string? apiAddress, string? displayName, bool? isSuperadmin = null, string? tlsSpki = null, string? tlsTrust = null);
    Task LogCommentCreateAsync(Comment comment);
    Task LogCommentDeleteAsync(Guid commentId);
    Task LogFolderCreateAsync(Folder folder);
    Task LogFolderRenameAsync(Guid folderId, string oldPath, string newPath, string newName, string? newParentPath, long lamportTs, DateTime updatedAt);
    /// <summary>
    /// Logs a folder delete and returns the version it published, for the same reason as
    /// <see cref="LogDeleteAsync"/>: the row must end up carrying the version its peers were told.
    /// </summary>
    Task<RowVersion> LogFolderDeleteAsync(Guid folderId, string path, DateTime deletedAt);
    Task LogMediaCreateAsync(Media media, byte[] ciphertext, IDbTransaction? transaction = null);
    Task LogMediaDeleteAsync(Guid mediaId, IDbTransaction? transaction = null);
    Task LogConceptTagRenameAsync(string oldName, string newName);
    Task LogConceptTagMergeAsync(string source, string target);
    Task LogConceptTagDeleteAsync(string name);
    Task LogMediaLinkAsync(Guid mediaId, Guid articleId, long lamportTs);
    Task LogHardDeleteAsync(string entityType, string entityIdentifier);
    Task LogSnapshotCheckpointAsync(long cpSeq, int eventsRemoved, string snapshotFileName, string snapshotSha256, string? prevCheckpointSha256, DateTime producedAt);

    /// <summary>
    /// Tells peers the master password changed here, so they can say they are out of step. Carries
    /// no key material — see MasterPasswordChangedPayload for why that is the whole design.
    /// </summary>
    Task LogMasterPasswordChangedAsync(DateTime changedAt);
}
