using System.Globalization;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>
/// The file names of snapshot archives. A name is the UTC second the snapshot is made in, and a restore by name looks in <c>snapshots/</c> and then in
/// <c>pre-encryption/</c> (docs/design/step-b-spec.md 10.7), so a new name must be free in both folders: two archives of one name would let a restore
/// open the wrong one. The node and <c>bmb snapshot create</c> both name their archives here.
/// </summary>
public static class SnapshotFileNames
{
    /// <summary>The folder of the ordinary snapshots, under the data folder.</summary>
    public const string SnapshotsDir = "snapshots";

    /// <summary>
    /// A file name for a snapshot made at <paramref name="utcNow"/> that is not taken in <paramref name="outputDir"/>, in <c>snapshots/</c> or in
    /// <c>pre-encryption/</c>: <c>bmb-snapshot-yyyyMMdd-HHmmss.tar.gz</c>, with <c>-2</c>, <c>-3</c>... before the extension when the second is taken.
    /// A blind node has no <c>pre-encryption/</c> folder, so the check there finds nothing.
    /// </summary>
    public static string Unique(string dataPath, string outputDir, DateTime utcNow)
    {
        var timestamp = utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"bmb-snapshot-{timestamp}.tar.gz";
        for (var counter = 2; Taken(dataPath, outputDir, fileName); counter++)
            fileName = $"bmb-snapshot-{timestamp}-{counter}.tar.gz";
        return fileName;
    }

    private static bool Taken(string dataPath, string outputDir, string fileName) =>
        File.Exists(Path.Combine(outputDir, fileName))
        || File.Exists(Path.Combine(dataPath, SnapshotsDir, fileName))
        || File.Exists(Path.Combine(dataPath, VaultStoreFiles.PreEncryptionDir, fileName));
}
