using BeeMemoryBank.Sync.Blind.Format2;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// <see cref="IBaselineSource"/> of a full node (docs/design/step-a-spec.md 7.3, 8.3): the newest baseline it produced that is complete and
/// whole on disk, with its own log position as the cut. Needs no key and no unlock, so a locked hub keeps serving the newest baseline it
/// produced while it was unlocked (spec 7.5).
/// </summary>
public sealed class FullNodeBaselineSource(SnapshotService snapshots, ILogger<FullNodeBaselineSource> logger) : IBaselineSource
{
    public Task<ServableBaseline?> GetServableAsync(CancellationToken ct = default)
    {
        var root = ProducedBaselines.Root(snapshots.DataPath);
        if (!Directory.Exists(root)) return Task.FromResult<ServableBaseline?>(null);

        ServableBaseline? newest = null;
        foreach (var directory in Directory.EnumerateDirectories(root).Where(ProducedBaselines.IsComplete))
        {
            ct.ThrowIfCancellationRequested();
            if (TryLoad(directory) is not { } files) continue;
            if (newest is null || files.Manifest.CreatedAt > newest.Files.Manifest.CreatedAt)
                newest = new ServableBaseline(files, files.Manifest.ProducerLogHead);
        }
        return Task.FromResult(newest);
    }

    /// <summary>The baseline in <paramref name="directory"/>, or null when a file is missing, cut short or unreadable (it is then not served).</summary>
    private BaselineFiles? TryLoad(string directory)
    {
        try
        {
            var manifestBytes = File.ReadAllBytes(Path.Combine(directory, BaselineManifest.FileName));
            var signature = File.ReadAllBytes(Path.Combine(directory, BaselineManifest.SignatureFileName));
            var manifest = BaselineManifest.Parse(manifestBytes);
            var container = new FileInfo(Path.Combine(directory, BaselineManifest.ContainerFileName));
            if (signature.Length != 64 || !container.Exists || container.Length != manifest.ContainerBytes
                || !File.Exists(Path.Combine(directory, BaselineManifest.BlobListFileName))
                || !string.Equals(Path.GetFileName(directory), manifest.BaselineId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                return null;
            return new BaselineFiles(directory, manifest, manifestBytes, signature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.LogWarning("A produced baseline in {Directory} cannot be read and is not served: {Reason}", Path.GetFileName(directory), ex.Message);
            return null;
        }
    }
}
