using System.Text.Json.Serialization;

namespace BeeMemoryBank.Core.Models;

public record HardDeleteListItem(
    string Type,
    Guid? Id,
    string Path,
    string Title,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long Size
);

public enum HardDeleteStatusFilter
{
    All,
    ActiveOnly,
    DeletedOnly
}

public record HardDeleteResult(
    int DeletedArticles,
    int DeletedFolders,
    int DeletedMedia
);

public record HardDeletePreview(
    int ArticlesCount,
    int SubfoldersCount,
    int MediaCount
);

public class PagedList<T>(List<T> items, int totalCount, int page, int pageSize)
{
    public List<T> Items { get; } = items;
    public int TotalCount { get; } = totalCount;
    public int Page { get; } = page;
    public int PageSize { get; } = pageSize;
    public int TotalPages => (int)Math.Ceiling(totalCount / (double)pageSize);
}

public class HardDeleteAuditEntry
{
    public int Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public int? UserId { get; set; }
    public int? AgentId { get; set; }
    public Guid? SourceNodeId { get; set; }
    public string EntityType { get; set; } = "";
    public string EntityIdentifier { get; set; } = "";
    public string? EntityTitle { get; set; }
    public int DeletedArticles { get; set; }
    public int DeletedFolders { get; set; }
    public int DeletedMedia { get; set; }
}
