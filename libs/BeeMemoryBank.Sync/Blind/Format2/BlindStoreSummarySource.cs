using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Sync.Blind.Format2;

/// <summary>
/// What a blind host holds, for its status and for my-standing (docs/design/step-a-spec.md 10.1): the current baseline, the
/// deltas stored after it (every row of this host's log past the baseline's local cut, or the whole log before the first
/// baseline), the blob store and the bytes on disk. No note count, by design (owner decision 6).
/// </summary>
public sealed class BlindStoreSummarySource(DbConnectionFactory factory, IBaselineStore store, string dataPath) : IBlindStoreSummarySource
{
    public async Task<BlindStoreSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var current = await store.GetCurrentAsync(ct);
        var cut = current?.LocalCut ?? 0;
        using var conn = factory.CreateConnection();
        var deltas = await conn.QuerySingleAsync<(long Count, long Bytes)>(
            "SELECT COUNT(*), COALESCE(SUM(length(payload) + length(signature)), 0) FROM tbl_event WHERE sequence_num > @cut",
            new { cut });
        var blobs = await conn.QuerySingleAsync<(long Count, long Bytes)>("SELECT COUNT(*), COALESCE(SUM(size), 0) FROM tbl_blob");
        var database = await conn.ExecuteScalarAsync<long>(
            "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()");
        var m = current?.Files.Manifest;
        return new BlindStoreSummary(m?.BaselineId, m?.ProducerNodeId, m?.CreatedAt, m?.KeyGeneration,
            deltas.Count, deltas.Bytes, blobs.Count, blobs.Bytes, database + DirectorySize(Path.Combine(dataPath, "baselines")));
    }

    private static long DirectorySize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }
}
