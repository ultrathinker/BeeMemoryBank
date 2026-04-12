using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("articleDetail", typeof(Pages.ArticleDetailPage));
        Routing.RegisterRoute("articleEdit", typeof(Pages.ArticleEditPage));
        Routing.RegisterRoute("folderPicker", typeof(Pages.FolderPickerPage));
        Routing.RegisterRoute("tagArticles", typeof(Pages.TagArticlesPage));
        Routing.RegisterRoute("treeFolder", typeof(Pages.TreeFolderPage));

        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        UpdateOfflineBanner(Connectivity.Current.NetworkAccess);
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => UpdateOfflineBanner(e.NetworkAccess));
    }

    private void UpdateOfflineBanner(NetworkAccess access)
    {
        OfflineLabel.IsVisible = access != NetworkAccess.Internet && access != NetworkAccess.ConstrainedInternet;
    }

    private async void OnLockClicked(object? sender, EventArgs e)
    {
        FlyoutIsPresented = false;

        var session = IPlatformApplication.Current!.Services
            .GetRequiredService<SessionService>();
        session.Lock();

        App.StopSyncService();
        await GoToAsync("//unlock");
    }
}
