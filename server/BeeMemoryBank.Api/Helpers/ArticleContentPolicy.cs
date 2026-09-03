using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// The shared "may this caller read this article's CONTENT" gate for <c>bee_get_article</c>,
/// used by BOTH <c>BeeReadTools.GetArticle</c> (MCP) and <c>ChatToolDispatcher.GetArticleAsync</c>
/// (native chat) so the two surfaces cannot silently diverge on this CRITICAL check again.
///
/// <para>Mirrors <c>ArticleEndpoints</c>' <c>GET /{id}/content</c> handler's gate order exactly:
/// <see cref="ArticleService.GetMetadataAsync"/> (scope-filtered by the ambient CallerScope;
/// returns null when the caller's scope denies the article's tree path) -&gt; protected-body check
/// -&gt; session-lock check -&gt; explicit re-check of the caller's folder ACL
/// (<see cref="FolderAccessService.IsAccessDenied"/>) -&gt; decrypt. The explicit re-check is
/// defense-in-depth: <see cref="ArticleService.GetContentAsync"/> itself goes straight to the body
/// repo with NO scope filter, so this gate is the only thing standing between "metadata visible"
/// and "plaintext returned". Calling <c>GetContentAsync</c> without running this gate first MUST
/// NOT be done.</para>
///
/// <para>Before this was extracted, <c>BeeReadTools.GetArticle</c> (MCP) had NEITHER the explicit
/// folder-ACL re-check NOR a structured locked-vault result — it called
/// <c>GetContentAsync</c> straight after the protected-body check and let a locked session surface
/// as a bare <c>"Error: Session is locked."</c> string with no metadata at all, contradicting its
/// own tool description ("Content is withheld when the vault is locked ... each reported as a
/// structured field, not an error"). <c>ChatToolDispatcher.GetArticleAsync</c> already did both
/// correctly. This type is that correct behavior, shared.</para>
/// </summary>
public static class ArticleContentPolicy
{
    public enum Status
    {
        /// <summary>No metadata could be read at all (never existed, or the caller's ambient
        /// CallerScope denies the tree path — indistinguishable from the caller's point of view,
        /// by design).</summary>
        NotFound,
        /// <summary>Content withheld: metadata is visible but the body is second-layer
        /// (passphrase) protected. Only a human in the web/mobile UI can unlock it.</summary>
        Protected,
        /// <summary>Content withheld: metadata is visible but the vault is locked.</summary>
        Locked,
        /// <summary>Content withheld: metadata is visible but the caller's folder ACL denies
        /// reading this article's body.</summary>
        AccessDenied,
        /// <summary>Either metadata-only was requested, or content was requested and is included
        /// in <see cref="Result.Content"/>.</summary>
        Ok
    }

    /// <summary><see cref="Article"/> is non-null for every status except <see cref="Status.NotFound"/>.
    /// <see cref="Content"/> is non-null only for <see cref="Status.Ok"/> when content was
    /// requested.</summary>
    public sealed record Result(Status Status, Article? Article, string? Content);

    public static async Task<Result> ResolveAsync(
        Guid id,
        bool includeContent,
        int? userId,
        int? agentId,
        bool isSuperadmin,
        ArticleService articleService,
        SessionService session,
        FolderAccessService folderAccess)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return new Result(Status.NotFound, null, null);

        if (!includeContent)
            return new Result(Status.Ok, article, null);

        // Protected (second-layer) bodies are opaque end-to-end: no passphrase is ever available
        // to an agent or to the AI chat, so the body must never be attempted.
        if (article.Protected)
            return new Result(Status.Protected, article, null);

        // Defense-in-depth: callers typically already gate on this before reaching here, but a
        // content read must never proceed against a locked session regardless.
        if (!session.IsUnlocked)
            return new Result(Status.Locked, article, null);

        // Folder ACL gate -- mirrors ArticleEndpoints /{id}/content. GetContentAsync goes straight
        // to the body repo with NO scope filter, so without this check any caller who happens to
        // know an article GUID could read its plaintext.
        if (!isSuperadmin)
        {
            var (denyPaths, allowPaths) = await folderAccess.GetAccessInfoAsync(userId, agentId);
            if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, article.TreePath))
                return new Result(Status.AccessDenied, article, null);
        }

        string content;
        try
        {
            content = await articleService.GetContentAsync(id);
        }
        catch (InvalidOperationException)
        {
            // The session raced to locked between the check above and here (e.g. another request
            // called Lock() in between) -- degrade the same as an already-locked vault rather than
            // letting the exception escape.
            return new Result(Status.Locked, article, null);
        }

        return new Result(Status.Ok, article, content);
    }
}
