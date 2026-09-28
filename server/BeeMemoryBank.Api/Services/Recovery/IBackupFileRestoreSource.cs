namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// A backup kept as ONE file that carries its own recovery material (an Android blind node's backup file)
/// rather than a folder or restic repository with a recovery set beside it. "Open an existing
/// profile" and the restore form hand every path to <see cref="BlindRestoreClient.RestoreFromBackupAsync"/>;
/// the first registered source that <see cref="Handles"/> it restores from it.
/// </summary>
public interface IBackupFileRestoreSource
{
    bool Handles(string fullPath);

    /// <param name="boxes">The user's choice about untried recovery boxes, as for every restore
    /// (resolve keys through <see cref="RecoveryRestoreService.ResolveKeysAsync"/>).</param>
    Task<RestoreResult> RestoreAsync(string fullPath, RestoreIdentity who, CancellationToken ct, RestoreBoxPolicy boxes);
}
