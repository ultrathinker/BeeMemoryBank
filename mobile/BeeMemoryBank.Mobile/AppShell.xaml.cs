using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
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

    protected override void OnNavigating(ShellNavigatingEventArgs args)
    {
        base.OnNavigating(args);

        // While initial sync is pending, the only allowed destinations are
        // the gate itself and the setup/unlock pages (cancel-and-wipe path).
        // Everything else is blocked — the user must wait for sync to finish.
        var target = args.Target?.Location?.OriginalString ?? "";
        if (target.StartsWith("//initialSync") || target.StartsWith("//setup") ||
            target.StartsWith("//unlock"))
            return;

        // Read the cached flag only — never touch SQLite on the UI thread.
        // The cache is seeded by PostUnlockRouter on every unlock path, and
        // cleared/updated by InitialSyncPage when sync completes. Before the
        // first unlock it's null, in which case we let navigation proceed
        // (the App.OnStart flow always routes to //unlock anyway).
        var router = IPlatformApplication.Current?.Services.GetService<PostUnlockRouter>();
        if (router?.CachedInitialSyncCompleted == false)
        {
            args.Cancel();
            Dispatcher.Dispatch(() => GoToAsync("//initialSync"));
        }
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
