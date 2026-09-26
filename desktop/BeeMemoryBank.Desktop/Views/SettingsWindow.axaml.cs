using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BeeMemoryBank.Profiles;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// All desktop settings in one window, opened from the tray: Windows autostart, which profile
/// opens at startup (§4.6 two-mode toggle: "last used" or one fixed profile), sleep
/// prevention and desktop updates. Every control writes through immediately; there is no
/// Save button.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainWindow _owner;
    private readonly ProfileService _profiles;
    private readonly Services.AutostartService _autostart;
    private readonly Services.PreventSleepService? _preventSleep;
    private readonly Services.DesktopUpdateController _updates;
    private readonly Action _checkForUpdates;
    private readonly Action _restartToUpdate;
    private bool _loading;

    public SettingsWindow()
    {
        // Designer fallback only.
        _owner = null!;
        _profiles = null!;
        _autostart = null!;
        _updates = null!;
        _checkForUpdates = () => { };
        _restartToUpdate = () => { };
        InitializeComponent();
    }

    public SettingsWindow(
        MainWindow owner,
        Services.AutostartService autostart,
        Services.PreventSleepService? preventSleep,
        Services.DesktopUpdateController updates,
        Action checkForUpdates,
        Action restartToUpdate)
    {
        _owner = owner;
        _profiles = owner.Profiles;
        _autostart = autostart;
        _preventSleep = preventSleep;
        _updates = updates;
        _checkForUpdates = checkForUpdates;
        _restartToUpdate = restartToUpdate;
        InitializeComponent();

        AutostartCheck.IsCheckedChanged += OnAutostartChanged;
        AutostartLastUsedRadio.IsCheckedChanged += OnAutostartProfileModeChanged;
        AutostartFixedRadio.IsCheckedChanged += OnAutostartProfileModeChanged;
        AutostartProfileCombo.SelectionChanged += OnAutostartProfileChanged;
        PreventSleepCheck.IsCheckedChanged += OnPreventSleepChanged;
        AutoUpdateCheck.IsCheckedChanged += OnAutoUpdateChanged;

        _updates.Changed += OnUpdatesChanged;
        _owner.ActiveProfileChanged += OnProfilesChanged;
        Closed += (_, _) =>
        {
            _updates.Changed -= OnUpdatesChanged;
            _owner.ActiveProfileChanged -= OnProfilesChanged;
        };

        LoadAll();
    }

    private void LoadAll()
    {
        _loading = true;
        try
        {
            AutostartCheck.IsChecked = _autostart.IsEnabled;

            PreventSleepCheck.IsEnabled = _preventSleep != null;
            PreventSleepCheck.IsChecked = _preventSleep?.IsEnabled ?? false;

            AutoUpdateCheck.IsEnabled = _updates.IsAvailable;
            AutoUpdateCheck.IsChecked = _updates.AutoCheck;
        }
        finally
        {
            _loading = false;
        }

        LoadAutostartProfile();
        ShowUpdateState();
    }

    // ── Startup ──────────────────────────────────────────────────────────────────

    private void OnAutostartChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (AutostartCheck.IsChecked == true) _autostart.Enable();
            else _autostart.Disable();
        }
        catch (Exception ex)
        {
            AutostartHint.Text = $"Could not change autostart: {ex.Message}";
        }

        _loading = true;
        AutostartCheck.IsChecked = _autostart.IsEnabled;
        _loading = false;
    }

    private void LoadAutostartProfile()
    {
        _loading = true;
        try
        {
            var all = _profiles.GetAll();
            AutostartProfileCombo.ItemsSource = all
                .Select(p => new ComboBoxItem { Content = p.Name, Tag = p.Id })
                .ToList();

            if (_profiles.AutostartMode == AutostartMode.FixedProfile
                && !string.IsNullOrEmpty(_profiles.AutostartProfileId))
            {
                AutostartFixedRadio.IsChecked = true;
                SelectComboById(_profiles.AutostartProfileId);
                AutostartHint.Text = "At startup the selected profile always opens.";
            }
            else
            {
                AutostartLastUsedRadio.IsChecked = true;
                AutostartProfileCombo.SelectedIndex = -1;
                AutostartHint.Text = "At startup the profile you used last opens.";
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void SelectComboById(string? id)
    {
        var index = -1;
        if (AutostartProfileCombo.ItemsSource is System.Collections.IList items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is ComboBoxItem { Tag: string tagId } && string.Equals(tagId, id, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
        }
        AutostartProfileCombo.SelectedIndex = index;
    }

    private string? SelectedProfileId =>
        AutostartProfileCombo.SelectedItem is ComboBoxItem cbi ? cbi.Tag as string : null;

    private void OnAutostartProfileModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (AutostartFixedRadio.IsChecked == true)
        {
            var id = SelectedProfileId;
            if (string.IsNullOrEmpty(id))
            {
                // Nothing picked yet — the combo selection applies the setting once chosen.
                AutostartHint.Text = "Choose a profile from the list.";
                return;
            }
            ApplyAutostartProfile(AutostartMode.FixedProfile, id);
        }
        else if (AutostartLastUsedRadio.IsChecked == true)
        {
            ApplyAutostartProfile(AutostartMode.LastUsed, null);
        }
    }

    private void OnAutostartProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var id = SelectedProfileId;
        if (string.IsNullOrEmpty(id)) return;

        // Picking a profile is an explicit act: it switches to "always this profile".
        ApplyAutostartProfile(AutostartMode.FixedProfile, id);
    }

    private void ApplyAutostartProfile(AutostartMode mode, string? fixedId)
    {
        try
        {
            _profiles.SetAutostart(mode, fixedId);
            _owner.NotifyProfilesChanged();
        }
        catch (Exception ex)
        {
            AutostartHint.Text = $"Error: {ex.Message}";
            return;
        }
        LoadAutostartProfile();
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(LoadAutostartProfile);
    }

    // ── Power ────────────────────────────────────────────────────────────────────

    private void OnPreventSleepChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _preventSleep == null || !OperatingSystem.IsWindows()) return;
        _preventSleep.IsEnabled = PreventSleepCheck.IsChecked == true;
    }

    // ── Updates ──────────────────────────────────────────────────────────────────

    private void OnAutoUpdateChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _updates.AutoCheck = AutoUpdateCheck.IsChecked == true;
    }

    private void OnCheckNowClick(object? sender, RoutedEventArgs e)
    {
        // The update window shows the check, its progress and the result.
        _checkForUpdates();
    }

    private void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        _restartToUpdate();
    }

    private void OnUpdatesChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ShowUpdateState);
    }

    private void ShowUpdateState()
    {
        VersionText.Text = _updates.IsAvailable
            ? $"Installed version: {_updates.CurrentVersion}"
            : "This copy was not installed with Setup, so it does not update itself.";
        CheckNowButton.IsEnabled = _updates.IsAvailable && !_updates.IsChecking;
        RestartButton.IsVisible = _updates.ReadyVersion != null;
        RestartButton.Content = $"Restart to update to {_updates.ReadyVersion}";
        // The idle status is just "Version X", already shown above.
        UpdateStatusText.Text = _updates.IsAvailable && _updates.StatusText != $"Version {_updates.CurrentVersion}"
            ? _updates.StatusText
            : string.Empty;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
