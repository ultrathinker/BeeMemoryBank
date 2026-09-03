using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// L7 (second half): FolderService.EnsureNoRemoteDescendantsAsync must check the TRUE, unfiltered
/// folder set, not the caller's ACL-filtered view.
///
/// <para>
/// GetAllActiveAsync applies the ambient scope's FilterFolders. A remote-mirror descendant hidden
/// from the acting caller by a deny rule (a perfectly ordinary "everything except this" ACL) used
/// to be invisible to this guard entirely -- so a rename/move on an ancestor proceeded, and
/// RenamePathAsync's own ACL checks only ever look at the TOP-level old/new path, not each
/// descendant, so nothing else caught it either. The rename silently rewrote the hidden mirror's
/// path underneath the caller's back and desynced its stored MountPath from reality, without ever
/// throwing -- the exact "corrupt or orphan a subscription the caller merely cannot see" failure
/// mode this guard exists to prevent.
/// </para>
/// </summary>
public class FolderRemoteMirrorGuardAclTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    private static HashSet<string> Set(params string[] paths) =>
        new(paths, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task RenamingAnAncestor_OfAHiddenRemoteMirror_ThrowsInsteadOfSilentlyCorruptingIt()
    {
        ScopeHolder.Scope = SystemCallerScope.Instance;
        var work = await FolderService.CreateAsync("/Work");
        // A remote-mirror mount directly under /Work -- created via the repository (not
        // FolderService, which refuses to create ordinary folders with a subscription id set)
        // to simulate what a real remote-mirror subscription leaves behind.
        await FolderRepo.CreateAsync(new Folder
        {
            Id = Guid.NewGuid(),
            Path = "/Work/Secret",
            Name = "Secret",
            ParentPath = "/Work",
            Status = "A",
            RemoteSubscriptionId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        // An ordinary "everything except this" caller: /Work/Secret is denied (and therefore
        // invisible to a naive ACL-filtered folder listing), but /Work itself is not.
        ScopeHolder.Scope = new HttpCallerScope(
            isSuperadmin: false, denyPaths: Set("/Work/Secret"), allowPaths: Set());

        var act = async () => await FolderService.RenameAsync(work.Id, "Renamed");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*remote-mirror*");

        // Nothing may have moved -- neither /Work nor the hidden mirror descendant.
        ScopeHolder.Scope = SystemCallerScope.Instance;
        (await FolderRepo.GetByPathAsync("/Work")).Should().NotBeNull();
        (await FolderRepo.GetByPathAsync("/Work/Secret")).Should().NotBeNull("the mirror's path must not have been silently rewritten");
    }
}
