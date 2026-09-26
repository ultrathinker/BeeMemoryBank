using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BeeMemoryBank.AppPaths;
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
/// Native "Profiles" window: create or add profiles, and per profile rename, move the data
/// folder, open it in Explorer or forget it (without touching disk files). Which profile opens
/// at startup lives in the Settings window.
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

        // Keep the list live if the user switches profiles from the TRAY while this window
        // stays open, rather than only refreshing on this window's own actions. The safety
        // check in ForgetAsync reads _owner.ActiveProfileId directly (not a cached copy), so
        // it is correct even without this - this is purely so the displayed list does not
        // visibly lag behind reality.
        _owner.ActiveProfileChanged += OnOwnerActiveProfileChanged;
        Closed += (_, _) => _owner.ActiveProfileChanged -= OnOwnerActiveProfileChanged;

        RefreshProfileList();
    }

    private void OnOwnerActiveProfileChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshProfileList);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshProfileList();
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
                ActiveBadge = isActive ? "● open now" : string.Empty,
            });
        }

        ProfilesList.ItemsSource = new AvaloniaList<ProfileRow>(rows);
    }

    // ── Header actions ───────────────────────────────────────────────────────────

    private async void OnNewProfileClick(object? sender, RoutedEventArgs e)
    {
        try { await ProfileCommands.NewProfileAsync(this, _owner); }
        catch (Exception ex) { await ShowMessageAsync("Could not create the profile", ex.Message); }
        RefreshProfileList();
    }

    private async void OnAddExistingClick(object? sender, RoutedEventArgs e)
    {
        try { await ProfileCommands.AddExistingAsync(this, _owner); }
        catch (Exception ex) { await ShowMessageAsync("Could not add the profile", ex.Message); }
        RefreshProfileList();
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
        catch (KeyNotFoundException) { RefreshProfileList(); return; }

        // ShowDialog(owner) sets Owner internally (protected); no direct assignment here.
        var dialog = new RenameStorageDialog(profile.Name);
        var newName = await dialog.ShowDialog<string?>(this);

        if (string.IsNullOrWhiteSpace(newName)) return;

        try
        {
            _profiles.RenameProfile(id, newName);
            _owner.NotifyProfilesChanged();
            RefreshProfileList();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not rename the profile", ex.Message);
        }
    }

    private async void OnMoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            await MoveAsync(id);
        }
    }

    private async Task MoveAsync(string id)
    {
        ProfileEntry profile;
        try { profile = _profiles.GetById(id); }
        catch (KeyNotFoundException) { RefreshProfileList(); return; }

        var picked = await FolderPicker.PickAsync(this, $"Where should profile “{profile.Name}” live?");
        if (picked == null) return;

        // An empty (or new) folder is used as is. A folder with other things in it, like a
        // whole drive, gets a subfolder named after the profile instead of mixing the vault
        // into unrelated files. The confirmation below shows the exact final path.
        var target = VaultFiles.Inspect(picked) is VaultFolderState.Missing or VaultFolderState.Empty
            ? picked
            : Path.Combine(picked, SafeFolderName(profile.Name));

        var error = VaultCopier.ValidateTarget(profile.DataPath, target);
        if (error != null)
        {
            await ShowMessageAsync("Cannot move the profile here", $"{error}\n\n{target}");
            return;
        }

        var isActive = string.Equals(id, _owner.ActiveProfileId, StringComparison.Ordinal);
        var confirm = new ConfirmDialog(
            $"Move profile “{profile.Name}”?",
            $"From:\n{profile.DataPath}\n\nTo:\n{target}\n\n" +
            (isActive ? "The profile closes for a moment while its data is copied, then opens again from the new folder.\n\n" : string.Empty) +
            "The old folder is kept until you decide what to do with it.",
            "Move", "Cancel");
        if (!await confirm.ShowDialog<bool>(this)) return;

        var originalTitle = Title;
        IsEnabled = false;
        Title = "Profiles — moving...";
        Services.RelocateResult result;
        try
        {
            result = await _owner.MoveProfileAsync(id, target);
        }
        finally
        {
            IsEnabled = true;
            Title = originalTitle;
            RefreshProfileList();
        }

        if (!result.Success)
        {
            await ShowMessageAsync("Could not move the profile", result.ErrorMessage ?? "Unknown error.");
            return;
        }

        await OfferToDeleteOldCopyAsync(result.Profile!, result.OldDataPath!);
    }

    private async Task OfferToDeleteOldCopyAsync(ProfileEntry moved, string oldPath)
    {
        var ask = new ConfirmDialog(
            "Profile moved",
            $"“{moved.Name}” now lives in:\n{moved.DataPath}\n\n" +
            $"The old copy is still in:\n{oldPath}\n\n" +
            "Delete the old copy? Keep it if you want a backup; you can delete it yourself later.",
            "Delete old copy", "Keep it");
        if (!await ask.ShowDialog<bool>(this)) return;

        // Never delete a folder some profile still points at, or one that is not a vault.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (_profiles.GetAll().Any(p => string.Equals(p.DataPath, oldPath, comparison))
            || VaultFiles.Inspect(oldPath) != VaultFolderState.Vault)
        {
            await ShowMessageAsync("The old copy was kept", $"It is not safe to delete this folder automatically:\n{oldPath}");
            return;
        }

        try
        {
            await Task.Run(() => Directory.Delete(oldPath, recursive: true));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not delete the old copy", $"{ex.Message}\n\n{oldPath}");
        }
    }

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "BeeMemoryBank" : $"BeeMemoryBank - {cleaned}";
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
        catch (KeyNotFoundException) { RefreshProfileList(); return; }

        // Forgetting the ACTIVE profile removes the registry pointer while the node for it
        // is still running: the next switch would pass a currentProfileId ProfileService can
        // no longer resolve, and the switch engine would skip StopAsync entirely (nothing to
        // find A's ownership through) while starting a new node - orphaning the still-running
        // one. Refuse outright, same defensive stance as ProfileService's own
        // "cannot forget the last profile" guard.
        if (!string.IsNullOrEmpty(_owner.ActiveProfileId)
            && string.Equals(id, _owner.ActiveProfileId, StringComparison.Ordinal))
        {
            await ShowMessageAsync("Cannot forget this profile",
                $"Profile “{profile.Name}” is open right now. Switch to another profile first, then try again.");
            return;
        }

        // The confirmation explicitly tells the user the data stays on disk and shows the
        // path, because ProfileService.ForgetProfile only removes the registry pointer.
        var confirm = new ConfirmForgetDialog(profile.Name, profile.DataPath);
        var ok = await confirm.ShowDialog<bool>(this);
        if (!ok) return;

        try
        {
            _profiles.ForgetProfile(id);
            _owner.NotifyProfilesChanged();
            RefreshProfileList();
        }
        catch (InvalidOperationException ioex)
        {
            // Last-profile case — the button must never silently do nothing.
            await ShowMessageAsync("Cannot forget this profile", ioex.Message);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not forget the profile", ex.Message);
        }
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;

        ProfileEntry profile;
        try { profile = _profiles.GetById(id); }
        catch (KeyNotFoundException) { RefreshProfileList(); return; }

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
