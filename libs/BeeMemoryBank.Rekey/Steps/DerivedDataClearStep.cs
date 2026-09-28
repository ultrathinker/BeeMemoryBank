using Dapper;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>
/// Clears what is derived from content in the copy (rekey-offline.md §2 step 3, R1; the offline form of DK2's WI-9):
/// the search index state and its key, the projection matrix, article and chunk embeddings, and tag vectors. All of it
/// is rebuilt by the existing startup paths after the first unlock: every article is flagged
/// <c>index_pending</c> and <c>embedding_pending</c>, the index key and the projection matrix are created again on
/// first use. The index segment files stay behind in the old directory (§5). Nothing here needs a key.
/// </summary>
public sealed class DerivedDataClearStep : IRekeyStep
{
    public string Name => "DerivedDataClear";

    private static readonly string[] ClearedTables =
    [
        "tbl_article_chunk_embedding", "tbl_search_index_manifest", "tbl_search_segment_tombstone",
        "tbl_search_index_key", "tbl_projection_matrix",
    ];

    public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
    {
        var counts = new Dictionary<string, long>();
        using var tx = ctx.Main.BeginTransaction();
        foreach (var table in ClearedTables)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            counts[table] = await ctx.Main.ExecuteAsync($"DELETE FROM {table}", transaction: tx);
        }
        counts["tbl_article.embedding_projection"] = await ctx.Main.ExecuteAsync(
            @"UPDATE tbl_article SET embedding_projection = NULL, embedding_model_version = NULL,
                     embedding_pending = 1, index_pending = 1", transaction: tx);
        counts["tbl_concept_tag.embedding"] = await ctx.Main.ExecuteAsync(
            "UPDATE tbl_concept_tag SET embedding = NULL, embedding_model_version = NULL", transaction: tx);
        tx.Commit();
        ctx.Progress.Report(Name, 1, 1);
        return new RekeyStepResult(Name, counts, []);
    }

    public async Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx)
    {
        var problems = new List<RekeyProblem>();
        foreach (var table in ClearedTables)
            if (await ctx.Main.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}") is var n and > 0)
                problems.Add(new(table, "*", $"{n} row(s) left"));
        if (await ctx.Main.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_article WHERE embedding_projection IS NOT NULL OR embedding_pending = 0 OR index_pending = 0") is var a and > 0)
            problems.Add(new("tbl_article", "*", $"{a} article(s) keep an embedding or are not queued for the rebuild"));
        if (await ctx.Main.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_concept_tag WHERE embedding IS NOT NULL") is var t and > 0)
            problems.Add(new("tbl_concept_tag", "*", $"{t} tag vector(s) left"));
        return problems;
    }
}
