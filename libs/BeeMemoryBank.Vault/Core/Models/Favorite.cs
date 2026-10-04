namespace BeeMemoryBank.Core.Models;

/// <summary>
/// One user's star on one article. See 011_favorites.sql for the sort_order contract:
/// null on every row = automatic alphabetical order; non-null = manual order.
/// </summary>
public class Favorite
{
    public int UserId { get; set; }
    public Guid ArticleId { get; set; }
    public int? SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}
