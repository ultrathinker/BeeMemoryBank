using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

public class SlotViewModel
{
    public required MasterKeyStore Slot { get; init; }
    public required bool CanDelete { get; init; }

    public string Icon => Slot.SlotType switch
    {
        "password" => "🔑",
        "user"     => "👤",
        "dev"      => "🛠",
        "recovery" => "🆘",
        _          => "🔐"
    };

    public string DisplayName => Slot.SlotType switch
    {
        "password" => "Main password",
        "user"     => "User password",
        "dev"      => "Dev password",
        "recovery" => "Recovery key",
        _          => Slot.SlotType
    };

    public string CreatedAtFormatted => $"Created {Slot.CreatedAt:yyyy-MM-dd HH:mm}";
}

public partial class SecurityPage : ContentPage
{
    private readonly IServiceProvider _services;

    public SecurityPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadSlotsAsync();
        ShowEncryptionCard();
    }

    // ---- Database encryption (docs/design/step-b-spec.md 10.8): offered while PhoneVaultLifecycle.PhoneEncryptionAvailable is true (it is, in this build). ----

    private void ShowEncryptionCard()
    {
        var gate = _services.GetService<IVaultGate>();
        var offered = PhoneVaultLifecycle.PhoneEncryptionAvailable && gate is not null;
        EncryptionCard.IsVisible = offered;
        if (!offered) return;

        var encrypted = gate!.IsSealed;
        EncryptionForm.IsVisible = !encrypted;
        _ = ShowFingerprintOfferAsync(!encrypted);
        EncryptionStatusLabel.Text = encrypted
            ? "This phone's database is encrypted. It closes whenever the app goes to the background, and the master password (or the fingerprint) opens it."
            : "Encrypt the notes on this phone so that a copy of the phone's storage shows nothing. The way back is to remove the vault here and join again from another device.";
    }

    private async void OnEncryptDatabaseClicked(object? sender, EventArgs e)
        => await RunEncryptAsync(EncryptPasswordEntry.Text ?? "", EncryptButton);

    /// <summary>The same confirmation the unlock page offers: the fingerprint hands over the master password the phone stored for it.</summary>
    private async void OnEncryptFingerprintClicked(object? sender, EventArgs e)
    {
        EncryptErrorLabel.IsVisible = false;
        var biometric = _services.GetService<IBiometricService>();
        if (biometric is null) return;
        string? password;
        try { password = await biometric.GetPasswordAsync(); }
        catch (Exception ex)
        {
            ShowEncryptError("Fingerprint: " + ex.Message);
            return;
        }
        if (password is null) return;   // cancelled: nothing to say
        await RunEncryptAsync(password, EncryptFingerprintButton);
    }

    private void OnToggleEncryptPasswordClicked(object? sender, EventArgs e)
    {
        EncryptPasswordEntry.IsPassword = !EncryptPasswordEntry.IsPassword;
        ToggleEncryptPasswordButton.Text = EncryptPasswordEntry.IsPassword ? "Show" : "Hide";
    }

    private async Task ShowFingerprintOfferAsync(bool formVisible)
    {
        var biometric = _services.GetService<IBiometricService>();
        EncryptFingerprintButton.IsVisible = formVisible && biometric is not null && await biometric.IsAvailableAsync() && biometric.HasStoredPassword();
    }

    /// <summary>
    /// A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up with the progress of the rewrite, so nothing can be tapped
    /// a second time; every reason it did not happen is said in the error line under the button.
    /// </summary>
    private async Task RunEncryptAsync(string masterPassword, Button source)
    {
        EncryptErrorLabel.IsVisible = false;
        var seal = _services.GetRequiredService<SealMigrationService>();
        var options = new SealOptions(AcceptNoSnapshot: OnlyCopyCheck.IsChecked);
        await BusyAction.RunAsync(source, async busy =>
        {
            try
            {
                var preflight = await seal.PreflightAsync(options);
                // The tick is only offered when it is the blocker in the way (spec 10.8 phone_no_copy).
                OnlyCopyRow.IsVisible = preflight.Blockers.Any(b => b.Code == SealBlockers.PhoneNoCopy) || OnlyCopyCheck.IsChecked;
                if (!preflight.CanStart)
                {
                    ShowEncryptError(preflight.Blockers[0].Message);
                    return;
                }

                // The screen stays on for as long as the database is rewritten (spec 10.8).
                DeviceDisplay.Current.KeepScreenOn = true;
                await seal.StartAsync(masterPassword, options);
                EncryptProgress.IsVisible = true;
                EncryptStepLabel.IsVisible = true;
                SealProgress? progress;
                do
                {
                    await Task.Delay(500);
                    progress = seal.Progress;
                    EncryptProgress.Progress = (progress?.Percent ?? 0) / 100.0;
                    EncryptStepLabel.Text = progress?.Step ?? "";
                    busy.Report((progress?.Percent ?? 0) / 100.0, progress?.Step ?? "");
                }
                while (progress is null || progress.Phase is SealProgress.Idle or SealProgress.Running);

                if (progress.Phase == SealProgress.Failed) ShowEncryptError(progress.Error ?? "The encryption failed; the phone is unchanged.");
                else ShowEncryptionCard();
            }
            catch (UnauthorizedAccessException ex) when (ex.Message == "The master password is not correct.")
            {
                ShowEncryptError("The master password is not correct.");
            }
            catch (Exception ex) when (ex is not SealMigrationException)
            {
                // Anything else (a missing Android permission, a file the app cannot read) is not a wrong password: say what it is.
                ShowEncryptError("The encryption could not start: " + ex.GetType().Name + ": " + ex.Message);
            }
            catch (SealMigrationException ex)
            {
                ShowEncryptError(ex.Message);
            }
            finally
            {
                DeviceDisplay.Current.KeepScreenOn = false;
                EncryptPasswordEntry.Text = "";
            }
        }, new BusyOptions
        {
            Long = true,
            Title = "Encrypting this phone's database",
            Message = "Please wait. The database is being rewritten; this can take several minutes. The screen stays on. Do not close the app.",
            ResultLabel = EncryptErrorLabel,
        });
    }

    private void ShowEncryptError(string message)
    {
        EncryptErrorLabel.Text = message;
        EncryptErrorLabel.IsVisible = true;
    }

    private async Task LoadSlotsAsync()
    {
        try
        {
            var slots = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Interfaces.IKeySlotRepository>();
                return await repo.GetAllAsync();
            });

            var hasDevSlot = slots.Any(s => s.SlotType == "dev");
            AddDevCard.IsVisible = !hasDevSlot;

            var viewModels = slots.Select(s => new SlotViewModel
            {
                Slot = s,
                CanDelete = s.SlotType != "user" && s.SlotType != "password" && slots.Count > 1
            }).ToList();

            SlotsList.ItemsSource = viewModels;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async void OnDeleteSlotClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is SlotViewModel vm)
        {
            var confirm = await DisplayAlert(
                "Delete slot",
                $"Remove \"{vm.DisplayName}\"? This password will no longer work.",
                "Delete", "Cancel");

            if (!confirm) return;

            // The one busy helper (BMB-311): the button is off and spins; a failure is said in full in a dialog (a list row has no room for a line).
            await BusyAction.RunAsync(btn, async () =>
            {
                await Task.Run(async () =>
                {
                    using var scope = _services.CreateScope();
                    var mgmt = scope.ServiceProvider.GetRequiredService<KeyManagementService>();
                    await mgmt.RemoveSlotAsync(vm.Slot.SlotId);
                });

                await LoadSlotsAsync();
            });
        }
    }

    private async void OnAddDevPasswordClicked(object? sender, EventArgs e)
    {
        AddErrorLabel.IsVisible = false;

        var password = DevPasswordEntry.Text ?? "";
        var confirm  = DevPasswordConfirmEntry.Text ?? "";

        if (string.IsNullOrEmpty(password))
        {
            AddErrorLabel.Text = "Enter a dev password.";
            AddErrorLabel.IsVisible = true;
            return;
        }

        if (password != confirm)
        {
            AddErrorLabel.Text = "Passwords don't match.";
            AddErrorLabel.IsVisible = true;
            return;
        }

        // The one busy helper (BMB-311): the result, good or bad, is written under the button.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var mgmt = scope.ServiceProvider.GetRequiredService<KeyManagementService>();
                await mgmt.AddPasswordSlotAsync("dev", password);
            });

            DevPasswordEntry.Text = "";
            DevPasswordConfirmEntry.Text = "";

            await LoadSlotsAsync();
        }, new BusyOptions { ResultLabel = AddErrorLabel, Success = "Dev password added." });
    }

    private async void OnChangePasswordClicked(object? sender, EventArgs e)
    {
        ChangeErrorLabel.IsVisible = false;

        var oldPwd     = OldPasswordEntry.Text ?? "";
        var newPwd     = NewPasswordEntry.Text ?? "";
        var confirmPwd = NewPasswordConfirmEntry.Text ?? "";

        if (string.IsNullOrEmpty(oldPwd) || string.IsNullOrEmpty(newPwd))
        {
            ChangeErrorLabel.Text = "Fill in all fields.";
            ChangeErrorLabel.IsVisible = true;
            return;
        }

        if (newPwd != confirmPwd)
        {
            ChangeErrorLabel.Text = "New passwords don't match.";
            ChangeErrorLabel.IsVisible = true;
            return;
        }

        // A long operation (BMB-311): the key is derived and re-wrapped; the one busy helper puts up the pop-up and writes the result under the button.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var userService = scope.ServiceProvider.GetRequiredService<UserService>();
                var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var users = await userRepo.ListActiveAsync();
                var currentUser = users.FirstOrDefault(u => u.Role == UserRoles.Superadmin)
                    ?? throw new InvalidOperationException("No superadmin user found");
                await userService.ChangePasswordAsync(currentUser.Id, oldPwd, newPwd);
            });

            OldPasswordEntry.Text = "";
            NewPasswordEntry.Text = "";
            NewPasswordConfirmEntry.Text = "";
        }, new BusyOptions
        {
            Long = true,
            Title = "Changing the password",
            Message = "Please wait. The new password is being applied to the keys of this phone.",
            ResultLabel = ChangeErrorLabel,
            Success = "Password changed.",
        });
    }
}
