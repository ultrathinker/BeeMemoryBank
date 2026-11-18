using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Sync;

/// <summary>One node whose events signed by a retired key this node took although the rotation did not list them (plan D3).</summary>
public sealed record RetiredKeyAttentionItem(Guid NodeId, long Count, string SetAt);

/// <summary>
/// The node-local markers behind the attention flag <see cref="EventApplier.RetiredKeyAttentionFlag"/> (one per node, in
/// <c>tbl_migration_marker</c>). A marker ends when its node is revoked or joins again, or when an administrator acknowledges it on the identity
/// page.
/// </summary>
public static class RetiredKeyAttention
{
    public static async Task<IReadOnlyList<RetiredKeyAttentionItem>> ListAsync(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<(string Key, string Value, string SetAt)>(
            "SELECT key, value, set_at FROM tbl_migration_marker WHERE key LIKE @prefix",
            new { prefix = EventApplier.RetiredKeyAttentionMarkerPrefix + "%" });
        var items = new List<RetiredKeyAttentionItem>();
        foreach (var (key, value, setAt) in rows)
            if (Guid.TryParse(key[EventApplier.RetiredKeyAttentionMarkerPrefix.Length..], out var node))
                items.Add(new RetiredKeyAttentionItem(node, long.TryParse(value, out var n) ? n : 0, setAt));
        return items.OrderBy(i => i.NodeId).ToList();
    }

    /// <summary>
    /// Raises the marker of <paramref name="nodeId"/> by <paramref name="count"/> events, or creates it. With <paramref name="onlyIfAbsent"/>, an
    /// existing marker is left as it is (the scan at the rotation counted the event already).
    /// </summary>
    public static async Task RaiseAsync(IDbConnectionFactory db, Guid nodeId, long count, bool onlyIfAbsent = false)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(onlyIfAbsent
                ? "INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at) VALUES (@key, @value, @now)"
                : @"INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@key, @value, @now)
                    ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + @count AS TEXT), set_at = excluded.set_at",
            new
            {
                key = EventApplier.RetiredKeyAttentionMarkerPrefix + nodeId.ToString("D"), value = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                count, now = DateTime.UtcNow.ToString("O"),
            });
    }

    /// <summary>Ends the marker of <paramref name="nodeId"/>; true when there was one.</summary>
    public static async Task<bool> ClearAsync(IDbConnectionFactory db, Guid nodeId)
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = @key COLLATE NOCASE",
            new { key = EventApplier.RetiredKeyAttentionMarkerPrefix + nodeId.ToString("D") }) > 0;
    }
}
