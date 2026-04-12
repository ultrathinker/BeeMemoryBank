using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;

#if ANDROID
using Android.Content;
using Android.OS;
#endif

namespace BeeMemoryBank.Mobile.Pages;

public partial class SetupPage : ContentPage
{
    private readonly NodeSetupService _setupSvc;
    private readonly SessionService _session;

    public SetupPage(NodeSetupService setupSvc, SessionService session)
    {
        InitializeComponent();
        _setupSvc = setupSvc;
        _session = session;
    }

    private async void OnSetupClicked(object? sender, EventArgs e)
    {
        var name = NameEntry.Text?.Trim() ?? "";
        var serverUrl = ServerUrlEntry.Text?.Trim() ?? "";
        var password = PasswordEntry.Text ?? "";

        ErrorLabel.IsVisible = false;

        if (string.IsNullOrWhiteSpace(name)) { ShowError("Enter node name"); return; }
        if (password.Length < 6) { ShowError("Password must be at least 6 characters"); return; }

        SetupButton.IsEnabled = false;
        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;

        try
        {
            if (!string.IsNullOrWhiteSpace(serverUrl))
            {
                // Normalize URL: add scheme if not provided
                if (!serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    // Use http for local IPs, https for domain names
                    bool isLocal = serverUrl.StartsWith("192.168.", StringComparison.Ordinal)
                                || serverUrl.StartsWith("10.", StringComparison.Ordinal)
                                || serverUrl.StartsWith("172.", StringComparison.Ordinal)
                                || serverUrl.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
                    serverUrl = (isLocal ? "http://" : "https://") + serverUrl;
                }

                // JOIN: import DEK from existing server
                await _setupSvc.JoinAsync(name, serverUrl, password);
            }
            else
            {
                // INIT: new standalone node (offline)
                bool confirmed = await DisplayAlert(
                    "Standalone Node",
                    "No server URL provided. This node will NOT sync with any server. Articles will not be accessible from other devices. Continue?",
                    "Create Standalone", "Cancel");
                if (!confirmed) return;
                await _setupSvc.InitAsync(name, password);
            }

            await _session.UnlockAsync(password);
            RequestBatteryOptimizationException();
            await Permissions.RequestAsync<Permissions.PostNotifications>();
            await Shell.Current.GoToAsync("//status");
            App.StartSyncService();
        }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
        }
        finally
        {
            SetupButton.IsEnabled = true;
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }

#if ANDROID
    private void RequestBatteryOptimizationException()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return;
        var pm = (PowerManager?)Platform.CurrentActivity?.GetSystemService(Context.PowerService);
        if (pm == null) return;
        if (!pm.IsIgnoringBatteryOptimizations(Platform.CurrentActivity?.PackageName ?? ""))
        {
            var intent = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
            intent.SetData(Android.Net.Uri.Parse("package:" + Platform.CurrentActivity?.PackageName));
            Platform.CurrentActivity?.StartActivity(intent);
        }
    }
#else
    private void RequestBatteryOptimizationException() { }
#endif
}
