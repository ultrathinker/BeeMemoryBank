using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;

namespace BeeMemoryBank.Mobile.Pages;

public partial class JoinPage : ContentPage
{
    private readonly NodeSetupService _setupSvc;
    private readonly SessionService _session;

    public JoinPage(NodeSetupService setupSvc, SessionService session)
    {
        InitializeComponent();
        _setupSvc = setupSvc;
        _session = session;
    }

    private async void OnJoinClicked(object? sender, EventArgs e)
    {
        var name = NameEntry.Text?.Trim() ?? "";
        var remoteUrl = UrlEntry.Text?.Trim() ?? "";
        var password = PasswordEntry.Text ?? "";

        ErrorLabel.IsVisible = false;

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Enter node name");
            return;
        }

        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            ShowError("Enter remote node URL");
            return;
        }

        if (password.Length < 6)
        {
            ShowError("Password must be at least 6 characters");
            return;
        }

        JoinButton.IsEnabled = false;
        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;

        try
        {
            await _setupSvc.JoinAsync(name, remoteUrl, password);
            await _session.UnlockAsync(password);

            await Shell.Current.GoToAsync("//status");
            App.StartSyncService();
        }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
        }
        finally
        {
            JoinButton.IsEnabled = true;
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }
}
