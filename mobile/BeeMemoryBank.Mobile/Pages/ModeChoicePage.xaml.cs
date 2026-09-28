using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Mobile.Services.Blind;

namespace BeeMemoryBank.Mobile.Pages;

/// <summary>First start: an ordinary device or a blind copy (plan section 10). Same APK, chosen once.</summary>
public partial class ModeChoicePage : ContentPage
{
    private readonly BlindPhonePairing _pairing;
    private bool _busy;

    public ModeChoicePage(BlindPhonePairing pairing)
    {
        InitializeComponent();
        _pairing = pairing;
        var deviceName = Microsoft.Maui.Devices.DeviceInfo.Current.Name;
        BlindNameEntry.Text = string.IsNullOrWhiteSpace(deviceName) ? "Blind copy" : deviceName;
    }

    private async void OnFullClicked(object? sender, EventArgs e)
    {
        DeviceModeStore.Set(DeviceMode.Full);
        await Shell.Current.GoToAsync("//setup");
    }

    private async void OnBlindClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        // Nothing navigates here while the choice is hidden (DeviceModeStore.BlindModeOffered), and
        // the page must not be a way in anyway: a deep link, or a future edit that routes back to
        // //mode early, would otherwise create a blind copy this build cannot finish setting up.
        if (!DeviceModeStore.BlindModeOffered)
        {
            ErrorLabel.Text = "This build does not offer the blind copy. Update the app to use it.";
            ErrorLabel.IsVisible = true;
            return;
        }
        _busy = true;
        ErrorLabel.IsVisible = false;
        try
        {
            await _pairing.CreateIdentityAsync(BlindNameEntry.Text ?? "");
            DeviceModeStore.Set(DeviceMode.Blind);
            App.StartBlindWork();
            await Shell.Current.GoToAsync("//blind");
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.Message;
            ErrorLabel.IsVisible = true;
        }
        finally
        {
            _busy = false;
        }
    }
}
