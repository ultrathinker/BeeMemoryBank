namespace BeeMemoryBank.FullIos.Pages;

/// <summary>A phone without a vault: join the owner's existing memory bank, or make a new one here.</summary>
public partial class WelcomePage : ContentPage
{
    private readonly IServiceProvider _services;

    public WelcomePage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    // busy-exempt: opens the join page, nothing is sent and nothing waits
    private async void OnJoin(object? sender, EventArgs e) => await Navigation.PushAsync(_services.GetRequiredService<JoinPage>());

    // busy-exempt: opens the create page, nothing is sent and nothing waits
    private async void OnCreate(object? sender, EventArgs e) => await Navigation.PushAsync(_services.GetRequiredService<CreateVaultPage>());
}
