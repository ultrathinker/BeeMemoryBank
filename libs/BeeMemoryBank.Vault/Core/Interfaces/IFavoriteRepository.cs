using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IFavoriteRepository
{
    /// <summary>
    /// Raw favorite rows for a user, including any whose article is deleted or out of the
    /// caller's folder scope — filtering and ordering are the service/endpoint's job, because
    /// only they hold the caller scope.
    /// </summary>
    Task<List<Favorite>> ListByUserAsync(int userId);

    /// <summary>
    /// Adds a star. A row already there is left untouched (idempotent), so a double-click on
    /// the star can't duplicate or reshuffle anything. <paramref name="sortOrder"/> is null
    /// while the user's list is alphabetical, and "one above the current top" once it is manual.
    /// </summary>
    Task AddAsync(int userId, Guid articleId, int? sortOrder);

    Task RemoveAsync(int userId, Guid articleId);

    /// <summary>Writes explicit positions for a user's rows — used by both "materialize alphabetical order" and a move.</summary>
    Task SetSortOrdersAsync(int userId, IReadOnlyList<(Guid ArticleId, int SortOrder)> positions);

    /// <summary>Drops back to automatic alphabetical order by clearing every position for the user.</summary>
    Task ClearSortOrdersAsync(int userId);

}
