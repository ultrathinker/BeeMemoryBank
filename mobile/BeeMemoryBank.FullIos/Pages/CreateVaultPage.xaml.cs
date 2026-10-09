using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>A new memory bank on this phone: a name, the master password twice; then the recovery key, once.</summary>
public partial class CreateVaultPage : ContentPage
{
    private readonly FullVault _vault;
    private readonly AppFlow _flow;
    private bool _busy;

    public CreateVaultPage(FullVault vault, AppFlow flow)
    {
        InitializeComponent();
        _vault = vault;
        _flow = flow;
        NameEntry.Text = FullVault.NodeName(null, DeviceInfo.Current.Name);
    }

    private async void OnCreate(object? sender, EventArgs e)
    {
        if (_busy) return;
        ErrorLabel.IsVisible = false;
        var problem = FullVault.CheckNewPassword(PasswordEntry.Text, RepeatEntry.Text);
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        var name = FullVault.NodeName(NameEntry.Text, DeviceInfo.Current.Name);
        var password = PasswordEntry.Text!;
        SetBusy(true, "Creating your memory bank. The key is derived from your password on purpose slowly; this takes a few seconds.");
        try
        {
            // Off the main thread: the Argon2id derivations take seconds on a phone and the screen must keep moving.
            // Both fields are emptied whatever the creation ends in (SecretFields), not just when it works.
            var recoveryKey = await SecretFields.RunAsync(
                () => Task.Run(() => _vault.CreateAsync(name, password)), () => PasswordEntry.Text = "", () => RepeatEntry.Text = "");
            _flow.ShowRecoveryKey(recoveryKey, firstTime: true);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        NameEntry.IsEnabled = PasswordEntry.IsEnabled = RepeatEntry.IsEnabled = CreateButton.IsEnabled = !busy;
        Busy.IsVisible = Busy.IsRunning = busy;
        StatusLabel.Text = status;
        StatusLabel.IsVisible = status is not null;
    }

    private void ShowError(string text)
    {
        ErrorLabel.Text = text;
        ErrorLabel.IsVisible = true;
    }
}
