namespace BeeMemoryBank.Core.Models;

public class ConceptTag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class ConceptTagInfo
{
    public string Name { get; set; } = "";
    public int ArticleCount { get; set; }
}

public class RelatedArticle
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string TreePath { get; set; } = "";
    public List<string> SharedConcepts { get; set; } = [];
    public int Strength { get; set; }
}

public class ConceptTagWithEmbedding
{
    public string Name { get; set; } = "";
    public byte[]? Embedding { get; set; }
    public string? EmbeddingModelVersion { get; set; }
}

public class ConceptGraphEdge
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public int Weight { get; set; }
}
