using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BeeMemoryBank.Desktop.Services;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// What "Check for updates" and "Restart to update" show: the check and download with progress,
/// then either "you're up to date" or "Update now", and finally "updating, the app will restart".
/// Closing the window while it checks or downloads cancels that; nothing is ever applied without
/// the "Update now" click.
/// </summary>
public partial class UpdateWindow : Window
{
    private enum Mode { Idle, Checking, Ready, Failed, Applying }

    private readonly DesktopUpdateController _updates;
    private readonly Func<Task> _applyUpdate;
    private Mode _mode;

    public UpdateWindow()
    {
        // Designer fallback only.
        _updates = null!;
        _applyUpdate = () => Task.CompletedTask;
        InitializeComponent();
    }

    public UpdateWindow(DesktopUpdateController updates, Func<Task> applyUpdate)
    {
        _updates = updates;
        _applyUpdate = applyUpdate;
        InitializeComponent();

        _updates.Progress += OnProgress;
        Closing += (_, _) =>
        {
            if (_mode == Mode.Checking) _updates.CancelCheck();
        };
        Closed += (_, _) => _updates.Progress -= OnProgress;
    }

    /// <summary>Runs (or joins) a check and shows how it ended.</summary>
    public async Task CheckAsync()
    {
        if (_mode is Mode.Checking or Mode.Applying) return;

        ShowChecking(_updates.CurrentProgress);
        var outcome = await _updates.CheckAsync(interactive: true);
        if (_mode != Mode.Checking) return; // closed or moved on meanwhile

        switch (outcome.Result)
        {
            case UpdateCheckResult.Ready:
                Show(Mode.Ready, $"Version {outcome.Version} is ready",
                    "Update now: the app closes and opens again by itself in about a minute.",
                    progress: null, primary: "Update now", secondary: "Later");
                break;
            case UpdateCheckResult.UpToDate:
                Show(Mode.Idle, "You're up to date",
                    $"Version {_updates.CurrentVersion} is the latest.",
                    progress: null, primary: "Close", secondary: null);
                break;
            case UpdateCheckResult.NotInstalled:
                Show(Mode.Idle, "Updates are off",
                    "This copy was not installed with Setup, so it does not update itself.",
                    progress: null, primary: "Close", secondary: null);
                break;
            case UpdateCheckResult.Failed:
                Show(Mode.Failed, "Couldn't check for updates",
                    $"{outcome.Error}\n\nCheck the internet connection and try again.",
                    progress: null, primary: "Try again", secondary: "Close");
                break;
            case UpdateCheckResult.Cancelled:
                Close();
                break;
        }
    }

    /// <summary>The last state: the update is being applied and the app is about to restart.</summary>
    public void ShowApplying(string? version)
    {
        Show(Mode.Applying, $"Updating to {version}",
            "Please wait. The app closes and opens again by itself in about a minute.",
            progress: -1, primary: null, secondary: null);
    }

    private void ShowChecking(UpdateCheckProgress? progress)
    {
        if (progress?.FoundVersion is { } version)
        {
            Show(Mode.Checking, $"Downloading version {version}",
                $"{progress.Percent}%. You can keep working; close this window to stop the download.",
                progress: progress.Percent, primary: null, secondary: "Cancel");
        }
        else
        {
            Show(Mode.Checking, "Checking for updates",
                "Looking for a new version of Bee Memory Bank...",
                progress: -1, primary: null, secondary: "Cancel");
        }
    }

    private void OnProgress(object? sender, UpdateCheckProgress progress)
    {
        if (_mode == Mode.Checking) ShowChecking(progress);
    }

    /// <param name="progress">null hides the bar, -1 shows it indeterminate, 0–100 is a percent.</param>
    private void Show(Mode mode, string title, string body, int? progress, string? primary, string? secondary)
    {
        _mode = mode;
        TitleText.Text = title;
        BodyText.Text = body;

        Bar.IsVisible = progress is not null;
        Bar.IsIndeterminate = progress == -1;
        if (progress is >= 0) Bar.Value = progress.Value;

        PrimaryButton.IsVisible = primary is not null;
        PrimaryButton.Content = primary;
        SecondaryButton.IsVisible = secondary is not null;
        SecondaryButton.Content = secondary;
        Buttons.IsVisible = primary is not null || secondary is not null;
    }

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Ready:
                await _applyUpdate();
                break;
            case Mode.Failed:
                await CheckAsync();
                break;
            default:
                Close();
                break;
        }
    }

    private void OnSecondaryClick(object? sender, RoutedEventArgs e)
    {
        // Cancel while checking (Closing cancels the check), Later / Close otherwise.
        Close();
    }
}
