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
}
