using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Folder auto-vivification must not be a way around folder ACL.
///
/// <para>
/// <c>FolderRepository.EnsureExistsAsync</c> swaps the ambient scope to System so it can create
/// missing ancestor stubs, documenting the assumption that "the leaf creation has already been
/// authorized at the endpoint level". On the app's busiest write path that assumption was false:
/// <c>ArticleService.CreateAsync</c> (and the move branch of <c>UpdateAsync</c>) vivified folders
/// BEFORE <c>articleRepo.CreateAsync</c> applied the caller's ACL, and nothing rolled the folders
/// back when the article write then threw. A denied or read-only caller could therefore persist
/// arbitrary folder names into a subtree they cannot write — plaintext metadata everyone can see,
/// and node-local, since that path emits no FolderCreate event.
/// </para>
/// </summary>
public class FolderVivificationAclTests : TestFixture
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
    public async Task ADeniedCaller_CannotCreateFoldersByWritingAnArticleIntoThem()
    {
        ScopeHolder.Scope = new HttpCallerScope(
            isSuperadmin: false, denyPaths: Set("/Secrets"), allowPaths: Set());

        var act = async () => await ArticleService.CreateAsync(
            "Trespass", "/Secrets/Planted/Deep", [], "body");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        // The point of the test: no folder may survive the refused write.
        ScopeHolder.Scope = SystemCallerScope.Instance;
        (await FolderRepo.GetByPathAsync("/Secrets/Planted")).Should().BeNull();
        (await FolderRepo.GetByPathAsync("/Secrets/Planted/Deep")).Should().BeNull();
    }

    [Fact]
    public async Task AReadOnlyCaller_CannotCreateFoldersByWritingAnArticleIntoThem()
    {
        ScopeHolder.Scope = new HttpCallerScope(
            isSuperadmin: false, denyPaths: Set(), allowPaths: Set("/Public"),
            readOnlyPaths: Set("/Public"));

        var act = async () => await ArticleService.CreateAsync(
            "Trespass", "/Public/Planted", [], "body");

        await act.Should().ThrowAsync<ReadOnlyAccessException>();

        ScopeHolder.Scope = SystemCallerScope.Instance;
        (await FolderRepo.GetByPathAsync("/Public/Planted")).Should().BeNull();
    }

    [Fact]
    public async Task MovingAnArticleIntoADeniedFolder_CreatesNothing()
    {
        ScopeHolder.Scope = SystemCallerScope.Instance;
        var article = await ArticleService.CreateAsync("Movable", "/Open", [], "body");

        ScopeHolder.Scope = new HttpCallerScope(
            isSuperadmin: false, denyPaths: Set("/Secrets"), allowPaths: Set());

        var act = async () => await ArticleService.UpdateAsync(
            article.Id, treePath: "/Secrets/Landing");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        ScopeHolder.Scope = SystemCallerScope.Instance;
        (await FolderRepo.GetByPathAsync("/Secrets/Landing")).Should().BeNull();
    }

    /// <summary>
    /// The guard checks only the requested leaf. An allow-list caller legitimately creates paths
    /// under their subtree whose ANCESTORS are outside their scope — /Work is invisible to a user
    /// allowed only /Work/Project, and blocking on it would break ordinary use.
    /// </summary>
    [Fact]
    public async Task AnAllowListCaller_CanStillCreateDeepPathsInsideTheirOwnSubtree()
    {
        ScopeHolder.Scope = SystemCallerScope.Instance;
        await FolderRepo.EnsureExistsAsync("/Work/Project", null);

        ScopeHolder.Scope = new HttpCallerScope(
            isSuperadmin: false, denyPaths: Set(), allowPaths: Set("/Work/Project"));

        var article = await ArticleService.CreateAsync(
            "Legit", "/Work/Project/Sub/Deeper", [], "body");
        article.TreePath.Should().Be("/Work/Project/Sub/Deeper");

        ScopeHolder.Scope = SystemCallerScope.Instance;
        (await FolderRepo.GetByPathAsync("/Work/Project/Sub/Deeper")).Should().NotBeNull();
    }

    [Fact]
    public async Task SystemScope_IsUnaffected()
    {
        // Sync's EventApplier, the startup FolderBootstrapper and background workers all run
        // under System scope and must keep vivifying anything.
        ScopeHolder.Scope = SystemCallerScope.Instance;
        await FolderRepo.EnsureExistsAsync("/Anywhere/At/All", null);
        (await FolderRepo.GetByPathAsync("/Anywhere/At/All")).Should().NotBeNull();
    }
}
