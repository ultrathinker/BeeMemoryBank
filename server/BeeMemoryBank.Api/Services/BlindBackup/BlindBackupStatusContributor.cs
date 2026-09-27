using BeeMemoryBank.Api.Services.BlindStatus;

namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// The backup/jobs part of <c>GET /api/blind/status</c>: where the repository is
/// (<c>backups_path</c> — always present, null while no repository is configured, so consumers
/// can tell "unset" from "not supported by this build"), the
/// <c>jobs[]</c> rows with their progress/speed/ETA, and the current CPU mode.
/// </summary>
public sealed class BlindBackupStatusContributor(
    BlindBackupSettingsStore settingsStore,
    BlindJobManager jobs) : IBlindStatusContributor
{
    public Task ContributeAsync(BlindStatusBuilder b, CancellationToken ct)
    {
        var s = settingsStore.Load();
        b.Set("backups_path", s.RepoType == BlindRepoType.Folder
            ? s.RepoFolder
            : string.IsNullOrEmpty(s.S3Endpoint) ? null : $"{s.S3Endpoint.TrimEnd('/')}/{s.S3Bucket}/{s.S3Prefix}");
        b.Set("cpu_mode", jobs.Mode.Name());
        foreach (var job in jobs.Snapshot())
            b.AddJob(job);
        return Task.CompletedTask;
    }
}
