using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

public class SlotViewModel
{
    public required MasterKeyStore Slot { get; init; }
    public required bool CanDelete { get; init; }

    /// <summary>The display name of the person a recovery key belongs to (2.12); null for a key issued before, whose owner nobody recorded.</summary>
    public string? OwnerName { get; init; }

    /// <summary>The <c>user</c> slot this phone's Debug dev password added (<see cref="DevSlot"/>).</summary>
    public bool IsDevSlot { get; init; }

    public string Icon => Slot.SlotType switch
    {
        "password" => "🔑",
        "user" => "👤",
        "dev" => "🛠",
        "recovery" => "🆘",
        _ => "🔐"
    };

    public string DisplayName => Slot.SlotType switch
    {
        "password" => AppStrings.Get("SlotMain"),
        "user" => AppStrings.Get(IsDevSlot ? "SlotDev" : "SlotUser"),
        "dev" => AppStrings.Get("SlotDev"),
        "recovery" => OwnerName is { } owner ? AppStrings.Format("SlotRecoveryOwnerFormat", owner) : AppStrings.Get("SlotRecoveryNoOwner"),
        _ => Slot.SlotType
    };

    public string CreatedAtFormatted => AppStrings.Format("SlotCreatedFormat", Slot.CreatedAt.ToString("yyyy-MM-dd HH:mm"));
}

/// <summary>
/// The mock-up's Settings screen: sign-in (fingerprint, change master password), lock (leave / idle /
/// now, on the shared auto-lock policy), recovery, storage on the phone (the encryption offer), trash,
/// appearance, about this device and the danger zone (the reset-node flow). The password slots and the
/// dev password are a Debug build only — the design has no place for them, but the real feature stays
/// reachable there.
/// </summary>
public partial class SecurityPage : ContentPage
{
    private readonly IServiceProvider _services;
    private bool _loading;
    private string _nodeId = "";
    private string _publicKey = "";

    // The mock-up's idle choices (1 / 5 / 15 minutes / don't lock), on the shared policy's timeout.
    private static readonly TimeSpan[] IdleChoices =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), AutoLockService.Never];

    public SecurityPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

        // Appearance: the mock-up's order (dark, light, system), the saved choice highlighted.
        ThemeSegments.Items =
        [
            AppStrings.Get("ThemeDark"),
            AppStrings.Get("ThemeLight"),
            AppStrings.Get("ThemeSystem"),
        ];
        ThemeSegments.SelectedIndex = ThemeManager.Saved switch
        {
            ThemeManager.Light => 1,
            ThemeManager.System => 2,
            _ => 0,
        };
        ThemeSegments.SelectionChanged += (_, index) => ThemeManager.Set(index switch
        {
            1 => ThemeManager.Light,
            2 => ThemeManager.System,
            _ => ThemeManager.Dark,
        });

    }

    // The idle choices' words, in the same order as IdleChoices.
    private static string[] IdleLabels() =>
    [
        AppStrings.Get("LockIdle1"),
        AppStrings.Get("LockIdle5"),
        AppStrings.Get("LockIdle15"),
        AppStrings.Get("LockIdleNever"),
    ];

    /// <summary>
    /// The reads below run inside the unlocked shell, but the idle timer, leaving the app or Lock now can close an encrypted database under one of
    /// them: this async void catches, looks again after every await whether the vault is still unlocked, and stops quietly
    /// when it is not - the unlock page takes over. A failure on an open vault is logged and said in the app's own sentence.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            _loading = true;

            var biometric = _services.GetService<IBiometricService>();
            FingerprintSwitch.IsEnabled = biometric is not null && await biometric.IsAvailableAsync();
            FingerprintSwitch.IsToggled = biometric?.HasStoredPassword() ?? false;

            var autoLock = _services.GetRequiredService<AutoLockService>();
            LockOnLeaveSwitch.IsToggled = autoLock.LockOnLeave;
            IdleValueLabel.Text = IdleLabels()[Math.Max(0, Array.IndexOf(IdleChoices, autoLock.Idle))];

            _loading = false;

            if (!IsUnlocked) return;
            await LoadAboutAsync();
            if (!IsUnlocked) return;
            await LoadTrashCountAsync();
            if (!IsUnlocked) return;
            await LoadSlotsAsync();
        }
        catch (Exception ex)
        {
            _loading = false;
            var unlocked = IsUnlocked;
            System.Diagnostics.Debug.WriteLine($"Preparing the settings page failed (vault unlocked: {unlocked}): {ex}");
            if (unlocked) await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
    }

    /// <summary>The vault is unlocked now: the page reads only then, and looks again after every await.</summary>
    private bool IsUnlocked => _services.GetService<SessionService>()?.IsUnlocked == true;

    // ---- sign-in ----

    private async void OnFingerprintToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        var biometric = _services.GetService<IBiometricService>();
        if (biometric is null) { RevertFingerprint(false); return; }

        if (e.Value)
        {
            if (!await biometric.IsAvailableAsync())
            {
                await AppDialog.AlertAsync(this, AppStrings.Get("SettingsFingerprint"), AppStrings.Get("FingerprintNotAvailable"));
                RevertFingerprint(false);
                return;
            }
            var password = await this.ShowInputPopupAsync(
                AppStrings.Get("SettingsFingerprint"), AppStrings.Get("FingerprintEnablePrompt"),
                accept: AppStrings.Get("Save"), cancel: AppStrings.Get("Cancel"), isPassword: true);
            if (string.IsNullOrEmpty(password)) { RevertFingerprint(false); return; }

            // A check that opens nothing, behind the one busy helper (the derivation takes a moment).
            var ok = false;
            await BusyAction.RunAsync(null, async () =>
            {
                ok = await _services.GetRequiredService<SessionService>().VerifyMasterPasswordAsync(password);
            });
            if (!ok)
            {
                await AppDialog.AlertAsync(this, AppStrings.Get("SettingsFingerprint"), AppStrings.Get("FingerprintWrongPassword"));
                RevertFingerprint(false);
                return;
            }
            // The system biometric prompt authorizes storing the password; it is Android's own window.
            var stored = await biometric.StorePasswordAsync(password);
            if (!stored)
            {
                await AppDialog.AlertAsync(this, AppStrings.Get("SettingsFingerprint"), AppStrings.Get("FingerprintStoreFailed"));
                RevertFingerprint(false);
            }
        }
        else
        {
            biometric.Clear();
        }
    }

    private void RevertFingerprint(bool value)
    {
        _loading = true;
        FingerprintSwitch.IsToggled = value;
        _loading = false;
    }

    // busy-exempt: opens the change-password dialog; the change itself runs behind BusyAction in the dialog
    private async void OnChangeMasterClicked(object? sender, EventArgs e)
    {
        var dialog = new ChangeMasterPasswordDialog(_services);
        await Navigation.PushModalAsync(dialog);
        if (await dialog.ResultTask)
            await AppDialog.AlertAsync(this, AppStrings.Get("SettingsChangeMaster"), AppStrings.Get("PasswordChanged"));
    }

    // ---- lock ----

    private void OnLockOnLeaveToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        _services.GetRequiredService<AutoLockService>().LockOnLeave = e.Value;
    }

    // busy-exempt: opens the idle-choice bottom sheet; picking one sets the timeout at once, nothing is sent and nothing waits
    private async void OnIdleFieldTapped(object? sender, EventArgs e)
    {
        var autoLock = _services.GetRequiredService<AutoLockService>();
        var labels = IdleLabels();
        var current = Math.Max(0, Array.IndexOf(IdleChoices, autoLock.Idle));
        var resources = Application.Current?.Resources;
        var stack = new VerticalStackLayout { Spacing = 8 };
        BottomSheet.BottomSheetPage? sheet = null;
        for (int i = 0; i < labels.Length; i++)
        {
            var idx = i;
            Style? style = null;
            if (resources?.TryGetValue(idx == current ? "PrimaryButton" : "SecondaryButton", out var s) == true)
                style = s as Style;
            var choice = new Button { Text = labels[idx], Style = style };
            choice.Clicked += async (_, _) =>
            {
                // Close the sheet first, then apply the choice (consistent with the other sheets).
                if (sheet != null) await sheet.CloseAsync();
                autoLock.Idle = IdleChoices[idx];
                IdleValueLabel.Text = labels[idx];
            };
            stack.Children.Add(choice);
        }
        sheet = new BottomSheet.BottomSheetPage(stack, AppStrings.Get("SettingsLockIdle"));
        await Navigation.PushModalAsync(sheet);
    }

    private async void OnLockNowClicked(object? sender, EventArgs e)
    {
        // The one busy helper (BMB-311): the button is off and spins until the lock page is shown.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            _services.GetRequiredService<AutoLockService>().LockNow();
            await Shell.Current.GoToAsync("//unlock");
        });
    }

    // ---- recovery / trash ----

    // busy-exempt: opens the recovery-key flow; its work runs behind BusyAction there
    private async void OnNewRecoveryClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("recoveryKey");

    // No encryption offer here: the database is encrypted by itself right after a create
    // or a join (SetupPage + PhoneSealing); an unlock never encrypts.

    // busy-exempt: opens the trash; the restore work runs behind BusyAction there
    private async void OnOpenTrashClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("trash");

    private async Task LoadTrashCountAsync()
    {
        try
        {
            var count = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var page = await scope.ServiceProvider.GetRequiredService<HardDeleteService>()
                    .ListAsync(1, 200, null, HardDeleteStatusFilter.DeletedOnly, CancellationToken.None);
                return page.Items.Count(i => i.Type == "article" && i.Id is not null);
            });
            TrashCountLabel.Text = AppStrings.Format("SettingsTrashCountFormat", count);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadTrashCount error: {ex.Message}");
        }
    }

    // ---- about this device ----

    private async Task LoadAboutAsync()
    {
        var identity = await Task.Run(async () =>
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
        });
        if (!IsUnlocked) return;
        AboutName.Text = identity?.DisplayName ?? "";
        _nodeId = identity?.NodeId.ToString() ?? "";
        _publicKey = identity is null ? "" : Convert.ToBase64String(identity.Ed25519PublicKey);
        AboutId.Text = _nodeId;
        AboutKey.Text = _publicKey;
        AboutVersion.Text = AppInfo.Current.VersionString;
        await ShowOwnerAsync(identity?.NodeId);
    }

    /// <summary>Whose device this is (2.12 device ownership), when the bank has more than one person who holds the key; display only.</summary>
    private async Task ShowOwnerAsync(Guid? self)
    {
        AboutOwnerRow.IsVisible = false;
        if (self is not { } nodeId) return;
        try
        {
            var line = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var sp = scope.ServiceProvider;
                return await DeviceOwnerLine.ReadAsync(sp.GetRequiredService<IDbConnectionFactory>(), sp.GetRequiredService<IWhitelistRepository>(), nodeId);
            });
            if (line is null) return;
            AboutOwner.Text = line.Ownership == OwnershipKinds.Shared ? AppStrings.Get("SettingsDeviceShared") : line.OwnerLabel;
            AboutOwnerRow.IsVisible = true;
        }
        catch (Exception ex)
        {
            // Display only: the row stays hidden.
            System.Diagnostics.Debug.WriteLine("Reading whose device this is failed: " + ex.GetType().Name);
        }
    }

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyIdClicked(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_nodeId);
        await AppDialog.AlertAsync(this, AppStrings.Get("SettingsDeviceId"), AppStrings.Get("IdCopied"));
    }

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyKeyClicked(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_publicKey);
        await AppDialog.AlertAsync(this, AppStrings.Get("SettingsDeviceKey"), AppStrings.Get("KeyCopied"));
    }

    // ---- danger zone: the reset-node flow ----

    private async void OnWipeClicked(object? sender, EventArgs e)
    {
        var confirmed = await AppDialog.ConfirmAsync(this, AppStrings.Get("WipeTitle"), AppStrings.Get("WipeDesc"),
            AppStrings.Get("Delete"), AppStrings.Get("Cancel"), danger: true);
        if (!confirmed) return;

        // A long operation (BMB-311): the one busy helper puts up the pop-up until the app restarts.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            App.StopSyncService();

            // Clear the biometric stored password and the ingest key (bound to the old node identity).
            _services.GetService<IBiometricService>()?.Clear();
            Preferences.Default.Remove(DevSlot.PreferenceKey);
            _services.GetService<IIngestKeyStore>()?.Clear();

            // The whole vault file set (boot store, both databases, the hint mirror, the seal journal, every sidecar - an orphaned -wal next to a
            // fresh db gets replayed on the next open): deleting the database alone bricked an encrypted phone.
            await PhoneWipe.ReturnToPlainAsync(_services);

            // Restart the app process — the only reliable way to reinitialize all singletons and
            // re-run migrations on the fresh database.
#if ANDROID
            var intent = global::Android.App.Application.Context.PackageManager!
                .GetLaunchIntentForPackage(global::Android.App.Application.Context.PackageName!)!;
            intent.AddFlags(global::Android.Content.ActivityFlags.ClearTop | global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
#else
            await Shell.Current.GoToAsync("//setup");
#endif
        }, new BusyOptions
        {
            Long = true,
            Title = AppStrings.Get("WipingTitle"),
            Message = AppStrings.Get("WipingMessage"),
            FailureTitle = AppStrings.Get("WipeFailedTitle"),
        });
    }

    // ---- password slots (a Debug build only) ----

    private async Task LoadSlotsAsync()
    {
#if DEBUG
        try
        {
            var (slots, users) = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
                return (await repo.GetAllAsync(), await scope.ServiceProvider.GetRequiredService<IUserRepository>().ListActiveAsync());
            });

            SlotsCard.IsVisible = true;
            // The dev password is an extra "user" slot no account signs in with (the vault's slot API takes no other type, as on the iPhone); the card
            // remembers which one it added, and offers to delete no other "user" slot: one no account points at may still be a person's door.
            var loginSlots = users.Where(u => u.KeySlotId.HasValue).Select(u => u.KeySlotId!.Value).ToHashSet();
            var remembered = Preferences.Default.Get(DevSlot.PreferenceKey, -1);
            int? devSlotId = slots.Any(s => s.SlotId == remembered && s.SlotType == "user" && !loginSlots.Contains(s.SlotId)) ? remembered : null;
            // The dev password is a testing tool: the interface offers it in a Debug build only (product rule 7).
            AddDevCard.IsVisible = devSlotId is null;

            var names = users.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName);
            var viewModels = slots.Select(s => new SlotViewModel
            {
                Slot = s,
                CanDelete = DevSlot.CanDelete(s, loginSlots, devSlotId, slots.Count),
                OwnerName = s.OwnerUserId is { } owner && names.TryGetValue(owner, out var name) ? name : null,
                IsDevSlot = s.SlotId == devSlotId,
            }).ToList();
            SlotsList.ItemsSource = viewModels;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Unlock slots load failed: " + ex);
            await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
#else
        SlotsCard.IsVisible = false;
        AddDevCard.IsVisible = false;
#endif
    }

    private async void OnDeleteSlotClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is SlotViewModel vm)
        {
            var confirm = await AppDialog.ConfirmAsync(this, AppStrings.Get("DeleteSlotTitle"),
                AppStrings.Format("DeleteSlotDescFormat", vm.DisplayName),
                AppStrings.Get("Delete"), AppStrings.Get("Cancel"), danger: true);
            if (!confirm) return;

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
        var confirm = DevPasswordConfirmEntry.Text ?? "";

        if (string.IsNullOrEmpty(password))
        {
            AddErrorLabel.Text = AppStrings.Get("DevPasswordEmpty");
            AddErrorLabel.IsVisible = true;
            return;
        }
        if (password != confirm)
        {
            AddErrorLabel.Text = AppStrings.Get("PasswordMismatch");
            AddErrorLabel.IsVisible = true;
            return;
        }

        await BusyAction.RunAsync(sender as Button, async () =>
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var mgmt = scope.ServiceProvider.GetRequiredService<KeyManagementService>();
                var repo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
                var before = await repo.GetAllAsync();
                // "user": the only type the vault's slot API takes ("dev" was always refused; a recovery key goes through its own issue).
                await mgmt.AddPasswordSlotAsync("user", password);
                // The card remembers which slot is the dev password: the only "user" slot it may offer to delete.
                if (DevSlot.Added(before, await repo.GetAllAsync()) is { } added) Preferences.Default.Set(DevSlot.PreferenceKey, added);
            });
            DevPasswordEntry.Text = "";
            DevPasswordConfirmEntry.Text = "";
            await LoadSlotsAsync();
        }, new BusyOptions { ResultLabel = AddErrorLabel, Success = AppStrings.Get("DevPasswordAdded") });
    }
}
