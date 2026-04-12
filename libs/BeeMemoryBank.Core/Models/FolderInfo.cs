namespace BeeMemoryBank.Core.Models;

public class FolderInfo
{
    public Guid Id { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public int ArticleCount { get; set; }
}

public class TreeChildrenResult
{
    public string Path { get; set; } = "";
    public List<FolderInfo> Folders { get; set; } = [];
    public List<Article> Articles { get; set; } = [];
}
