// Per-user favorites ("starred" articles). Node-local, like the users they belong to —
// nothing here is synced to other nodes.

using System.Globalization;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Api.Endpoints;

public static class FavoriteEndpoints
{
    /// <summary>
    /// Titles are sorted with a case-insensitive invariant comparison rather than SQL's ORDER BY:
    /// SQLite's COLLATE NOCASE only case-folds ASCII, so Cyrillic titles (most of this vault)
    /// would sort by raw code point and look broken next to the Latin ones.
    /// </summary>
    private static readonly StringComparer TitleComparer =
        StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);

    public static void MapFavoriteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/favorites").WithTags("Favorites").RequireInternalKey();
        // Favorites are a human, per-user concept; an agent bearer token has no business here —
        // its owner's stars are not the agent's, and the sidebar is the only consumer.
        group.AddEndpointFilter<RequireNonAgentFilter>();

        // GET /api/favorites — the caller's starred articles, already ordered for display.
        group.MapGet("/", async (HttpContext ctx, IFavoriteRepository favRepo, IArticleRepository articleRepo) =>
        {
            if (CallerUserId(ctx) is not int userId)
                return Results.Json(new ErrorResponse("User identification failed"), statusCode: 403);

            var view = await LoadAsync(userId, favRepo, articleRepo);
            var items = view.Ordered
                .Select(a => new FavoriteItem(a.Id, a.Title, a.TreePath, a.Protected))
                .ToList();
            return Results.Ok(new FavoriteListResponse(items, view.ManualOrder));
        });

        // POST /api/favorites/{articleId} — star an article.
        group.MapPost("/{articleId:guid}", async (
            Guid articleId, HttpContext ctx, IFavoriteRepository favRepo, IArticleRepository articleRepo) =>
        {
            if (CallerUserId(ctx) is not int userId)
                return Results.Json(new ErrorResponse("User identification failed"), statusCode: 403);

            // Scope-filtered lookup: starring an article the caller cannot see must 404 exactly
            // like opening it does, otherwise the star becomes an existence oracle for hidden folders.
            var article = await articleRepo.GetByIdAsync(articleId);
            if (article == null) return Results.NotFound(new ErrorResponse("Article not found"));

            // While the list is alphabetical every row stays unpositioned. Once it is manual, a
            // newly starred article goes on top — it is the thing the user just cared about, and
            // it must not hide at the bottom of a long curated list.
            var view = await LoadAsync(userId, favRepo, articleRepo);
            int? sortOrder = view.ManualOrder ? view.MinVisiblePosition - 1 : null;

            await favRepo.AddAsync(userId, articleId, sortOrder);
            return Results.NoContent();
        });

        // DELETE /api/favorites/{articleId} — unstar. Idempotent; gaps left in sort_order are harmless.
        group.MapDelete("/{articleId:guid}", async (Guid articleId, HttpContext ctx, IFavoriteRepository favRepo) =>
        {
            if (CallerUserId(ctx) is not int userId)
                return Results.Json(new ErrorResponse("User identification failed"), statusCode: 403);

            await favRepo.RemoveAsync(userId, articleId);
            return Results.NoContent();
        });

        // POST /api/favorites/{articleId}/move — nudge one entry up or down by a single position.
        group.MapPost("/{articleId:guid}/move", async (
            Guid articleId, FavoriteMoveRequest req, HttpContext ctx,
            IFavoriteRepository favRepo, IArticleRepository articleRepo) =>
        {
            if (CallerUserId(ctx) is not int userId)
                return Results.Json(new ErrorResponse("User identification failed"), statusCode: 403);

            var up = string.Equals(req.Direction, "up", StringComparison.OrdinalIgnoreCase);
            var down = string.Equals(req.Direction, "down", StringComparison.OrdinalIgnoreCase);
            if (!up && !down)
                return Results.BadRequest(new ErrorResponse("Direction must be 'up' or 'down'"));

            var view = await LoadAsync(userId, favRepo, articleRepo);
            var index = view.Ordered.FindIndex(a => a.Id == articleId);
            if (index < 0) return Results.NotFound(new ErrorResponse("Article is not in favorites"));

            var target = up ? index - 1 : index + 1;
            // Already at the edge: nothing to move, and nothing to renumber either — a stray click
            // on the top item's up-arrow must not silently switch the list out of alphabetical mode.
            if (target < 0 || target >= view.Ordered.Count) return Results.NoContent();

            var movedId = view.Ordered[index].Id;
            var neighbourId = view.Ordered[target].Id;

            // Already-positioned list: exchange just those two positions. Everything else — including
            // favorites the caller currently cannot see (soft-deleted, or in a folder their ACL no
            // longer covers) — keeps the position it had, so a reorder among visible rows can never
            // quietly demote a hidden one to the bottom of the list.
            // The two positions must also differ: duplicates are reachable (a star lands at
            // "min visible - 1" while a hidden row already holds that value), and swapping equal
            // numbers would leave the arrows visibly doing nothing. Renumbering instead repairs it.
            if (view.Positions.GetValueOrDefault(movedId) is int movedPos &&
                view.Positions.GetValueOrDefault(neighbourId) is int neighbourPos &&
                movedPos != neighbourPos)
            {
                await favRepo.SetSortOrdersAsync(userId,
                    [(movedId, neighbourPos), (neighbourId, movedPos)]);
                return Results.NoContent();
            }

            // First manual move of an alphabetical list: positions have to be materialized for ALL
            // of the user's rows at once, because sort_order is all-or-nothing per user (see
            // 011_favorites.sql).
            //
            // Rows the caller cannot see are appended after the visible ones, ordered by when they
            // were starred. Their alphabetical position is unknowable here on purpose: reading the
            // titles of articles outside the caller's folder scope just to sort them would defeat
            // the ACL this endpoint exists to respect.
            var ordered = view.Ordered.ToList();
            (ordered[index], ordered[target]) = (ordered[target], ordered[index]);

            var visibleIds = ordered.Select(a => a.Id).ToList();
            var visibleIdSet = visibleIds.ToHashSet();
            var positions = visibleIds.Select((id, i) => (ArticleId: id, SortOrder: i)).ToList();

            var hidden = view.All
                .Where(f => !visibleIdSet.Contains(f.ArticleId))
                .OrderBy(f => f.SortOrder ?? int.MaxValue)
                .ThenBy(f => f.CreatedAt)
                .ToList();
            positions.AddRange(hidden.Select((f, i) => (f.ArticleId, SortOrder: visibleIds.Count + i)));

            await favRepo.SetSortOrdersAsync(userId, positions);
            return Results.NoContent();
        });

        // POST /api/favorites/reset-order — drop the manual order, back to A→Z.
        group.MapPost("/reset-order", async (HttpContext ctx, IFavoriteRepository favRepo) =>
        {
            if (CallerUserId(ctx) is not int userId)
                return Results.Json(new ErrorResponse("User identification failed"), statusCode: 403);

            await favRepo.ClearSortOrdersAsync(userId);
            return Results.NoContent();
        });
    }

    private static int? CallerUserId(HttpContext ctx) => CallerIdentity.Extract(ctx).UserId;

    /// <param name="Ordered">Visible articles in display order.</param>
    /// <param name="Positions">Stored sort_order per visible article; null while the list is alphabetical.</param>
    /// <param name="ManualOrder">
    /// Whether the list the caller SEES is manually ordered. Deliberately derived from visible rows
    /// only: rows for articles they can no longer see — soft-deleted ones above all — must not put
    /// an otherwise untouched list into manual mode, which would strand new stars at negative
    /// positions and show a "back to A→Z" control on a list nobody ever reordered.
    /// </param>
    /// <param name="MinVisiblePosition">Topmost stored position among visible rows (0 when there are none).</param>
    /// <param name="All">Every favorite row of the user, visible or not — needed to renumber safely.</param>
    private sealed record FavoritesView(
        List<Article> Ordered,
        Dictionary<Guid, int?> Positions,
        bool ManualOrder,
        int MinVisiblePosition,
        List<Favorite> All);

    /// <summary>
    /// The caller's favorites reduced to what they may actually see, in display order.
    /// <c>GetByIdsAsync</c> both drops soft-deleted articles and applies the folder ACL, so losing
    /// access to a folder silently removes its articles from the list instead of leaking a title.
    /// </summary>
    private static async Task<FavoritesView> LoadAsync(
        int userId, IFavoriteRepository favRepo, IArticleRepository articleRepo)
    {
        var favorites = await favRepo.ListByUserAsync(userId);
        if (favorites.Count == 0) return new FavoritesView([], [], false, 0, favorites);

        var articles = await articleRepo.GetByIdsAsync(favorites.Select(f => f.ArticleId).ToList());
        var positionById = favorites.ToDictionary(f => f.ArticleId, f => f.SortOrder);

        var visiblePositions = articles
            .Select(a => positionById.GetValueOrDefault(a.Id))
            .OfType<int>()
            .ToList();
        var manualOrder = visiblePositions.Count > 0;

        // A visible row can only be position-less inside a manual list in one narrow case: it was
        // starred between the read and the write of a reorder. Sorting those first rather than last
        // keeps that race consistent with the rule for a new favorite in a manual list — on top.
        var ordered = manualOrder
            ? articles
                .OrderBy(a => positionById.GetValueOrDefault(a.Id) ?? int.MinValue)
                .ThenBy(a => a.Title, TitleComparer)
                .ToList()
            : articles
                .OrderBy(a => a.Title, TitleComparer)
                .ToList();

        return new FavoritesView(
            ordered,
            ordered.ToDictionary(a => a.Id, a => positionById.GetValueOrDefault(a.Id)),
            manualOrder,
            manualOrder ? visiblePositions.Min() : 0,
            favorites);
    }
}
