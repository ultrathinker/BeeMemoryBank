using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
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

#if ANDROID
        // This is a personal vault — we never want Android's autofill framework to offer to "save"
        // any field. Disable autofill on every Entry.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("BmbNoAutofill", (handler, view) =>
        {
            var editText = handler.PlatformView;
            if (editText is null) return; // can be null during handler teardown
            editText.ImportantForAutofill = Android.Views.ImportantForAutofill.No;
            editText.SetAutofillHints((string[]?)null);

            // SecretEntry (per-article passphrase): mask the value but DON'T present it as a password
            // input type — otherwise the keyboard / password manager still offers to save & restore it
            // at the IME level, bypassing ImportantForAutofill. This is the native equivalent of the
            // web "-webkit-text-security" trick: plain text input type + a masking transformation.
            if (view is BeeMemoryBank.Mobile.Controls.SecretEntry)
            {
                editText.InputType = Android.Text.InputTypes.ClassText
                                   | Android.Text.InputTypes.TextFlagNoSuggestions;
                editText.TransformationMethod = Android.Text.Method.PasswordTransformationMethod.Instance;
            }
        });
#endif

        var dataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(dataDir, "beememorybank.db");
        var mediaDir = Path.Combine(dataDir, "media");
        Directory.CreateDirectory(mediaDir);

        var modelPath = Path.Combine(dataDir, "model.onnx");
        builder.Services.AddSingleton<IEmbeddingGenerator>(_ =>
        {
            if (!File.Exists(modelPath))
            {
                using var src = Task.Run(() => FileSystem.OpenAppPackageFileAsync("model.onnx")).GetAwaiter().GetResult();
                using var dst = File.Create(modelPath);
                src.CopyTo(dst);
            }
            return new OnnxEmbeddingGenerator(modelPath);
        });

        builder.Services
            .AddStorage(dbPath)
            .AddCore()
            .AddSync()
            .AddSingleton(new MediaStorageOptions(mediaDir))
            .AddSingleton<SyncNotificationService>()
            .AddSingleton<SyncStatusService>()
            .AddScoped<NodeSetupService>()
            .AddSingleton<IngestKeyEnroller>()
            .AddSingleton<PostUnlockRouter>()
            .AddSingleton<MobileUnlockHolder>()
            .AddLogging();

        // p5: friendly maintenance-mode messages for HTTP 503 from the BMB API. Only routes
        // through the named/DI HttpClient — raw `new HttpClient()` sites still see raw 503.
        builder.Services.AddTransient<Services.MaintenanceDetectingHandler>();
        builder.Services.AddHttpClient(string.Empty)
            .AddHttpMessageHandler<Services.MaintenanceDetectingHandler>();

        builder.Services.AddTransient<SnapshotJoinClient>(sp =>
            new SnapshotJoinClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                sp.GetRequiredService<DbConnectionFactory>(),
                dataDir,
                sp.GetRequiredService<ILogger<SnapshotJoinClient>>()));

#if ANDROID
        builder.Services.AddSingleton<IBiometricService, BiometricService>();
        // Hardware-backed ingest key + signer: lets background backup-sync authenticate while the
        // vault is locked / after reboot, without the master DEK. Overrides the default
        // SessionNodeAuthSigner registered in AddSync().
        builder.Services.AddSingleton<IIngestKeyStore, IngestKeyStore>();
        builder.Services.AddScoped<INodeAuthSigner, KeystoreNodeAuthSigner>();
#endif

        builder.Services.AddTransient<Pages.UnlockPage>();
        builder.Services.AddTransient<Pages.SetupPage>();
        builder.Services.AddTransient<Pages.InitialSyncPage>();
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
