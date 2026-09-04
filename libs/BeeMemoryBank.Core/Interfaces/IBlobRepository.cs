using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Content-addressed store of ciphertext bytes (tbl_blob), keyed by <see cref="Services.BlobHash"/>.
///
/// Article bodies and versions reference a row here by hash instead of carrying the bytes inline,
/// and a sync event references the same row instead of embedding a base64 copy — so one row
/// serves the live body, its history and the event log at once. Media ciphertext also passes
/// through here, but only in transit: its home is the .enc file on disk, and the blob row exists
/// so the pusher can ship it ahead of the media_create event and a relay node can forward it.
///
/// Nothing enforces referential integrity from the referencing tables to this one (no foreign
/// keys, on purpose — see migration 016). The invariants are held by two conventions instead:
/// a writer inserts the blob in the same transaction as, or before, the row that references it;
/// and <see cref="SweepUnreferencedAsync"/> never touches a blob younger than its grace period.
/// </summary>
public interface IBlobRepository
{
    /// <summary>
    /// Stores <paramref name="data"/> under its own SHA-256 and returns that hash. There is no
    /// overload that accepts a caller-supplied hash: bytes arriving from a peer are stored under
    /// what they actually hash to, so a wrong claim can never occupy a right address. Idempotent —
    /// storing bytes already present is a no-op.
    /// </summary>
    Task<string> StoreAsync(byte[] data, IDbTransaction? transaction = null);

    Task<byte[]?> GetAsync(string hash);

    /// <summary>Which of <paramref name="hashes"/> this node already has.</summary>
    Task<HashSet<string>> GetExistingAsync(IReadOnlyCollection<string> hashes);

    /// <summary>
    /// Loads blobs for a batch of hashes, stopping once <paramref name="byteBudget"/> is exceeded
    /// — but always returning at least one if any exist, so a caller paging through a list makes
    /// progress even when a single blob is bigger than the budget. Hashes not found are simply
    /// absent from the result; the caller compares against what it asked for.
    /// </summary>
    Task<List<StoredBlob>> GetManyAsync(IReadOnlyCollection<string> hashes, long byteBudget);

    /// <summary>
    /// Garbage collection: deletes blobs created before <paramref name="createdBefore"/> that no
    /// article body, article version or event payload references. The cutoff is the grace period
    /// that makes this safe against a blob stored moments before the row referencing it commits
    /// (a pushed blob waits for its event in a later HTTP request). Returns the number swept.
    /// </summary>
    Task<int> SweepUnreferencedAsync(DateTime createdBefore);

    /// <summary>Row count and total bytes — for the admin status view and tests.</summary>
    Task<(long Count, long Bytes)> GetStatsAsync();
}
