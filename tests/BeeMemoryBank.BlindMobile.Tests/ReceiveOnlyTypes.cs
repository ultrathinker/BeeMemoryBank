using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The vault-shaped types the blind app composes ONLY because the receive path needs them to be built
/// (BMB-91: Sync still lives with the vault-aware code, so the blind app has to feed EventApplier the
/// replicated-row collaborators it asks for). One row per type: why, and who takes it. Everything in
/// here is registered by <c>BlindMobileServices</c> and must be mentioned by NO other type of the app:
/// not a page, not a platform class, not another service. Exit condition: BMB-91, a receive-only
/// applier in its own project, after which this table and the exception go away.
/// </summary>
internal static class ReceiveOnlyTypes
{
    /// <param name="ViaLocator">The consumer resolves it from the container by itself, so its
    /// constructor does not show it; the line in <paramref name="Why"/> is the evidence.</param>
    public sealed record Row(Type Service, Type Implementation, Type Consumer, bool ViaLocator, string Why);

    private const string Applier = "EventApplier.cs:17-43 ctor";

    public static readonly IReadOnlyList<Row> Rows =
    [
        new(typeof(IArticleRepository), typeof(ArticleRepository), typeof(EventApplier), false,
            $"{Applier} articleRepo; article create/update/delete events, EventApplier.Article.cs:123,269,355,387"),
        new(typeof(IArticleBodyRepository), typeof(ArticleBodyRepository), typeof(EventApplier), false,
            $"{Applier} bodyRepo; stores the received (still encrypted) body, EventApplier.Article.cs:124,270"),
        new(typeof(IBlobRepository), typeof(BlobRepository), typeof(EventApplier), false,
            $"{Applier} blobRepo; blob rows of media/concept events, EventApplier.ConceptTags.cs:119, EventApplier.Folder.cs:214"),
        new(typeof(ITombstoneRepository), typeof(TombstoneRepository), typeof(EventApplier), false,
            $"{Applier} tombstoneRepo; tombstone lookup before article create/update, EventApplier.Article.cs:52,176"),
        new(typeof(IConflictVersionRepository), typeof(ConflictVersionRepository), typeof(EventApplier), false,
            $"{Applier} conflictRepo; conflict versions of concurrent edits, EventApplier.Article.cs:216,284"),
        new(typeof(ICommentRepository), typeof(CommentRepository), typeof(EventApplier), false,
            $"{Applier} commentRepo; comment rows, EventApplier.Whitelist.cs:238,261"),
        new(typeof(IFolderRepository), typeof(FolderRepository), typeof(EventApplier), false,
            $"{Applier} folderRepo; folder lookup, EventApplier.Article.cs:158,160"),
        new(typeof(IMediaRepository), typeof(MediaRepository), typeof(EventApplier), false,
            $"{Applier} mediaRepo; media rows, EventApplier.ConceptTags.cs:71,84"),
        new(typeof(IConceptTagRepository), typeof(ConceptTagRepository), typeof(EventApplier), false,
            $"{Applier} conceptTagRepo; concept-tag rename and embedding, EventApplier.ConceptTags.cs:17,29"),
        new(typeof(IRestoreReplayShieldRepository), typeof(RestoreReplayShieldRepository), typeof(EventApplier), false,
            $"{Applier} replayShieldRepo; replay-shield threshold, EventApplier.cs:175"),
        new(typeof(IRestoreEventStateRepository), typeof(RestoreEventStateRepository), typeof(EventApplier), false,
            $"{Applier} restoreEventStateRepo; restore events, EventApplier.Restore.cs:22,29"),
        new(typeof(IDekRotationStateRepository), typeof(DekRotationStateRepository), typeof(EventApplier), false,
            $"{Applier} dekRotationStateRepo; rotation events, EventApplier.Restore.cs:85,89"),
        new(typeof(ConceptTagService), typeof(ConceptTagService), typeof(EventApplier), false,
            $"{Applier} conceptTagService; EventApplier.Article.cs:109,125"),
        new(typeof(HardDeleteService), typeof(HardDeleteService), typeof(EventApplier), false,
            $"{Applier} hardDeleteService; EventApplier.Article.cs:15 ApplyRemoteAsync"),
        new(typeof(FolderAccessService), typeof(FolderAccessService), typeof(EventApplier), false,
            $"{Applier} folderAccess; cache invalidation, EventApplier.Folder.cs:166"),
        new(typeof(IEventLogger), typeof(NullEventLogger), typeof(HardDeleteService), false,
            "HardDeleteService.cs:13 and ConceptTagService.cs:10 ctor eventLogger; the no-op logger"),
        new(typeof(ISyncPositionRepository), typeof(SyncPositionRepository), typeof(BlindPhonePullClient), true,
            "BlindPhonePullClient.cs:32 receive cursor"),
        new(typeof(ISyncQuarantineRepository), typeof(SyncQuarantineRepository), typeof(BlindPhonePullClient), true,
            "BlindPhonePullClient.cs:41 poison-event quarantine")
    ];

    /// <summary>Full names the boundary scan looks for; the applier itself counts as one.</summary>
    public static readonly IReadOnlySet<string> RestrictedNames = Rows
        .SelectMany(r => new[] { r.Service, r.Implementation })
        .Append(typeof(EventApplier))
        .Select(t => t.FullName!)
        .ToHashSet();
}
