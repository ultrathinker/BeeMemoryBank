using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// The public door to the blob push for the blobs of a baseline (docs/design/step-a-spec.md 8.7): before a <c>baseline</c> package goes to a
/// blind node, the sender asks it through <c>/api/sync/blobs/check</c> which blobs of <c>blobs.txt</c> it lacks and uploads those through
/// <c>/api/sync/blobs</c>, exactly as it ships the blobs of events ahead of a push.
/// </summary>
public static class BaselineBlobShipper
{
    public static Task ShipAsync(
        HttpClient http, string baseUrl, string token, IReadOnlyCollection<string> hashes, IBlobRepository blobs, ILogger logger,
        string baselineLabel, CancellationToken ct = default) =>
        BlobTransport.ShipHashesAsync(http, baseUrl, token, hashes, blobs, logger, ct, baselineLabel);
}
