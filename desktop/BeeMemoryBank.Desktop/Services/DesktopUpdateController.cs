using System;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.Desktop.Services;

public enum UpdateCheckResult { NotInstalled, UpToDate, Ready, Cancelled, Failed }

/// <summary>How a check ended. <see cref="Version"/> is set for Ready, <see cref="Error"/> for Failed.</summary>
public sealed record UpdateCheckOutcome(UpdateCheckResult Result, string? Version = null, string? Error = null);

/// <summary>Where a running check is: looking (no version yet) or downloading a found version.</summary>
public sealed record UpdateCheckProgress(string? FoundVersion, int Percent);

/// <summary>
/// One place for the desktop self-update state that the tray menu, the Settings window and the
/// update window show: the background check schedule, the "check automatically" preference, the
/// status line, the running check and the version waiting for a restart. UI-thread only.
/// </summary>
public sealed class DesktopUpdateController
{
    private const string AutoCheckKey = "autoCheckUpdates";

    private readonly DesktopUpdateService _updates;
    private readonly DesktopSettingsStore _settings;
    private readonly DispatcherTimer _timer;
    private Task<UpdateCheckOutcome>? _running;
    private CancellationTokenSource? _cts;

    public DesktopUpdateController(DesktopUpdateService updates, DesktopSettingsStore settings)
    {
        _updates = updates;
        _settings = settings;
        StatusText = updates.IsAvailable ? $"Version {updates.CurrentVersion}" : "Updates: not an installed build";

        // Quiet background check: shortly after start, then daily. Only downloads; the user
        // chooses when to restart.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(24);
            await CheckAsync(interactive: false);
        };
        if (updates.IsAvailable && AutoCheck) _timer.Start();
    }

    /// <summary>Raised on the UI thread whenever any of the properties below changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread as a running check finds a version and downloads it.</summary>
    public event EventHandler<UpdateCheckProgress>? Progress;

    public bool IsAvailable => _updates.IsAvailable;
    public string? CurrentVersion => _updates.CurrentVersion;
    public string? ReadyVersion => _updates.ReadyVersion;
    public bool IsChecking => _cts is not null;
    public UpdateCheckProgress? CurrentProgress { get; private set; }
    public string StatusText { get; private set; }

    /// <summary>Whether the app looks for updates in the background (default on).</summary>
    public bool AutoCheck
    {
        get => _settings.GetBool(AutoCheckKey, defaultValue: true);
        set
        {
            _settings.SetBool(AutoCheckKey, value);
            if (value && IsAvailable) _timer.Start();
            else _timer.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Checks for a newer release and downloads it. A check already running (the background one,
    /// say) is joined rather than started twice. <paramref name="interactive"/> only decides whether
    /// the tray status line reports "up to date" and failures.
    /// </summary>
    public Task<UpdateCheckOutcome> CheckAsync(bool interactive)
    {
        if (!IsAvailable) return Task.FromResult(new UpdateCheckOutcome(UpdateCheckResult.NotInstalled));
        if (ReadyVersion is not null) return Task.FromResult(new UpdateCheckOutcome(UpdateCheckResult.Ready, ReadyVersion));
        if (_running is not null) return _running;
        var run = RunCheckAsync(interactive);
        if (!run.IsCompleted) _running = run;
        return run;
    }

    /// <summary>Stops the running check or download, if any. Nothing is applied either way.</summary>
    public void CancelCheck() => _cts?.Cancel();

    private async Task<UpdateCheckOutcome> RunCheckAsync(bool interactive)
    {
        using var cts = new CancellationTokenSource();
        _cts = cts;
        ReportProgress(new UpdateCheckProgress(null, 0));
        if (interactive) SetStatus("Checking for updates...");
        else Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            string? found = null;
            var ready = await _updates.CheckAndDownloadAsync(
                version => Dispatcher.UIThread.Post(() => ReportProgress(new UpdateCheckProgress(found = version, 0))),
                percent => Dispatcher.UIThread.Post(() => ReportProgress(new UpdateCheckProgress(found, percent))),
                cts.Token);

            if (ready is not null)
            {
                SetStatus($"Restart to update to {ready}");
                return new UpdateCheckOutcome(UpdateCheckResult.Ready, ready);
            }
            if (interactive) SetStatus($"Up to date ({CurrentVersion})");
            return new UpdateCheckOutcome(UpdateCheckResult.UpToDate);
        }
        catch (OperationCanceledException)
        {
            SetStatus($"Version {CurrentVersion}");
            return new UpdateCheckOutcome(UpdateCheckResult.Cancelled);
        }
        catch (Exception ex)
        {
            if (interactive) SetStatus("Update check failed");
            Console.WriteLine($"Update check failed: {ex.Message}");
            return new UpdateCheckOutcome(UpdateCheckResult.Failed, Error: ex.Message);
        }
        finally
        {
            _cts = null;
            _running = null;
            CurrentProgress = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Restarts into the downloaded version. <paramref name="prepare"/> runs while the node is
    /// still up (the session handoff), then <paramref name="stopNode"/> so the database is closed
    /// before Velopack swaps the files. When Velopack cannot apply the update, <paramref name="resumeNode"/>
    /// starts the node again (the app stays up, serving the vault as before) and
    /// <paramref name="onFailure"/> gets the reason in words; it is also written to the logs folder.
    /// </summary>
    public Task ApplyAndRestartAsync(Func<Task> prepare, Action stopNode, Action resumeNode, Action<string> onFailure)
    {
        if (ReadyVersion is null) return Task.CompletedTask;
        return UpdateRestartSequence.RunAsync(prepare, stopNode, _updates.ApplyAndRestart, resumeNode, onFailure, LogToFile);
    }

    private static void LogToFile(string line)
    {
        Directory.CreateDirectory(BmbPaths.LogsDir);
        File.AppendAllText(Path.Combine(BmbPaths.LogsDir, "velopack.log"), $"[{DateTime.UtcNow:O}] {line}{Environment.NewLine}");
    }

    private void ReportProgress(UpdateCheckProgress progress)
    {
        // A late callback from a run that has already ended must not resurrect the progress.
        if (_cts is null) return;
        CurrentProgress = progress;
        Progress?.Invoke(this, progress);
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The steps of "Restart to update", apart from Velopack and the window, so that the failure path can be tested.</summary>
internal static class UpdateRestartSequence
{
    /// <param name="apply">Applies the downloaded update; on success the process ends and this never returns.</param>
    /// <param name="log">Writes one line to the logs folder; a log that cannot be written never hides the failure.</param>
    public static async Task RunAsync(Func<Task> prepare, Action stopNode, Action apply, Action resumeNode, Action<string> onFailure, Action<string> log)
    {
        await prepare();
        stopNode();
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            TryLog(log, $"Applying the update failed ({ex.GetType().Name}): {ex.Message}");
            try
            {
                resumeNode();
            }
            catch (Exception resumeError)
            {
                message += $" The node could not be started again either: {resumeError.Message} Close the app and open it again.";
                TryLog(log, $"Starting the node again after the failed update failed ({resumeError.GetType().Name}): {resumeError.Message}");
            }
            onFailure(message);
        }
    }

    private static void TryLog(Action<string> log, string line)
    {
        try { log(line); }
        catch (Exception) { /* a log is not worth losing the report for */ }
    }
}
