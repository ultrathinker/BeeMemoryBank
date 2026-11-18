using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Platforms.Android;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

/// <summary>
/// The mock-up's new-recovery-key flow, on the core service the iPhone and the web admin use: confirm
/// the master password (a check that opens nothing), then <see cref="KeyManagementService.AddRecoveryKeyAsync"/>
/// makes a new key. Since 2.12 a recovery key is personal: it is issued for the phone's administrator, and on a
/// phone with several, for the one whose own door the password typed here opens (<see cref="PhoneOwner"/>). The new key is ADDED alongside the existing passwords and recovery keys — it does
/// not invalidate them (documented product behaviour); only a data-key rotation in the server admin
/// makes the old recovery keys stop working. The key is shown once, copied or saved only on request,
/// and forgotten when the page is left.
/// </summary>
public partial class RecoveryKeyPage : ContentPage
{
    private readonly IServiceProvider _services;
    private string? _key;
    private int? _owner;

    public RecoveryKeyPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    private void ShowStep(int step)
    {
        Step1.IsVisible = step == 1;
        Step2.IsVisible = step == 2;
        Step3.IsVisible = step == 3;
    }

    // busy-exempt: returns to Settings without doing any work
    private async void OnCancelClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

    private void OnBackClicked(object? sender, EventArgs e) => ShowStep(1);

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        PasswordError.IsVisible = false;
        var password = PasswordEntry.Text ?? "";
        if (password.Length < 8)
        {
            PasswordError.Text = AppStrings.Get("RecoveryPasswordError");
            PasswordError.IsVisible = true;
            return;
        }

        // A check that opens nothing (the session is already unlocked; this only confirms the person
        // knows the password before a new key is made).
        var verified = false;
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            var session = _services.GetRequiredService<SessionService>();
            verified = await session.VerifyMasterPasswordAsync(password);
            if (!verified) return;
            using var scope = _services.CreateScope();
            _owner = await Task.Run(() => PhoneOwner.ForPasswordAsync(scope.ServiceProvider, password));
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });

        if (!verified)
        {
            PasswordError.Text = AppStrings.Get("FingerprintWrongPassword");
            PasswordError.IsVisible = true;
            return;
        }
        PasswordEntry.Text = "";
        ShowStep(2);
    }

    private async void OnCreateKeyClicked(object? sender, EventArgs e)
    {
        string? key = null;
        var ran = await BusyAction.RunAsync(sender as Button, async () =>
        {
            key = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var sp = scope.ServiceProvider;
                var name = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.DisplayName;
                return await PhoneOwner.IssueRecoveryKeyAsync(sp.GetRequiredService<KeyManagementService>(), _owner, name);
            });
        }, new BusyOptions
        {
            Long = true,
            Title = AppStrings.Get("RecoveryCreatingTitle"),
            Message = AppStrings.Get("RecoveryCreatingMessage"),
            FailureTitle = AppStrings.Get("ErrorTitle"),
        });

        if (ran && key is not null)
        {
            _key = key;
            KeyLabel.Text = FormatKey(key);
            SavedCheck.IsChecked = false;
            DoneButton.IsEnabled = false;
            ShowStep(3);
        }
    }

    // Groups of four are easier to copy by hand; the key itself is the plain Base64 (spaces not part of it).
    private static string FormatKey(string key) =>
        string.Join(" ", Enumerable.Range(0, (key.Length + 3) / 4)
            .Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyClicked(object? sender, EventArgs e)
    {
        if (_key is null) return;
        await Clipboard.Default.SetTextAsync(_key);
        await AppDialog.AlertAsync(this, AppStrings.Get("RecoveryCopy"), AppStrings.Get("RecoveryKeyCopied"));
    }

    // busy-exempt: opens the system file picker; writing the one chosen file is the picker's own work
    private async void OnSaveFileClicked(object? sender, EventArgs e)
    {
        if (_key is null) return;
        var content = AppStrings.Format("RecoveryKeyFileContent", _key);
        var saved = await TextFileSaver.SaveTextAsync(AppStrings.Get("RecoveryKeyFileName"), content);
        if (saved)
            await AppDialog.AlertAsync(this, AppStrings.Get("RecoverySaveFile"), AppStrings.Get("RecoveryKeyFileSaved"));
    }

    private void OnSavedCheckedChanged(object? sender, CheckedChangedEventArgs e) => DoneButton.IsEnabled = e.Value;

    // busy-exempt: forgets the key and returns to Settings; no work that waits
    private async void OnDoneClicked(object? sender, EventArgs e)
    {
        _key = null;
        KeyLabel.Text = "";
        await Shell.Current.GoToAsync("..");
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // The key is shown once: leaving the page forgets it.
        _key = null;
        KeyLabel.Text = "";
    }
}
