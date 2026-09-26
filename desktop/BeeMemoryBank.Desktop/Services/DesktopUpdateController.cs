using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// One place for the desktop self-update state that both the tray menu and the Settings
/// window show: the background check schedule, the "check automatically" preference, the
/// status line and the version waiting for a restart. UI-thread only.
/// </summary>
public sealed class DesktopUpdateController
{
    private const string AutoCheckKey = "autoCheckUpdates";

    private readonly DesktopUpdateService _updates;
    private readonly DesktopSettingsStore _settings;
    private readonly DispatcherTimer _timer;
    private bool _checking;

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

    public bool IsAvailable => _updates.IsAvailable;
    public string? CurrentVersion => _updates.CurrentVersion;
    public string? ReadyVersion => _updates.ReadyVersion;
    public bool IsChecking => _checking;
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

    public async Task CheckAsync(bool interactive)
    {
        if (!IsAvailable || _checking) return;
        _checking = true;
        if (interactive) SetStatus("Checking for updates...");
        else Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var ready = await _updates.CheckAndDownloadAsync();
            if (ready is not null) SetStatus($"Restart to update to {ready}");
            else if (interactive) SetStatus($"Up to date ({CurrentVersion})");
        }
        catch (Exception ex)
        {
            if (interactive) SetStatus("Update check failed");
            Console.WriteLine($"Update check failed: {ex.Message}");
        }
        finally
        {
            _checking = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Restarts into the downloaded version. <paramref name="stopNode"/> runs first so the
    /// database is closed before Velopack swaps the files.
    /// </summary>
    public void ApplyAndRestart(Action stopNode, Action onFailure)
    {
        if (ReadyVersion is null) return;
        stopNode();
        try
        {
            _updates.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Applying update failed: {ex.Message}");
            onFailure();
        }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
