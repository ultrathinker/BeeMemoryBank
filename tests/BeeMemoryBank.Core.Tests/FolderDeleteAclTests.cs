using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// H1: folder delete must not relocate ACL-denied descendant articles to '/', nor soft-delete an
/// ACL-denied descendant folder.
///
/// <para>
/// The concrete scenario this guards against: a caller with allow=/ and deny=/Work/Secret (an
/// ordinary "everything except this" configuration) deletes /Work. The deny check on /Work itself
/// passes -- the caller IS authorized on /Work -- but before this fix the cascade underneath
/// (<c>FolderService.DeleteAsync</c> -&gt; <c>ArticleRepository.ClearFolderIdAsync</c>, which
/// carries no ACL check of its own) relocated every article in /Work/Secret to '/' and
/// soft-deleted the /Work/Secret folder row, without ever re-checking the caller's ACL against
/// that descendant. Articles that were supposed to stay hidden under /Work/Secret became readable
/// at '/' by anyone with root access, and the denied subtree was destroyed by a caller who was
/// never authorized to touch it.
/// </para>
/// </summary>
public class FolderDeleteAclTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    private static HashSet<string> Set(params string[] paths) =>
        new(paths, StringComparer.OrdinalIgnoreCase);

    private async Task<(Guid WorkFolderId, Guid SecretArticleId)> SeedWorkWithDeniedSecretAsync()
    {
        ScopeHolder.Scope = SystemCallerScope.Instance;
        var work = await FolderService.CreateAsync("/Work");
        await FolderService.CreateAsync("/Work/Secret");
        var article = await ArticleService.CreateAsync("Confidential", "/Work/Secret", [], "top secret body");
        return (work.Id, article.Id);
    }

    [Fact]
    public async Task DeletingASubtreeWithADeniedDescendant_ThrowsAndChangesNothing()
    {
        var (workId, articleId) = await SeedWorkWithDeniedSecretAsync();

        // "allow=/, deny=/Work/Secret" -- empty allowPaths means "no restrictions except the deny
        // list", the same effective policy as an explicit allow="/" row.
        ScopeHolder.Scope = new HttpCallerScope(
            isSuperadmin: false, denyPaths: Set("/Work/Secret"), allowPaths: Set());

        var act = async () => await FolderService.DeleteAsync(workId);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        // Nothing may have moved: the denied subtree must still exist, untouched, and the article
        // must still live at its original (denied) path -- not relocated to '/'.
        ScopeHolder.Scope = SystemCallerScope.Instance;
        var work = await FolderRepo.GetByIdAsync(workId);
        work.Should().NotBeNull();
        work!.Status.Should().Be("A");

        var secret = await FolderRepo.GetByPathAsync("/Work/Secret");
        secret.Should().NotBeNull("the denied descendant folder must not be soft-deleted");
        secret!.Status.Should().Be("A");

        var article = await ArticleService.GetMetadataAsync(articleId);
        article.Should().NotBeNull();
        article!.TreePath.Should().Be("/Work/Secret", "the article must not be relocated to '/'");
        article.FolderId.Should().Be(secret.Id);
    }

    [Fact]
    public async Task DeletingASubtreeWithAReadOnlyDescendant_ThrowsReadOnlyAccessException()
    {
        var (workId, articleId) = await SeedWorkWithDeniedSecretAsync();

        ScopeHolder.Scope = new HttpCallerScope(
            isSuperadmin: false, denyPaths: Set(), allowPaths: Set("/"),
            readOnlyPaths: Set("/Work/Secret"));

        var act = async () => await FolderService.DeleteAsync(workId);
        await act.Should().ThrowAsync<ReadOnlyAccessException>();

        ScopeHolder.Scope = SystemCallerScope.Instance;
        (await FolderRepo.GetByPathAsync("/Work/Secret")).Should().NotBeNull();
        var article = await ArticleService.GetMetadataAsync(articleId);
        article!.TreePath.Should().Be("/Work/Secret");
    }

    /// <summary>
    /// Regression guard for the H1 fix itself: a caller with no restricted descendants must still
    /// be able to delete a subtree normally, and the cascade must still relocate/soft-delete
    /// exactly as before.
    /// </summary>
    [Fact]
    public async Task DeletingASubtreeWithNoRestrictedDescendants_StillCascadesNormally()
    {
        var (workId, articleId) = await SeedWorkWithDeniedSecretAsync();

        ScopeHolder.Scope = SystemCallerScope.Instance;
        await FolderService.DeleteAsync(workId);

        var work = await FolderRepo.GetByIdAsync(workId, includeDeleted: true);
        work!.Status.Should().Be("D");

        var secret = await FolderRepo.GetByPathAsync("/Work/Secret");
        secret.Should().BeNull("the descendant folder must still be soft-deleted when nothing is denied");

        var article = await ArticleService.GetMetadataAsync(articleId);
        article!.TreePath.Should().Be("/", "articles under a deleted subtree are still relocated to '/' when authorized");
        article.FolderId.Should().BeNull();
    }
}
