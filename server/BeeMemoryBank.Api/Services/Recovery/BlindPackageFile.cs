namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// Names inside a blind package and a blind node's backup. The package itself is read by the blind seed's
/// reader, <see cref="SnapshotService.ExtractVerifiedAsync"/> (see <see cref="RecoveryRestoreService.RestoreFromPackageAsync"/>).
/// </summary>
public static class BlindPackageFile
{
    /// <summary>The database file in a package and in a backup folder.</summary>
    public const string DbFileName = "beememorybank.db";
}
