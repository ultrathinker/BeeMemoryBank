using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using Dapper;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What a host's screen shows of the copy's store (docs/design/step-a-spec.md 10.2): the time its restore point (the baseline it adopted) was made,
/// the changes held since, and the bytes stored. The hosts name no sync type: this is theirs.
/// </summary>
/// <param name="RestorePointAt">When the baseline was made (UTC); null when the copy holds none yet.</param>
/// <param name="ChangesSince">The deltas stored after the baseline, as received.</param>
/// <param name="StoredBytes">The database and the baseline's files.</param>
public sealed record BlindStoreInfo(DateTime? RestorePointAt, long ChangesSince, long StoredBytes);

/// <summary>
/// What the copy holds, for a host's screen (docs/design/step-a-spec.md 10.2): the age of its restore point (the baseline it adopted), how many
/// changes it holds since (the deltas, as received), and the bytes it stores. Rows are counted and files measured; nothing is decrypted and no key
/// is touched (a blind copy has none that could open anything), and no note is counted: after the activation the copy holds none in a form it
/// could count, and a number that is wrong for a part of the copies is worse than none.
/// </summary>
public sealed class BlindStoreStats(BlindStartup startup, IDbConnectionFactory connections, IBaselineStore baselines, string dataDirectory)
{
    /// <summary>
    /// The summary, or null when the database cannot be read now (not open yet, being replaced by a first load, damaged): a screen then says
    /// "unknown" rather than a wrong number. Never throws, except for the caller's cancellation.
    /// </summary>
    public async Task<BlindStoreInfo?> GetAsync(CancellationToken ct = default)
    {
        try
        {
            await startup.EnsureReadyAsync().WaitAsync(ct);
            var current = await baselines.GetCurrentAsync(ct);
            using var conn = connections.CreateConnection();
            // Every row of the log of a copy is a delta: what its baseline covers of the node's log is trimmed away (spec 13.3), and before
            // the first baseline the whole log is what the copy holds.
            var deltas = await conn.QuerySingleAsync<(long Count, long Bytes)>(new CommandDefinition(
                "SELECT COUNT(*), COALESCE(SUM(length(payload) + length(signature)), 0) FROM tbl_event", cancellationToken: ct));
            var database = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()", cancellationToken: ct));
            var m = current?.Files.Manifest;
            return new BlindStoreInfo(m?.CreatedAt, deltas.Count, database + DirectorySize(Path.Combine(dataDirectory, "baselines")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return null;
        }
    }

    private static long DirectorySize(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* a file that went while it was measured is not stored any more */ }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }
}
