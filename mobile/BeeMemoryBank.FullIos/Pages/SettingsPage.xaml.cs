using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>Face ID / Touch ID, when the vault locks by itself, a new recovery key, Lock now (at the bottom).</summary>
public partial class SettingsPage : ContentPage
{
    private readonly AppFlow _flow;
    private readonly QuickUnlock _quick;
    private readonly FullVault _vault;
    private readonly IServiceProvider _services;
    private bool _loading;

    public SettingsPage(AppFlow flow, QuickUnlock quick, FullVault vault, IServiceProvider services)
    {
        InitializeComponent();
        _flow = flow;
        _quick = quick;
        _vault = vault;
        _services = services;
        GracePicker.ItemsSource = AutoLockPolicy.GraceChoices.Select(AutoLockPolicy.Describe).ToList();
        IdlePicker.ItemsSource = AutoLockPolicy.IdleChoices.Select(AutoLockPolicy.Describe).ToList();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _loading = true;
        var name = _quick.BiometryName;
        QuickLabel.Text = "Unlock with " + name;
        QuickSwitch.IsEnabled = _quick.IsAvailable;
        QuickSwitch.IsToggled = _quick.IsEnabled;
        QuickDetail.Text = _quick.IsAvailable
            ? $"A key that only {name} can release opens the vault; the master password is not stored. Adding a face or finger to this iPhone turns it off."
            : $"{name} is not set up on this iPhone.";
        // Hidden until the native release gate of docs/design/step-b-spec.md 12.4 is green.
        var encryptionOffered = PhoneVaultLifecycle.PhoneEncryptionAvailable && _services.GetService<PhoneVaultLifecycle>() is not null;
        EncryptionSection.IsVisible = encryptionOffered;
        EncryptionButton.IsVisible = encryptionOffered;
        GracePicker.SelectedIndex = Math.Max(0, Array.IndexOf(AutoLockPolicy.GraceChoices, _flow.Grace));
        IdlePicker.SelectedIndex = Math.Max(0, Array.IndexOf(AutoLockPolicy.IdleChoices, _flow.Idle));
        _loading = false;

        var identity = await _vault.IdentityAsync();
        AboutLabel.Text = $"Bee Memory Bank {AppInfo.Current.VersionString} for iPhone, sync protocol {SyncProtocolVersion.Current}.\n" +
                          (identity is null ? "" : $"This node: {identity.DisplayName}, {identity.NodeId}.");
    }

    private async void OnQuickToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (e.Value) _quick.Enable();
            else _quick.Disable();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(_quick.BiometryName, ex.Message, "OK");
            _loading = true;
            QuickSwitch.IsToggled = _quick.IsEnabled;
            _loading = false;
        }
    }

    private void OnGraceChanged(object? sender, EventArgs e)
    {
        if (!_loading && GracePicker.SelectedIndex >= 0) _flow.Grace = AutoLockPolicy.GraceChoices[GracePicker.SelectedIndex];
    }

    private void OnIdleChanged(object? sender, EventArgs e)
    {
        if (!_loading && IdlePicker.SelectedIndex >= 0) _flow.Idle = AutoLockPolicy.IdleChoices[IdlePicker.SelectedIndex];
    }

    private async void OnNewRecoveryKey(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("New recovery key", "Make a new recovery key? It is shown once.", "Make one", "Cancel")) return;
        try
        {
            var key = await Task.Run(_vault.NewRecoveryKeyAsync);
            _flow.ShowRecoveryKey(key, firstTime: false);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("New recovery key", ex.Message, "OK");
        }
    }

    private async void OnEncryption(object? sender, EventArgs e) =>
        await Navigation.PushAsync(_services.GetRequiredService<EncryptDatabasePage>());

    private void OnLock(object? sender, EventArgs e) => _flow.LockNow();
}
