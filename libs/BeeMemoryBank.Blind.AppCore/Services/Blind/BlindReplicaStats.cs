using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What the copy holds, counted in its own database for a host's screen: the live notes (articles) it keeps for the network. Only rows
/// are counted; nothing is decrypted and no key is touched (a blind copy has none that could open a note).
/// </summary>
public sealed class BlindReplicaStats(BlindStartup startup, IDbConnectionFactory connections)
{
    /// <summary>
    /// The number of live notes, or null when the database cannot be read now (not open yet, being replaced by a first load, damaged): a
    /// screen then says "unknown" rather than a wrong number. Never throws, except for the caller's cancellation.
    /// </summary>
    public async Task<long?> CountNotesAsync(CancellationToken ct = default)
    {
        try
        {
            await startup.EnsureReadyAsync().WaitAsync(ct);
            using var conn = connections.CreateConnection();
            // Straight from the table, like the blind node's own status (CoreBlindStatusContributor): what this copy stores, not what a
            // caller's folder rules would let it read - a blind copy has no users and no scope.
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM tbl_article WHERE status = 'A'", cancellationToken: ct));
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
}
