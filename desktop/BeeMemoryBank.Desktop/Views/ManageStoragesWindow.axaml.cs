using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BeeMemoryBank.Profiles;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// Simple immutable view-model the ManageStoragesWindow DataTemplate binds to. Marked public
/// + top-level so the XAML compiler can resolve <c>x:DataType</c> against it and validate
/// bindings at compile time. Setters exist only because ItemsControl occasionally wants to
/// clone rows; in practice the list is rebuilt wholesale on every refresh.
/// </summary>
public sealed class ProfileRow
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DataPath { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string ActiveBadge { get; set; } = string.Empty;
}

/// <summary>
/// Native window for managing the registered profiles: rename, forget (without touching disk
/// files), open the data folder in Explorer, and pick the autostart profile (§4.6 —
/// two-mode toggle, NOT a per-profile checkbox grid: only ONE of "LastUsed" or a single
/// "FixedProfile" is active at a time).
///
/// Lists/refreshes come from a single <see cref="ProfileService"/> instance shared with the
/// rest of the shell (passed in via the constructor). Mutations go straight through that
/// service; on success this window calls <see cref="MainWindow.NotifyProfilesChanged"/> so
/// the tray menu and shell title rebuild.
/// </summary>
public partial class ManageStoragesWindow : Window
{
    private readonly ProfileService _profiles;
    private readonly MainWindow _owner;
    private bool _suppressAutostartEvents;

    public ManageStoragesWindow()
    {
        // Designer / XAML loader fallback only — see CreateStorageDialog for rationale.
        _owner = null!;
        _profiles = null!;
        InitializeComponent();
    }

    public ManageStoragesWindow(MainWindow owner, ProfileService profiles)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        InitializeComponent();

        AutostartLastUsedRadio.IsCheckedChanged += OnAutostartRadioChanged;
        AutostartFixedRadio.IsCheckedChanged += OnAutostartRadioChanged;
        AutostartProfileCombo.SelectionChanged += OnAutostartProfileChanged;

        RefreshAll();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshProfileList();
        RefreshAutostart();
    }

    private void RefreshProfileList()
    {
        var all = _profiles.GetAll();
        var activeId = _owner.ActiveProfileId;
        var rows = new List<ProfileRow>();
        foreach (var p in all)
        {
            var isActive = !string.IsNullOrEmpty(activeId)
                && string.Equals(p.Id, activeId, StringComparison.Ordinal);
            rows.Add(new ProfileRow
            {
                Id = p.Id,
                DisplayName = p.Name,
                DataPath = p.DataPath,
                IsActive = isActive,
                ActiveBadge = isActive ? "● активное" : string.Empty,
            });
        }

        ProfilesList.ItemsSource = new AvaloniaList<ProfileRow>(rows);
    }

    private void RefreshAutostart()
    {
        _suppressAutostartEvents = true;
        try
        {
            // Combo always lists all profiles so the user can pick any as the fixed target.
            var all = _profiles.GetAll();
            AutostartProfileCombo.ItemsSource = all
                .Select(p => new ComboBoxItem { Content = p.Name, Tag = p.Id })
                .ToList();

            if (_profiles.AutostartMode == AutostartMode.FixedProfile
                && !string.IsNullOrEmpty(_profiles.AutostartProfileId))
            {
                AutostartFixedRadio.IsChecked = true;
                AutostartLastUsedRadio.IsChecked = false;
                SelectComboById(_profiles.AutostartProfileId);
                AutostartHint.Text = "При автозапуске всегда открывается выбранное хранилище.";
            }
            else
            {
                AutostartLastUsedRadio.IsChecked = true;
                AutostartFixedRadio.IsChecked = false;
                AutostartProfileCombo.SelectedIndex = -1;
                AutostartHint.Text = "При автозапуске открывается последнее активно использованное хранилище.";
            }
        }
        finally
        {
            _suppressAutostartEvents = false;
        }
    }

    private void SelectComboById(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            AutostartProfileCombo.SelectedIndex = -1;
            return;
        }
        var items = AutostartProfileCombo.ItemsSource as System.Collections.IList;
        if (items == null) return;
        int idx = -1;
        int i = 0;
        foreach (var it in items)
        {
            if (it is ComboBoxItem cbi && cbi.Tag is string tagId
                && string.Equals(tagId, id, StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
            i++;
        }
        AutostartProfileCombo.SelectedIndex = idx;
    }

    private string? GetSelectedAutostartProfileId()
    {
        return AutostartProfileCombo.SelectedItem is ComboBoxItem cbi ? cbi.Tag as string : null;
    }

    private void OnAutostartRadioChanged(object? sender, EventArgs e)
    {
        if (_suppressAutostartEvents) return;

        if (AutostartFixedRadio.IsChecked == true)
        {
            var id = GetSelectedAutostartProfileId();
            if (string.IsNullOrEmpty(id))
            {
                // No profile selected yet — let the combo selection drive this once picked.
                AutostartHint.Text = "Выберите хранилище из списка.";
                return;
            }
            ApplyAutostart(AutostartMode.FixedProfile, id);
        }
        else if (AutostartLastUsedRadio.IsChecked == true)
        {
            ApplyAutostart(AutostartMode.LastUsed, null);
        }
    }

    private void OnAutostartProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAutostartEvents) return;
        if (AutostartFixedRadio.IsChecked != true)
        {
            // Switch to "fixed" mode as soon as the user picks a profile, since picking is an
            // explicit act that signals intent.
            AutostartFixedRadio.IsChecked = true;
        }

        var id = GetSelectedAutostartProfileId();
        if (!string.IsNullOrEmpty(id))
        {
            ApplyAutostart(AutostartMode.FixedProfile, id);
        }
    }

    private void ApplyAutostart(AutostartMode mode, string? fixedId)
    {
        try
        {
            _profiles.SetAutostart(mode, fixedId);
            _owner.NotifyProfilesChanged();
            RefreshAutostart();
        }
        catch (Exception ex)
        {
            AutostartHint.Text = $"Ошибка: {ex.Message}";
        }
    }

    // ── Per-row actions ──────────────────────────────────────────────────────────

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            await RenameAsync(id);
        }
    }

    private async Task RenameAsync(string id)
    {
        ProfileEntry profile;
        try { profile = _profiles.GetById(id); }
        catch (KeyNotFoundException) { RefreshAll(); return; }

        // ShowDialog(owner) sets Owner internally (protected); no direct assignment here.
        var dialog = new RenameStorageDialog(profile.Name);
        var newName = await dialog.ShowDialog<string?>(this);

        if (string.IsNullOrWhiteSpace(newName)) return;

        try
        {
            _profiles.RenameProfile(id, newName);
            _owner.NotifyProfilesChanged();
            RefreshAll();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Не удалось переименовать", ex.Message);
        }
    }

    private async void OnForgetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            await ForgetAsync(id);
        }
    }

    private async Task ForgetAsync(string id)
    {
        ProfileEntry profile;
        try { profile = _profiles.GetById(id); }
        catch (KeyNotFoundException) { RefreshAll(); return; }

        // The confirmation text is the brief's literal: it explicitly tells the user the
        // data stays on disk and shows the path, because ProfileService.ForgetProfile only
        // removes the registry pointer.
        var confirm = new ConfirmForgetDialog(profile.Name, profile.DataPath);
        var ok = await confirm.ShowDialog<bool>(this);
        if (!ok) return;

        try
        {
            _profiles.ForgetProfile(id);
            _owner.NotifyProfilesChanged();
            RefreshAll();
        }
        catch (InvalidOperationException ioex)
        {
            // Last-profile case — the brief: "не дай кнопке просто молча не сработать".
            await ShowMessageAsync("Невозможно забыть хранилище", ioex.Message);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Не удалось забыть хранилище", ex.Message);
        }
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;

        ProfileEntry profile;
        try { profile = _profiles.GetById(id); }
        catch (KeyNotFoundException) { RefreshAll(); return; }

        OpenFolder(profile.DataPath);
    }

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Windows-only as the rest of the shell, but still guarded: matches PowerEventsService
        // / other Windows hooks. On other OSes the call silently no-ops (we don't have a
        // cross-platform "open folder" abstraction in this app yet).
        if (!OperatingSystem.IsWindows()) return;

        // Ensure the dir exists so Explorer does not error out on a vault that was never
        // started (ProfileService.AddProfile creates the auto path, but an explicit path or
        // a forgotten-but-not-deleted profile might not).
        try { Directory.CreateDirectory(path); }
        catch { /* best-effort; let explorer.exe handle whatever it can */ }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open folder '{path}': {ex.Message}");
        }
    }

    private async Task ShowMessageAsync(string title, string body)
    {
        var dlg = new MessageDialog(title, body);
        await dlg.ShowDialog(this);
    }
}
