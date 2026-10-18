using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>A new memory bank on this phone: a name, the master password twice; then the recovery key, once.</summary>
public partial class CreateVaultPage : ContentPage
{
    private readonly FullVault _vault;
    private readonly AppFlow _flow;

    public CreateVaultPage(FullVault vault, AppFlow flow)
    {
        InitializeComponent();
        _vault = vault;
        _flow = flow;
        NameEntry.Text = FullVault.NodeName(null, DeviceInfo.Current.Name);
    }

    private async void OnCreate(object? sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;
        var problem = FullVault.CheckNewPassword(PasswordEntry.Text, RepeatEntry.Text);
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        var name = FullVault.NodeName(NameEntry.Text, DeviceInfo.Current.Name);
        var password = PasswordEntry.Text!;
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up, so Create cannot be tapped twice; the whole error
        // is written under the button when it fails.
        string? recoveryKey = null;
        var ran = await BusyAction.RunAsync(sender as Button, async () =>
        {
            // Off the main thread: the Argon2id derivations take seconds on a phone and the screen must keep moving.
            // Both fields are emptied whatever the creation ends in (SecretFields), not just when it works.
            recoveryKey = await SecretFields.RunAsync(
                () => Task.Run(() => _vault.CreateAsync(name, password)), () => PasswordEntry.Text = "", () => RepeatEntry.Text = "");
        }, new BusyOptions
        {
            Long = true,
            Title = "Creating your memory bank",
            Message = "Please wait. The key is derived from your password on purpose slowly; this takes a few seconds.",
            ResultLabel = ErrorLabel,
        });
        if (ran && recoveryKey is not null) _flow.ShowRecoveryKey(recoveryKey, firstTime: true);
    }

    private void ShowError(string text)
    {
        ErrorLabel.Text = text;
        ErrorLabel.IsVisible = true;
    }
}
