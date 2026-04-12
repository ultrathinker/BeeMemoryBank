using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;

#if ANDROID
using Android.Content;
using Android.OS;
#endif

namespace BeeMemoryBank.Mobile.Pages;

public partial class InitPage : ContentPage
{
    private readonly NodeSetupService _setupSvc;
    private readonly SessionService _session;

    public InitPage(NodeSetupService setupSvc, SessionService session)
    {
        InitializeComponent();
        _setupSvc = setupSvc;
        _session = session;
    }

    private async void OnInitClicked(object? sender, EventArgs e)
    {
        var name = NameEntry.Text?.Trim() ?? "";
        var password = PasswordEntry.Text ?? "";
        var confirm = ConfirmPasswordEntry.Text ?? "";

        ErrorLabel.IsVisible = false;

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Enter node name");
            return;
        }

        if (password.Length < 6)
        {
            ShowError("Password must be at least 6 characters");
            return;
        }

        if (password != confirm)
        {
            ShowError("Passwords do not match");
            return;
        }

        InitButton.IsEnabled = false;
        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;

        try
        {
            await _setupSvc.InitAsync(name, password);
            await _session.UnlockAsync(password);

            RequestBatteryOptimizationException();
            RequestNotificationsPermission();

            await Shell.Current.GoToAsync("//status");
            App.StartSyncService();
        }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
        }
        finally
        {
            InitButton.IsEnabled = true;
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    private async void OnJoinTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("//setup");
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

    private async void RequestNotificationsPermission()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
    }
#else
    private void RequestBatteryOptimizationException() { }
    private void RequestNotificationsPermission() { }
#endif
}
