using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
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
