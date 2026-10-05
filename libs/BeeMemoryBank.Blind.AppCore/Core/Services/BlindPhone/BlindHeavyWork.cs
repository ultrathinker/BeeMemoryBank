using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>How the last long job ended when it failed; see <see cref="BlindHeavyWork.LastFailure"/>.</summary>
/// <param name="Title">"First load" or "Backup".</param>
/// <param name="Message">The error's own words (no keys or codes are ever in them).</param>
/// <param name="Result">The sentence the run returned: "{Title} failed: {Message}".</param>
/// <param name="At">When the latest of the identical failures happened.</param>
/// <param name="Count">How many times in a row the job failed with this same sentence.</param>
public sealed record BlindHeavyFailure(string Title, string Message, string Result, DateTimeOffset At, int Count);

/// <summary>
/// The Android blind node's long jobs — the first load, then a backup when one is due or asked for —
/// run from the foreground service ("Back up now") or the scheduled worker.
/// </summary>
public sealed class BlindHeavyWork(
    BlindPhoneState state,
    IBlindReplicaSource replica,
    BlindPhoneBackupRunner backups,
    BlindPhoneLog log,
    string replicaWorkDirectory,
    TimeProvider time,
    BlindActivity? activity = null)
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private BlindHeavyFailure? _lastFailure;

    /// <summary>
    /// The failure the job is in at the moment, or null (after a success, and before any failure). A job that keeps failing the same way is one
    /// line in the log the first time and again only at the 2nd, 4th, 8th ... failure ("(attempt N)"), so retries every few minutes do not
    /// push the useful lines out of the log; the count is here for the screen.
    /// </summary>
    public BlindHeavyFailure? LastFailure => Volatile.Read(ref _lastFailure);

    /// <summary>What the notification and the screen show while a job runs.</summary>
    public event Action<string, double>? Progress;

    /// <summary>Runs what is due. <paramref name="forceBackup"/> is the "Back up now" button.</summary>
    public async Task<string> RunAsync(bool forceBackup, CancellationToken ct)
    {
        // Registered before anything is touched: "Disconnect and wipe" waits for this job to end, and a job that starts while it runs is refused.
        using var operation = activity?.TryBegin(BlindActivity.Heavy, ct);
        if (activity is not null && operation is null) return "The copy is being wiped; nothing was started.";
        ct = operation?.Token ?? ct;

        if (!await OneAtATime.WaitAsync(0, ct)) return "Already running.";
        try
        {
            if (state.CallCode is not { } target) return "Not paired yet.";

            if (!state.InitialLoadDone)
            {
                var message = await GuardedAsync("load", "First load", async (progress, token) =>
                {
                    await replica.FetchAndInstallAsync(target, replicaWorkDirectory, progress, token);
                    state.InitialLoadDone = true;
                    log.Add("load", "First load finished.");
                    return "First load finished.";
                }, ct);
                if (!state.InitialLoadDone) return message;
            }

            if (!forceBackup && !state.BackupDue(time.GetUtcNow())) return "Nothing due.";
            return await GuardedAsync("backup", "Backup", async (progress, token) =>
                (await backups.RunAsync(progress, token)).Message, ct);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private async Task<string> GuardedAsync(string kind, string title,
        Func<IProgress<double>, CancellationToken, Task<string>> work, CancellationToken ct)
    {
        var progress = new InlineProgress(p =>
        {
            Progress?.Invoke(title, p);
        });

        try
        {
            var result = await work(progress, ct);
            if (LastFailure is { } failed && failed.Title == title) Volatile.Write(ref _lastFailure, null);
            return result;
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
            return NoteFailure(kind, title, ex.Message);
        }
    }

    private string NoteFailure(string kind, string title, string message)
    {
        var result = $"{title} failed: {message}";
        var previous = LastFailure;
        var count = previous is not null && previous.Result == result ? previous.Count + 1 : 1;
        Volatile.Write(ref _lastFailure, new BlindHeavyFailure(title, message, result, time.GetUtcNow(), count));
        if (count == 1) log.Add(kind, result);
        else if ((count & (count - 1)) == 0) log.Add(kind, $"{result} (attempt {count})");
        return result;
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
