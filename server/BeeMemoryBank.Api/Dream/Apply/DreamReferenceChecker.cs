using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Finds other articles whose text names an article's id before a trash_article advice gets apply_mode code (plan 3.10),
/// again for every trash prompt, and again right before Apply trashes the copy: a link written since the night would
/// otherwise point into the trash.
///
/// <para>It is the existing content search (SearchService.SearchWithContentAsync: titles, tags, ids and every active body,
/// case-insensitive) in the system scope, which is what the run itself uses, so a superadmin's Apply and the night agree
/// on what counts. <see cref="CheckAsync"/> bypasses the shared 30-second result cache (SearchQueryCache has no write
/// invalidation): it runs the same search through a SearchService of its own whose cache is new and empty, so the answer
/// is read from the bodies as they are at that moment. "Unavailable" (Checked false) is never read as "none": the copy then
/// goes to the chat.</para>
/// </summary>
public sealed class DreamReferenceChecker(IServiceProvider services, CallerScopeHolder scopeHolder, SessionService session)
{
    /// <summary>How many referring articles the advice names as samples.</summary>
    public const int MaxSamples = 3;

    /// <summary>A fresh look, never a cached one: for the trash prompt and right before Apply trashes.</summary>
    public async Task<DreamReferences> CheckAsync(Guid articleId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The request's own repositories and scope, the same search code, and a result cache nobody else shares.
        var fresh = ActivatorUtilities.CreateInstance<SearchService>(services, new SearchQueryCache());

        // Elevated only for the search: the caller's scope comes back the moment it returns (the scope store of a request
        // is the request's own, so a hand-written assignment would leak into the rest of it).
        var found = await scopeHolder.RunAsSystemAsync(() => FindAsync(fresh, articleId));

        // Without the key the search reads titles only and would call a body's link "none".
        if (!session.IsUnlocked) found = null;
        return found is null
            ? new DreamReferences(false, 0, [])
            : new DreamReferences(true, found.Count, found.Take(MaxSamples).Select(a => new DreamReferenceSample(a.Id, a.Title)).ToList());
    }

    /// <summary>The count of the corpus (IDreamCorpus.CountReferencesAsync): -1 when the search is unavailable. The night's
    /// count may come from the shared cache; what it decides is checked again, fresh, before anything is trashed.</summary>
    internal static async Task<int> CountAsync(SearchService search, Guid articleId) =>
        (await FindAsync(search, articleId))?.Count ?? -1;

    /// <summary>The other active articles that name <paramref name="articleId"/>, or null when the search failed.</summary>
    private static async Task<IReadOnlyList<Core.Models.Article>?> FindAsync(SearchService search, Guid articleId)
    {
        try
        {
            var found = await search.SearchWithContentAsync(articleId.ToString("D"));
            return found.Articles.Where(a => a.Id != articleId).DistinctBy(a => a.Id).ToList();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SessionLockedException or VaultSealedException))
        {
            return null;
        }
    }
}
