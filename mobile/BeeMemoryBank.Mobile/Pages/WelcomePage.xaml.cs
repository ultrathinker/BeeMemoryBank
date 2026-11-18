namespace BeeMemoryBank.Mobile.Pages;

/// <summary>
/// The mock-up's Welcome screen on a fresh install: the connect path only. Creating a brand-new vault
/// (with a recovery key) is not a phone capability today, so there is no create button — the setup
/// screen it opens joins an existing vault, or makes a standalone offline device when the address is
/// left empty (both are the existing flows).
/// </summary>
public partial class WelcomePage : ContentPage
{
    public WelcomePage()
    {
        InitializeComponent();
    }

    // busy-exempt: opens the connect screen; nothing is sent and nothing waits
    private async void OnConnectClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("//setup");
}
