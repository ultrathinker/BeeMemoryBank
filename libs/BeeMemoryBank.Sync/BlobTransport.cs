using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Moves blob bytes between peers so that, by the time an event reaches EventApplier, every blob
/// it references is already in the local store. Client side of /api/sync/blobs/* — the server side
/// lives in SyncEndpoints.
///
/// The direction is always "the node that opened the connection does the work", in both phases:
/// when pushing, we ship our blobs before our events; when pulling, we fetch the peer's blobs
/// before applying its events. The receiver of a push never calls back — most peers sit behind
/// NAT and cannot be dialed — which is why this is not a lazy "fetch on miss" inside the applier.
/// Content addressing makes the whole thing idempotent: re-shipping a blob the peer already has
/// is a no-op, so nothing here needs to remember what was sent.
/// </summary>
internal static class BlobTransport
{
    /// <summary>
    /// Raw bytes per upload/download request. Base64 in JSON adds a third, so this lands around
    /// 11MB on the wire — well under the 32MB per-request cap the sync endpoints enforce, with the
    /// same "one oversized item still goes alone" rule as event batching (a 20MB media blob is
    /// ~27MB encoded, and fits).
    /// </summary>
    private const long BatchByteTarget = 8 * 1024 * 1024;

    /// <summary>Hashes per check/get request; the endpoints reject more than 2000.</summary>
    private const int HashesPerRequest = 500;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Push side: asks the peer which of the blobs referenced by <paramref name="events"/> it lacks,
    /// and uploads those. Call before pushing the events themselves.
    /// </summary>
    public static async Task ShipForAsync(
        HttpClient http, string baseUrl, string token, IReadOnlyList<SyncEvent> events,
        IBlobRepository blobRepo, ILogger logger, CancellationToken ct)
    {
        var referenced = BlobReferences.Collect(events);
        if (referenced.Count == 0) return;

        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in referenced.Chunk(HashesPerRequest))
            missing.UnionWith(await CheckMissingAsync(http, baseUrl, token, chunk, ct));
        if (missing.Count == 0) return;

        var remaining = new HashSet<string>(missing, StringComparer.Ordinal);
        int shipped = 0;
        while (remaining.Count > 0)
        {
            var batch = await blobRepo.GetManyAsync(remaining.ToList(), BatchByteTarget);
            if (batch.Count == 0)
            {
                // Every one of these is referenced by an event in our own log, and the garbage
                // collector keeps such blobs — so this is corruption or manual surgery, not a
                // normal state. The events go out anyway; the peer will fail to apply them and
                // its skips show up as a push stall in /api/sync/status, which is the existing
                // signal for "an event cannot be delivered".
                logger.LogError(
                    "Blob transport: {Count} blob(s) referenced by outgoing events are missing from the local store " +
                    "(first: {Hash}). The peer will not be able to apply those events.",
                    remaining.Count, remaining.First());
                break;
            }

            await UploadWithSplitAsync(http, baseUrl, token, batch, logger, ct);
            shipped += batch.Count;
            foreach (var b in batch) remaining.Remove(b.Hash);
        }

        if (shipped > 0)
            logger.LogInformation("Blob transport: shipped {Shipped} blob(s) to {Base} ahead of {Events} event(s)",
                shipped, baseUrl, events.Count);
    }

    /// <summary>
    /// Pull side: for the events just pulled, fetches every referenced blob we do not already hold.
    /// Call before applying the events. Blobs the peer cannot supply are logged and left missing —
    /// the corresponding event then fails with <see cref="BlobMissingException"/> in the applier
    /// and goes through the normal retry/quarantine path.
    /// </summary>
    public static async Task FetchForAsync(
        HttpClient http, string baseUrl, string token, IReadOnlyList<SyncEvent> events,
        IBlobRepository blobRepo, ILogger logger, CancellationToken ct)
    {
        var referenced = BlobReferences.Collect(events);
        if (referenced.Count == 0) return;

        var have = await blobRepo.GetExistingAsync(referenced);
        var missing = referenced.Where(h => !have.Contains(h)).ToList();
        if (missing.Count == 0) return;

        int fetched = 0, unavailable = 0;
        // Work through `missing` in fixed-size requests. The peer answers each with as many of the
        // requested blobs as fit its byte budget (at least one, if it has any of them), so a
        // request may need several rounds; a round that returns nothing means the peer has none of
        // the hashes asked for, and that group is given up on rather than re-asked forever.
        foreach (var group in missing.Chunk(HashesPerRequest))
        {
            var pending = new HashSet<string>(group, StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var got = await DownloadAsync(http, baseUrl, token, pending.ToList(), ct);
                if (got.Count == 0)
                {
                    unavailable += pending.Count;
                    logger.LogWarning(
                        "Blob transport: peer {Base} has none of {Count} requested blob(s) (first: {Hash}); " +
                        "the events referencing them cannot be applied until it does.",
                        baseUrl, pending.Count, pending.First());
                    break;
                }
                var progressed = false;
                foreach (var blob in got)
                {
                    // Only what we asked for counts. A peer answering with hashes we did not request
                    // (or with the same one over and over) must not keep this loop alive, and bytes
                    // whose real hash differs from the claim are dropped rather than stored — storing
                    // them under their real hash would only leave garbage for the collector, and the
                    // claimed hash stays unresolved either way so the event fails safe.
                    if (!pending.Contains(blob.Hash))
                    {
                        logger.LogWarning("Blob transport: peer {Base} returned unrequested blob {Hash}; ignoring", baseUrl, blob.Hash);
                        continue;
                    }
                    if (!BlobHash.Matches(blob.Hash, blob.Data))
                    {
                        logger.LogWarning("Blob transport: peer {Base} sent bytes that do not hash to {Claimed}; dropping", baseUrl, blob.Hash);
                        pending.Remove(blob.Hash);
                        unavailable++;
                        progressed = true;
                        continue;
                    }
                    await blobRepo.StoreAsync(blob.Data);
                    pending.Remove(blob.Hash);
                    fetched++;
                    progressed = true;
                }
                if (!progressed)
                {
                    unavailable += pending.Count;
                    logger.LogWarning("Blob transport: peer {Base} made no progress on {Count} requested blob(s); giving up on them this cycle",
                        baseUrl, pending.Count);
                    break;
                }
            }
        }

        logger.LogInformation("Blob transport: fetched {Fetched} blob(s) from {Base} for {Events} event(s){Unavailable}",
            fetched, baseUrl, events.Count, unavailable > 0 ? $", {unavailable} unavailable" : "");
    }

    // ─── Wire ───────────────────────────────────────────────────────────────────

    private static async Task<List<string>> CheckMissingAsync(
        HttpClient http, string baseUrl, string token, IReadOnlyCollection<string> hashes, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sync/blobs/check");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new HashListDto(hashes.ToList()), options: JsonOpts);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<MissingDto>(JsonOpts, ct);
        return body?.Missing ?? [];
    }

    /// <summary>
    /// Uploads a batch; on 413 halves it and retries, the same safety net PushChunkWithSplitAsync
    /// gives events. A single blob the peer still rejects as too large is logged and skipped — the
    /// event referencing it will then be skipped by the peer and surface as a push stall, which is
    /// the existing signal for "this cannot be delivered" — rather than failing the whole sync
    /// cycle every time, which would block every other event to that peer behind one oversized
    /// blob. MediaService caps files at 20MB (~27MB base64), under the 32MB request cap, so this
    /// only fires on a configuration mismatch between the two nodes.
    /// </summary>
    private static async Task UploadWithSplitAsync(
        HttpClient http, string baseUrl, string token, List<StoredBlob> blobs, ILogger logger, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sync/blobs");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(
            new BlobBatchDto(blobs.Select(b => new BlobDto(b.Hash, Convert.ToBase64String(b.Data))).ToList()),
            options: JsonOpts);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.RequestEntityTooLarge)
        {
            resp.EnsureSuccessStatusCode();
            return;
        }

        if (blobs.Count == 1)
        {
            logger.LogError(
                "Blob transport: peer {Base} rejected blob {Hash} ({Bytes} bytes) as too large even alone; " +
                "skipping it — the event referencing it cannot be delivered until the size caps agree.",
                baseUrl, blobs[0].Hash, blobs[0].Data.Length);
            return;
        }
        var mid = blobs.Count / 2;
        await UploadWithSplitAsync(http, baseUrl, token, blobs[..mid], logger, ct);
        await UploadWithSplitAsync(http, baseUrl, token, blobs[mid..], logger, ct);
    }

    private static async Task<List<StoredBlob>> DownloadAsync(
        HttpClient http, string baseUrl, string token, IReadOnlyCollection<string> hashes, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sync/blobs/get");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new HashListDto(hashes.ToList()), options: JsonOpts);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<BlobBatchDto>(JsonOpts, ct);
        var result = new List<StoredBlob>(body?.Blobs.Count ?? 0);
        foreach (var b in body?.Blobs ?? [])
        {
            byte[] data;
            try { data = Convert.FromBase64String(b.Data); }
            catch (FormatException) { continue; }
            result.Add(new StoredBlob(b.Hash, data));
        }
        return result;
    }

    // Wire shapes; mirrored by the endpoint DTOs in BeeMemoryBank.Api.Models. Kept private here
    // for the same reason SyncClient keeps its own — the Sync library does not reference Api.
    private sealed record HashListDto(List<string> Hashes);
    private sealed record MissingDto(List<string> Missing);
    private sealed record BlobDto(string Hash, string Data);
    private sealed record BlobBatchDto(List<BlobDto> Blobs);
}
