using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
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

    // ---- Database encryption (docs/design/step-b-spec.md 10.8): hidden until the native release gate of 12.4 is green. ----

    private void ShowEncryptionCard()
    {
        var gate = _services.GetService<IVaultGate>();
        var offered = PhoneVaultLifecycle.PhoneEncryptionAvailable && gate is not null;
        EncryptionCard.IsVisible = offered;
        if (!offered) return;

        var encrypted = gate!.IsSealed;
        EncryptionForm.IsVisible = !encrypted;
        EncryptionStatusLabel.Text = encrypted
            ? "This phone's database is encrypted. It closes whenever the app goes to the background, and the master password (or the fingerprint) opens it."
            : "Encrypt the notes on this phone so that a copy of the phone's storage shows nothing. The way back is to remove the vault here and join again from another device.";
    }

    private async void OnEncryptDatabaseClicked(object? sender, EventArgs e)
    {
        EncryptErrorLabel.IsVisible = false;
        var seal = _services.GetRequiredService<SealMigrationService>();
        var options = new SealOptions(AcceptNoSnapshot: OnlyCopyCheck.IsChecked);
        try
        {
            EncryptButton.IsEnabled = false;
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
            await seal.StartAsync(EncryptPasswordEntry.Text ?? "", options);
            EncryptProgress.IsVisible = true;
            EncryptStepLabel.IsVisible = true;
            SealProgress? progress;
            do
            {
                await Task.Delay(500);
                progress = seal.Progress;
                EncryptProgress.Progress = (progress?.Percent ?? 0) / 100.0;
                EncryptStepLabel.Text = progress?.Step ?? "";
            }
            while (progress is null || progress.Phase is SealProgress.Idle or SealProgress.Running);

            if (progress.Phase == SealProgress.Failed) ShowEncryptError(progress.Error ?? "The encryption failed; the phone is unchanged.");
            else ShowEncryptionCard();
        }
        catch (UnauthorizedAccessException)
        {
            ShowEncryptError("The master password is not correct.");
        }
        catch (SealMigrationException ex)
        {
            ShowEncryptError(ex.Message);
        }
        finally
        {
            DeviceDisplay.Current.KeepScreenOn = false;
            EncryptPasswordEntry.Text = "";
            EncryptButton.IsEnabled = true;
        }
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

            try
            {
                await Task.Run(async () =>
                {
                    using var scope = _services.CreateScope();
                    var mgmt = scope.ServiceProvider.GetRequiredService<KeyManagementService>();
                    await mgmt.RemoveSlotAsync(vm.Slot.SlotId);
                });

                await LoadSlotsAsync();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", ex.Message, "OK");
            }
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

        try
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var mgmt = scope.ServiceProvider.GetRequiredService<KeyManagementService>();
                await mgmt.AddPasswordSlotAsync("dev", password);
            });

            DevPasswordEntry.Text = "";
            DevPasswordConfirmEntry.Text = "";

            await DisplayAlert("Done", "Dev password added.", "OK");
            await LoadSlotsAsync();
        }
        catch (Exception ex)
        {
            AddErrorLabel.Text = ex.Message;
            AddErrorLabel.IsVisible = true;
        }
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

        try
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

            await DisplayAlert("Done", "Password changed.", "OK");
        }
        catch (Exception ex)
        {
            ChangeErrorLabel.Text = ex.Message;
            ChangeErrorLabel.IsVisible = true;
        }
    }
}
