using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// M7: FolderService.RenameAsync/MoveAsync must route the assembled tree path through
/// TreePathCanonicalizer instead of hand-rolled Trim('/') normalization, and MoveAsync's
/// self-nesting guard must compare paths the same way (OrdinalIgnoreCase) the SQL descendant
/// rewrite underneath it does.
///
/// <para>
/// Why this matters beyond "ugly path in the DB": a non-canonical path is silently REJECTED by
/// peers (see EventApplier.cs), so an operation that stores one succeeds locally while every peer
/// discards the resulting sync event -- the mesh permanently diverges with nothing but a warning
/// in a log to show for it. And a case-sensitive self-nesting check that disagrees with the
/// case-insensitive SQL LIKE beneath it lets a caller nest a folder inside its own descendant,
/// corrupting the tree.
/// </para>
/// </summary>
public class FolderPathCanonicalizationTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
        ScopeHolder.Scope = SystemCallerScope.Instance;
    }

    [Fact]
    public async Task MoveAsync_RejectsPathTraversalInNewParentPath()
    {
        var item = await FolderService.CreateAsync("/Item");

        var act = async () => await FolderService.MoveAsync(item.Id, "/Foo/../../Evil");
        await act.Should().ThrowAsync<ArgumentException>();

        // Nothing may have moved or been created at the traversal target.
        (await FolderRepo.GetByPathAsync("/Item")).Should().NotBeNull();
        (await FolderRepo.GetByPathAsync("/Evil")).Should().BeNull();
    }

    [Fact]
    public async Task MoveAsync_CollapsesDoubleSlashesInNewParentPath()
    {
        await FolderService.CreateAsync("/Archive");
        var item = await FolderService.CreateAsync("/Item");

        await FolderService.MoveAsync(item.Id, "//Archive");

        var moved = await FolderRepo.GetByIdAsync(item.Id);
        moved!.Path.Should().Be("/Archive/Item", "the double leading slash must be collapsed, not stored literally");
    }

    /// <summary>
    /// Regression test for the exact bug: before the fix, the self-nesting guard used a
    /// culture-sensitive/case-sensitive StartsWith while RenamePathAsync's descendant rewrite
    /// matches via SQLite's default case-insensitive LIKE. A differently-cased target let a
    /// caller move a folder into its own descendant, which the SQL then rewrote as a cycle.
    /// </summary>
    [Fact]
    public async Task MoveAsync_SelfNestingGuard_IsCaseInsensitive()
    {
        var work = await FolderService.CreateAsync("/Work");
        await FolderService.CreateAsync("/Work/Sub");

        var act = async () => await FolderService.MoveAsync(work.Id, "/WORK/Sub");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*into itself*");

        // The tree must be exactly as it was -- no corruption from a partially-applied move.
        var reloadedWork = await FolderRepo.GetByIdAsync(work.Id);
        reloadedWork!.Path.Should().Be("/Work");
        (await FolderRepo.GetByPathAsync("/Work/Sub")).Should().NotBeNull();
    }

    [Fact]
    public async Task RenameAsync_RejectsControlCharactersInNewName()
    {
        var folder = await FolderService.CreateAsync("/Foo");

        // BEL (0x07) is a control character -- rejected only because the assembled path is now
        // routed through TreePathCanonicalizer. Before the fix, RenameAsync's hand-rolled checks
        // only rejected '/', '\', ".", and "..", so a control character in newName would have been
        // written straight into tbl_folder.path. Built at runtime (not as a literal in this file)
        // to keep a raw control byte out of the source.
        var nameWithControlChar = "Evil" + (char)7 + "Name";

        var act = async () => await FolderService.RenameAsync(folder.Id, nameWithControlChar);
        await act.Should().ThrowAsync<ArgumentException>();

        (await FolderRepo.GetByIdAsync(folder.Id))!.Path.Should().Be("/Foo", "a rejected rename must not partially apply");
    }

    [Fact]
    public async Task RenameAsync_ProducesACanonicalPathAtRoot()
    {
        var folder = await FolderService.CreateAsync("/Foo");

        await FolderService.RenameAsync(folder.Id, "Bar");

        var renamed = await FolderRepo.GetByIdAsync(folder.Id);
        renamed!.Path.Should().Be("/Bar");
    }
}
