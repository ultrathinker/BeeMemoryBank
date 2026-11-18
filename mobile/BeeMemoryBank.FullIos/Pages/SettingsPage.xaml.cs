using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// Settings in the design's card order (the Android SecurityPage's), with only the actions this
/// app has: Face ID sign-in, the two auto-lock choices as app fields with bottom sheets, Lock now
/// inside the lock card, a new recovery key, the encryption offer, the trash, appearance and
/// about this device. The saved settings and their values are exactly the ones AppFlow keeps.
/// </summary>
public partial class SettingsPage : ContentPage
{
    private readonly AppFlow _flow;
    private readonly QuickUnlock _quick;
    private readonly FullVault _vault;
    private readonly NotesService _notes;
    private readonly SecurityOps _security;
    private readonly IServiceProvider _services;
    private bool _loading;
    private string _nodeId = "";
    private string _publicKey = "";

    public SettingsPage(AppFlow flow, QuickUnlock quick, FullVault vault, NotesService notes, SecurityOps security, IServiceProvider services)
    {
        InitializeComponent();
        _flow = flow;
        _quick = quick;
        _vault = vault;
        _notes = notes;
        _security = security;
        _services = services;
        Title = AppStrings.Get("TabSettings");

        // Appearance: the design's order (dark, light, system), the saved choice highlighted.
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

    // The choices' words, in the same order as the policy's choices (the values are unchanged).
    private static string[] GraceLabels() =>
    [
        AppStrings.Get("LockGrace0"),
        AppStrings.Get("LockGrace1"),
        AppStrings.Get("LockGrace5"),
    ];

    private static string[] IdleLabels() =>
    [
        AppStrings.Get("LockIdle2"),
        AppStrings.Get("LockIdle5"),
        AppStrings.Get("LockIdle15"),
    ];

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _loading = true;
        var name = _quick.BiometryName;
        QuickLabel.Text = AppStrings.Format("SettingsFingerprintName", name);
        QuickSwitch.IsEnabled = _quick.IsAvailable;
        QuickSwitch.IsToggled = _quick.IsEnabled;
        QuickDetail.Text = _quick.IsAvailable
            ? AppStrings.Get("SettingsFingerprintDesc")
            : AppStrings.Format("SettingsFingerprintOff", name);

        GraceValue.Text = GraceLabels()[Math.Max(0, Array.IndexOf(AutoLockPolicy.GraceChoices, _flow.Grace))];
        IdleValue.Text = IdleLabels()[Math.Max(0, Array.IndexOf(AutoLockPolicy.IdleChoices, _flow.Idle))];

        // No encryption card: every database on this phone is encrypted by itself (AppFlow.EncryptThenShowMainAsync).
        _loading = false;

        // The reads below run inside the unlocked shell, but an idle lock, the background lock or Lock now can close an encrypted database under
        // one of them: this async void catches, looks again after every await whether the vault is still unlocked, and
        // stops quietly when it is not - the unlock page takes over. A failure on an open vault is logged and said in the app's own sentence.
        try
        {
            if (!_vault.IsUnlocked) return;
            var identity = await _vault.IdentityAsync();
            if (!_vault.IsUnlocked) return;
            // The node's own name (the one the sync page, the unlock page and Android show). The
            // system's DeviceInfo name is only "iPhone" on a real phone since iOS 16.
            AboutName.Text = identity?.DisplayName ?? DeviceInfo.Current.Name;
            _nodeId = identity?.NodeId.ToString() ?? "";
            _publicKey = identity is null ? "" : Convert.ToBase64String(identity.Ed25519PublicKey);
            AboutId.Text = _nodeId;
            AboutKey.Text = _publicKey;
            AboutVersion.Text = AppInfo.Current.VersionString;
            await ShowOwnerAsync(identity?.NodeId);
            if (!_vault.IsUnlocked) return;

            var trash = await Task.Run(() => _notes.TrashAsync());
            if (!_vault.IsUnlocked) return;
            TrashCountLabel.Text = ContentFormat.ItemCount(trash.Count);
            await LoadSlotsAsync();
        }
        catch (Exception ex)
        {
            var unlocked = _vault.IsUnlocked;
            _services.GetService<ILogger<SettingsPage>>()?.LogWarning(ex, "Preparing the settings page failed (vault unlocked: {Unlocked})", unlocked);
            if (unlocked) await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
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
                return await BeeMemoryBank.Sync.DeviceOwnerLine.ReadAsync(sp.GetRequiredService<BeeMemoryBank.Core.Interfaces.IDbConnectionFactory>(),
                    sp.GetRequiredService<BeeMemoryBank.Core.Interfaces.IWhitelistRepository>(), nodeId);
            });
            if (line is null) return;
            AboutOwner.Text = line.Ownership == BeeMemoryBank.Core.Models.OwnershipKinds.Shared ? AppStrings.Get("SettingsDeviceShared") : line.OwnerLabel;
            AboutOwnerRow.IsVisible = true;
        }
        catch (Exception ex)
        {
            // Display only: the row stays hidden.
            System.Diagnostics.Debug.WriteLine("Reading whose device this is failed: " + ex.GetType().Name);
        }
    }

    // ---- change master password ----

    // busy-exempt: opens the dialog; the change runs behind BusyAction inside it
    private async void OnChangeMaster(object? sender, EventArgs e)
    {
        var dialog = new ChangeMasterPasswordDialog(_security);
        await Navigation.PushModalAsync(dialog);
        if (await dialog.ResultTask)
            await AppDialog.AlertAsync(this, AppStrings.Get("SettingsChangeMaster"), AppStrings.Get("PasswordChanged"));
    }

    // ---- about this device: copy id / key ----

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyId(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_nodeId);
        await AppDialog.AlertAsync(this, AppStrings.Get("SettingsDeviceId"), AppStrings.Get("IdCopied"));
    }

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyKey(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_publicKey);
        await AppDialog.AlertAsync(this, AppStrings.Get("SettingsDeviceKey"), AppStrings.Get("KeyCopied"));
    }

    // ---- danger zone ----

    private async void OnWipe(object? sender, EventArgs e)
    {
        if (!await AppDialog.ConfirmAsync(this, AppStrings.Get("WipeTitle"), AppStrings.Get("WipeDesc"),
                AppStrings.Get("Delete"), danger: true)) return;
        await BusyAction.RunAsync(sender as Button, () => _flow.WipeAsync(), new BusyOptions
        {
            Long = true,
            Title = AppStrings.Get("WipingTitle"),
            Message = AppStrings.Get("WipingMessage"),
            FailureTitle = AppStrings.Get("WipeFailedTitle"),
        });
    }

    // ---- password slots / dev password (Debug only, a testing tool) ----

    private async Task LoadSlotsAsync()
    {
#if DEBUG
        try
        {
            var slots = await Task.Run(() => _security.SlotsAsync());
            SlotsCard.IsVisible = true;
            AddDevCard.IsVisible = true;
            SlotsList.Children.Clear();
            var resources = Application.Current?.Resources;
            foreach (var s in slots)
            {
                var row = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 10 };
                row.Children.Add(new Label { Text = SlotIcon(s.SlotType), FontSize = 20, VerticalOptions = LayoutOptions.Center });
                var field = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
                field.Children.Add(new Label { Text = SlotName(s.SlotType, s.Owner), FontAttributes = FontAttributes.Bold, FontSize = 15 });
                field.Children.Add(new Label { Text = AppStrings.Format("SlotCreatedFormat", s.CreatedAt.ToString("yyyy-MM-dd HH:mm")), Style = resources?["ItemMeta"] as Style });
                Grid.SetColumn(field, 1);
                row.Children.Add(field);
                if (s.CanDelete)
                {
                    var del = new Button { Text = AppStrings.Get("Delete"), Style = resources?["TextButton"] as Style, CommandParameter = s, VerticalOptions = LayoutOptions.Center };
                    del.TextColor = ThemeManager.Token("Error");
                    del.Clicked += OnDeleteSlot;
                    Grid.SetColumn(del, 2);
                    row.Children.Add(del);
                }
                SlotsList.Children.Add(row);
            }
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

    private static string SlotIcon(string type) => type switch
    {
        "password" => "🔑",
        "user" => "👤",
        "dev" => "🛠",
        "recovery" => "🆘",
        _ => "🔐",
    };

    private static string SlotName(string type, string? owner = null) => type switch
    {
        "password" => AppStrings.Get("SlotMain"),
        "user" => AppStrings.Get("SlotUser"),
        "dev" => AppStrings.Get("SlotDev"),
        // 2.12: a recovery key is personal; one from before has no owner on record.
        "recovery" => owner is not null ? AppStrings.Format("SlotRecoveryOwnerFormat", owner) : AppStrings.Get("SlotRecoveryNoOwner"),
        "quick_unlock" => AppStrings.Get("SlotQuickUnlock"),
        _ => type,
    };

    private async void OnDeleteSlot(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SlotView vm } btn) return;
        if (!await AppDialog.ConfirmAsync(this, AppStrings.Get("DeleteSlotTitle"),
                AppStrings.Format("DeleteSlotDescFormat", SlotName(vm.SlotType, vm.Owner)), AppStrings.Get("Delete"), danger: true)) return;
        await BusyAction.RunAsync(btn, async () =>
        {
            await Task.Run(() => _security.RemoveSlotAsync(vm.SlotId));
            await LoadSlotsAsync();
        });
    }

    private async void OnAddDevPassword(object? sender, EventArgs e)
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
            // The fields are emptied whatever the add ends in (SecretFields), not only on success.
            await SecretFields.RunAsync(() => Task.Run(() => _security.AddDevSlotAsync(password)),
                () => { DevPasswordEntry.Text = ""; DevPasswordConfirmEntry.Text = ""; });
            await LoadSlotsAsync();
        }, new BusyOptions { ResultLabel = AddErrorLabel, Success = AppStrings.Get("DevPasswordAdded") });
    }

    // busy-exempt: switches the quick unlock of this phone, a local setting (a door of this phone's vault made or removed here); nothing is sent
    private async void OnQuickToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (e.Value) await _quick.EnableAsync();
            else await _quick.DisableAsync();
        }
        catch (Exception ex)
        {
            // Our own refusal ("Face ID is not set up") is said as it is; any other kind is told calmly, never its raw text.
            System.Diagnostics.Debug.WriteLine("Quick unlock toggle failed: " + ex);
            await AppDialog.AlertAsync(this, _quick.BiometryName, BeeMemoryBank.Sync.FailureText.Say(ex));
            _loading = true;
            QuickSwitch.IsToggled = _quick.IsEnabled;
            _loading = false;
        }
    }

    // busy-exempt: opens the choice sheet, nothing is sent and nothing waits
    private async void OnGraceFieldTapped(object? sender, TappedEventArgs e) => await ShowGraceSheetAsync();

    // busy-exempt: opens the choice sheet, nothing is sent and nothing waits
    private async void OnIdleFieldTapped(object? sender, TappedEventArgs e) => await ShowIdleSheetAsync();

    internal Task ShowGraceSheetAsync() => ShowChoiceSheetAsync(AppStrings.Get("SettingsLockOnLeave"), GraceLabels(),
        Math.Max(0, Array.IndexOf(AutoLockPolicy.GraceChoices, _flow.Grace)),
        index => { _flow.Grace = AutoLockPolicy.GraceChoices[index]; GraceValue.Text = GraceLabels()[index]; });

    internal Task ShowIdleSheetAsync() => ShowChoiceSheetAsync(AppStrings.Get("SettingsLockIdle"), IdleLabels(),
        Math.Max(0, Array.IndexOf(AutoLockPolicy.IdleChoices, _flow.Idle)),
        index => { _flow.Idle = AutoLockPolicy.IdleChoices[index]; IdleValue.Text = IdleLabels()[index]; });

    // The app's field + bottom sheet instead of the stock picker: the same choices, the current
    // one highlighted, the sheet closes before the choice is applied (the pop-first lesson).
    // The sheet carries the field's own label as its title, as the Android one does.
    private async Task ShowChoiceSheetAsync(string title, string[] labels, int current, Action<int> apply)
    {
        var resources = Application.Current?.Resources;
        var stack = new VerticalStackLayout { Spacing = 8 };
        var sheet = new BottomSheet.BottomSheetPage(stack, title);
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = new Button { Text = labels[i], HorizontalOptions = LayoutOptions.Fill };
            if (resources is not null && resources.TryGetValue(index == current ? "PrimaryButton" : "SecondaryButton", out var s) && s is Style style)
                button.Style = style;
            button.Clicked += async (_, _) =>
            {
                await sheet.CloseAsync();
                apply(index);
            };
            stack.Children.Add(button);
        }
        var cancel = new Button { Text = AppStrings.Get("Cancel"), HorizontalOptions = LayoutOptions.Fill };
        if (resources is not null && resources.TryGetValue("TextButton", out var ts) && ts is Style textStyle) cancel.Style = textStyle;
        cancel.Clicked += async (_, _) => await sheet.CloseAsync();
        stack.Children.Add(cancel);
        await Navigation.PushModalAsync(sheet);
    }

    private async void OnNewRecoveryKey(object? sender, EventArgs e)
    {
        if (!await AppDialog.ConfirmAsync(this, AppStrings.Get("SettingsRecovery"), AppStrings.Get("SettingsNewRecoveryConfirm"),
                AppStrings.Get("SettingsNewRecoveryAction"))) return;
        // The one busy helper (BMB-311): the button is off and spins through the key derivation; a failure is said in full.
        string? key = null;
        var ran = await BusyAction.RunAsync(sender as Button, async () => key = await Task.Run(_vault.NewRecoveryKeyAsync),
            new BusyOptions { FailureTitle = AppStrings.Get("SettingsRecovery") });
        if (ran && key is not null) _flow.ShowRecoveryKey(key, firstTime: false);
    }

    // busy-exempt: opens the trash, nothing is sent and nothing waits
    private async void OnTrash(object? sender, EventArgs e) =>
        await Navigation.PushAsync(_services.GetRequiredService<TrashPage>());

    private void OnLock(object? sender, EventArgs e) => _flow.LockNow();

#if DEBUG || BMB_E2E
    /// <summary>The end-to-end check photographs the appearance card at the bottom of the page.</summary>
    internal Task ScrollToEndAsync() => Scroller.ScrollToAsync(0, double.MaxValue, false);

    /// <summary>The end-to-end check photographs the recovery button mid-page (the large-type caption check).</summary>
    internal Task ScrollToRecoveryAsync() => Scroller.ScrollToAsync(NewRecoveryButton, ScrollToPosition.Start, false);

    /// <summary>The end-to-end check photographs the Debug slots card.</summary>
    internal Task ScrollToSlotsAsync() => Scroller.ScrollToAsync(SlotsCard, ScrollToPosition.Start, false);

    /// <summary>The end-to-end check photographs the wipe confirm left open.</summary>
    internal Task ShowWipeConfirmForE2EAsync() => AppDialog.ConfirmAsync(this, AppStrings.Get("WipeTitle"), AppStrings.Get("WipeDesc"),
        AppStrings.Get("Delete"), danger: true);
#endif
}
