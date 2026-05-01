using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Migrator;

/// <summary>Reads data from a v1 SQLite database.</summary>
public class V1Reader(string dbPath)
{
    public List<V1Node> ReadNodes()
    {
        using var conn = Open();
        return conn.Query<V1Node>(
            "SELECT id, parent_id AS ParentId, name FROM tbl_node WHERE status = 'A' ORDER BY id"
        ).ToList();
    }

    public List<V1Article> ReadArticles()
    {
        using var conn = Open();
        return conn.Query<V1Article>(
            "SELECT id, node_id AS NodeId, title, content FROM tbl_article WHERE status = 'A' ORDER BY id"
        ).ToList();
    }

    public Dictionary<long, List<string>> ReadArticleTags()
    {
        using var conn = Open();
        var rows = conn.Query<(long ArticleId, string Tag)>(
            "SELECT at.article_id AS ArticleId, t.name AS Tag FROM tbl_article_tag at JOIN tbl_tag t ON t.id = at.tag_id"
        ).ToList();

        var result = new Dictionary<long, List<string>>();
        foreach (var (articleId, tag) in rows)
        {
            if (!result.TryGetValue(articleId, out var list))
            {
                list = [];
                result[articleId] = list;
            }
            list.Add(tag);
        }
        return result;
    }

    private IDbConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    /// <summary>Builds a dictionary of node_id → "/Path/To/Node" from a flat list of nodes.</summary>
    public static Dictionary<long, string> BuildNodePaths(List<V1Node> nodes)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var cache = new Dictionary<long, string>();

        string GetPath(long id)
        {
            if (cache.TryGetValue(id, out var cached)) return cached;
            if (!byId.TryGetValue(id, out var node)) return "/Imported";

            var parentPath = node.ParentId.HasValue ? GetPath(node.ParentId.Value) : "";
            var path = parentPath + "/" + node.Name;
            cache[id] = path;
            return path;
        }

        foreach (var node in nodes)
            GetPath(node.Id);

        return cache;
    }
}

public record V1Node(long Id, long? ParentId, string Name);
public record V1Article(long Id, long NodeId, string Title, string Content);
