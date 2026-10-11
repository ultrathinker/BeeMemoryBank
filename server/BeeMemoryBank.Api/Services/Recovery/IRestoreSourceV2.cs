using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind.Format2;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// What a restore from a baseline reads (docs/design/step-a-spec.md 12), wherever it came from: a format-2 <c>full</c> package of a blind
/// node (restore code routes, an Android blind backup file) or a Docker blind node's backup folder. Everything here has been checked
/// by the source's own reader (package: spec 8.6, signatures and hashes); what the restore adds is the key check, which only a holder of
/// the master key can make.
/// </summary>
public interface IRestoreSourceV2 : IDisposable
{
    /// <summary>The baseline on disk: manifest, its signature, <c>blobs.txt</c> and <c>baseline.bmbch</c>.</summary>
    BaselineFiles Baseline { get; }

    /// <summary>The serving node's manifest: its whitelist (keys and superadmin flags), pull positions, log head and the activation.</summary>
    BlindManifestV2 Manifest { get; }

    /// <summary>A plain SQLite file with the control tables of the serving node (<c>SnapshotTables.Control</c>), newest rows included.</summary>
    string ControlDbPath { get; }

    /// <summary>The events after the baseline's cut, in the serving node's log order; each carries its sequence number in that log.</summary>
    IEnumerable<SyncEvent> ReadDeltas();

    /// <summary>The content blob stored under <paramref name="hash"/>, or null when the source holds none (a source with no blobs).</summary>
    Stream? OpenBlob(string hash);
}

/// <summary>A format-2 package already extracted and checked by <see cref="IBlindPackageV2Reader"/>.</summary>
public sealed class PackageRestoreSource(VerifiedPackageV2 package) : IRestoreSourceV2
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public BaselineFiles Baseline => package.Baseline;

    public BlindManifestV2 Manifest => package.Manifest;

    public string ControlDbPath => package.ControlDbPath
        ?? throw new InvalidDataException("The package carries no control.db: it is not a full package, a restore cannot use it.");

    public IEnumerable<SyncEvent> ReadDeltas()
    {
        if (package.DeltasPath is null) yield break;
        foreach (var line in File.ReadLines(package.DeltasPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            SyncEvent? evt;
            try { evt = JsonSerializer.Deserialize<SyncEvent>(line, JsonOpts); }
            catch (JsonException ex) { throw new InvalidDataException("deltas.jsonl holds a line that is not an event.", ex); }
            yield return evt ?? throw new InvalidDataException("deltas.jsonl holds an empty line.");
        }
    }

    public Stream? OpenBlob(string hash)
    {
        if (package.BlobsDirectory is null) return null;
        // The name is a hash: anything else could leave the folder.
        if (!BeeMemoryBank.Sync.BlobReferences.IsWellFormedHash(hash)) return null;
        var path = Path.Combine(package.BlobsDirectory, hash);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public void Dispose() { /* the extracted folder belongs to whoever extracted it */ }
}
