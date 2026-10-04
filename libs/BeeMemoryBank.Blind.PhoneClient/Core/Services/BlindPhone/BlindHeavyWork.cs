using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>
/// The Android blind node's long jobs — the first load, then a backup when one is due or asked for —
/// run from the foreground service ("Back up now") or the scheduled worker. Both stop as soon as Wi-Fi,
/// the charger or the battery floor goes away (checked after every chunk) and resume later where they
/// stopped.
/// </summary>
public sealed class BlindHeavyWork(
    BlindPhoneState state,
    IBlindReplicaSource replica,
    BlindPhoneBackupRunner backups,
    IDeviceStateProvider device,
    BlindPhoneLog log,
    string replicaWorkDirectory,
    TimeProvider time)
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>What the notification and the screen show while a job runs.</summary>
    public event Action<string, double>? Progress;

    /// <summary>Runs what is due. <paramref name="forceBackup"/> is the "Back up now" button.</summary>
    public async Task<string> RunAsync(bool forceBackup, CancellationToken ct)
    {
        if (!await OneAtATime.WaitAsync(0, ct)) return "Already running.";
        try
        {
            if (state.CallCode is not { } target) return "Not paired yet.";

            if (!state.InitialLoadDone)
            {
                var message = await GuardedAsync(BlindPhoneJob.InitialLoad, "First load", async (progress, token) =>
                {
                    await replica.FetchAndInstallAsync(target, replicaWorkDirectory, progress, token);
                    state.InitialLoadDone = true;
                    log.Add("load", "First load finished.");
                    return "First load finished.";
                }, ct);
                if (!state.InitialLoadDone) return message;
            }

            if (!forceBackup && !state.BackupDue(time.GetUtcNow())) return "Nothing due.";
            return await GuardedAsync(BlindPhoneJob.Backup, "Backup", async (progress, token) =>
                (await backups.RunAsync(progress, token)).Message, ct);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private async Task<string> GuardedAsync(BlindPhoneJob job, string title,
        Func<IProgress<double>, CancellationToken, Task<string>> work, CancellationToken ct)
    {
        if (BlindPhoneWork.WhyNot(job, device.Current()) is { } wait) return $"{title}: {wait}";

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new InlineProgress(p =>
        {
            Progress?.Invoke(title, p);
            // Charger pulled or Wi-Fi gone mid-run: stop now, continue later from here.
            if (BlindPhoneWork.WhyNot(job, device.Current()) is { } reason)
            {
                log.Add(job == BlindPhoneJob.Backup ? "backup" : "load", $"{title} paused: {reason}");
                stop.Cancel();
            }
        });

        try
        {
            return await work(progress, stop.Token);
        }
        catch (BlindFeaturePendingException ex)
        {
            return ex.Message;
        }
        catch (OperationCanceledException)
        {
            return $"{title} paused; it continues where it stopped.";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException)
        {
            log.Add(job == BlindPhoneJob.Backup ? "backup" : "load", $"{title} failed: {ex.Message}");
            return $"{title} failed: {ex.Message}";
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
