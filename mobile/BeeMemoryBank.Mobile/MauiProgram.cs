using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.Logging;
#if ANDROID
using BeeMemoryBank.Mobile.Platforms.Android;
#endif

namespace BeeMemoryBank.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var dataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(dataDir, "beememorybank.db");
        var mediaDir = Path.Combine(dataDir, "media");
        Directory.CreateDirectory(mediaDir);

        builder.Services
            .AddStorage(dbPath)
            .AddCore()
            .AddSync()
            .AddSingleton(new MediaStorageOptions(mediaDir))
            .AddSingleton<SyncNotificationService>()
            .AddSingleton<SyncStatusService>()
            .AddScoped<NodeSetupService>()
            .AddLogging()
            .AddHttpClient();

#if ANDROID
        builder.Services.AddSingleton<IBiometricService, BiometricService>();
#endif

        builder.Services.AddTransient<Pages.InitPage>();
        builder.Services.AddTransient<Pages.UnlockPage>();
        builder.Services.AddTransient<Pages.SetupPage>();
        builder.Services.AddTransient<Pages.StatusPage>();
        builder.Services.AddTransient<Pages.ArticlesPage>();
        builder.Services.AddTransient<Pages.TagsPage>();
        builder.Services.AddTransient<Pages.TagArticlesPage>();
        builder.Services.AddTransient<Pages.ArticleDetailPage>();
        builder.Services.AddTransient<Pages.ArticleEditPage>();
        builder.Services.AddTransient<Pages.TreePage>();
        builder.Services.AddTransient<Pages.TreeFolderPage>();
        builder.Services.AddTransient<Pages.FolderPickerPage>();
        builder.Services.AddTransient<Pages.SecurityPage>();

        return builder.Build();
    }
}
