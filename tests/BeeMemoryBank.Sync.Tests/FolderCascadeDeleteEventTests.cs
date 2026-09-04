using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Deleting a folder cascades locally to every subfolder, but the cascade is a bulk UPDATE that
/// writes no events, and <c>EventApplier.ApplyFolderDeleteAsync</c> only ever acts on the single
/// folder id carried by the event it is given.
///
/// <para>
/// So logging only the top folder meant a peer deleted <c>/Work</c> and left <c>/Work/Reports</c>
/// alive — with its articles still attached to it — permanently. Nothing detects or repairs that;
/// the two nodes simply disagree about the shape of the tree from then on, and no later edit
/// reconciles it because no event describing the subfolder's deletion was ever created.
/// </para>
/// </summary>
public class FolderCascadeDeleteEventTests : SyncTestFixture
{
    private FolderService BuildFolderService()
    {
        var scopeHolder = new CallerScopeHolder();
        var folderRepo = new FolderRepository(Factory, scopeHolder);
        var userRepo = new UserRepository(Factory);
        var folderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => Factory)
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(Factory))
            .AddScoped<IRoleRepository>(_ => new RoleRepository(Factory))
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(Factory))
            .AddScoped<IUserRepository>(_ => userRepo)
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .AddScoped(_ => scopeHolder)
            .BuildServiceProvider());

        return new FolderService(folderRepo, ArticleRepo, NodeRepo, Clock, EventLogger, folderAccess, scopeHolder);
    }

    private async Task<List<string>> DeletedFolderPathsFromEventLogAsync()
    {
        var events = await EventLogRepo.GetAfterSequenceAsync(0, 1000);
        return events
            .Where(e => e.EventType == EventTypes.FolderDelete)
            .Select(e => System.Text.Json.JsonSerializer.Deserialize<FolderDeletePayload>(
                e.Payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Path)
            .ToList();
    }

    [Fact]
    public async Task DeletingAFolder_LogsADeleteEventForEverySubfolderInTheCascade()
    {
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        var folderSvc = BuildFolderService();
        var work = await folderSvc.CreateAsync("/Work");
        await folderSvc.CreateAsync("/Work/Reports");
        await folderSvc.CreateAsync("/Work/Reports/2026");

        await folderSvc.DeleteAsync(work.Id);

        var deletedPaths = await DeletedFolderPathsFromEventLogAsync();

        // Every folder that went down locally must be described by an event, or the peer applying
        // this batch keeps the subfolders alive forever.
        deletedPaths.Should().Contain("/Work");
        deletedPaths.Should().Contain("/Work/Reports",
            "a subfolder deleted by the cascade must be announced to peers, not just deleted locally");
        deletedPaths.Should().Contain("/Work/Reports/2026",
            "the cascade is recursive, so every depth must be announced");
    }

    [Fact]
    public async Task DeletingALeafFolder_LogsExactlyOneEvent()
    {
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        var folderSvc = BuildFolderService();
        var leaf = await folderSvc.CreateAsync("/Standalone");

        await folderSvc.DeleteAsync(leaf.Id);

        var deletedPaths = await DeletedFolderPathsFromEventLogAsync();
        deletedPaths.Should().BeEquivalentTo(["/Standalone"],
            "a folder with no descendants must not produce extra events");
    }

    /// <summary>
    /// Announcing the cascade is only half of it: every row it took down must also END UP carrying
    /// the version of the event that announced it.
    ///
    /// <para>
    /// The bulk UPDATE that performs the cascade cannot write those versions — it runs before any
    /// event exists, and each folder gets its own Lamport tick. So each row is stamped after its
    /// event is written. Without that, a cascaded folder keeps whatever version its last RENAME
    /// left behind, and the applier's already-deleted branch — which compares an incoming delete
    /// against tbl_folder.lamport_ts — answers with a number describing an unrelated write at an
    /// unrelated time. Two nodes then disagree about which delete the row is attributed to, and a
    /// later event comparing against it gets a different answer on each of them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CascadeDelete_StampsEveryRowWithItsOwnEventsVersion()
    {
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        var folderSvc = BuildFolderService();
        var work = await folderSvc.CreateAsync("/Work");
        var reports = await folderSvc.CreateAsync("/Work/Reports");
        var y2026 = await folderSvc.CreateAsync("/Work/Reports/2026");

        // Renaming first gives every row a pre-existing version, so "still zero" cannot pass for
        // "correctly stamped" — the stale value the bug left behind was a real number, not a null.
        await folderSvc.RenameAsync(y2026.Id, "2026-archive");

        await folderSvc.DeleteAsync(work.Id);

        var identity = (await NodeRepo.GetAsync())!;
        var folderRepo = new FolderRepository(Factory, new CallerScopeHolder());

        // The Lamport each folder's own delete event carried, keyed by folder id.
        var events = await EventLogRepo.GetAfterSequenceAsync(0, 1000);
        var announced = events
            .Where(e => e.EventType == EventTypes.FolderDelete)
            .ToDictionary(
                e => System.Text.Json.JsonSerializer.Deserialize<FolderDeletePayload>(
                    e.Payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.FolderId,
                e => e.LamportTs);

        foreach (var id in new[] { work.Id, reports.Id, y2026.Id })
        {
            var row = (await folderRepo.GetByIdAsync(id, includeDeleted: true))!;
            row.Status.Should().Be("D");
            row.LamportTs.Should().Be(announced[id],
                "the row must carry the Lamport of the delete event that announced it, not the one its last rename left behind");
            row.SourceNodeId.Should().Be(identity.NodeId,
                "the row must be attributed to the node that deleted it");
        }
    }
}
