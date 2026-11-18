using BeeMemoryBank.Core;
using BeeMemoryBank.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Media;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Channels;
using BeeMemoryBank.Vault.Sealed;
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

        // The mock-up's type faces (Resources/Fonts, SIL OFL). One alias per weight, the way MAUI
        // picks a face: the styles name the alias, not the file.
        builder.ConfigureFonts(fonts =>
        {
            fonts.AddFont("NotoSans-Regular.ttf", "NotoSans");
            fonts.AddFont("NotoSans-Medium.ttf", "NotoSansMedium");
            fonts.AddFont("NotoSans-SemiBold.ttf", "NotoSansSemibold");
            fonts.AddFont("NotoSans-Bold.ttf", "NotoSansBold");
            fonts.AddFont("PlusJakartaSans-SemiBold.ttf", "JakartaSemibold");
            fonts.AddFont("PlusJakartaSans-Bold.ttf", "JakartaBold");
        });

#if ANDROID
        // This is a personal vault — we never want Android's autofill framework to offer to "save"
        // any field. Disable autofill on every Entry.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("BmbNoAutofill", (handler, view) =>
        {
            var editText = handler.PlatformView;
            if (editText is null) return; // can be null during handler teardown
            editText.ImportantForAutofill = Android.Views.ImportantForAutofill.No;
            editText.SetAutofillHints((string[]?)null);

            // SecretEntry (per-article passphrase) AND the master-password fields (PasswordBox, which
            // sets IsPassword): mask the value but DON'T present it as a password input type — otherwise
            // the keyboard / password manager still offers to save & restore it at the IME level,
            // bypassing ImportantForAutofill. This is the native equivalent of the web
            // "-webkit-text-security" trick: plain text input type + a masking transformation.
            // The PasswordBox show/hide eye keeps working: it toggles IsPassword, and this branch only
            // re-applies the mask while IsPassword is on; when revealed, the default mapping clears the
            // transformation. The field stays masked, keeps its eye, and types normally.
            if (view is BeeMemoryBank.Mobile.Controls.SecretEntry || view.IsPassword)
            {
                editText.InputType = Android.Text.InputTypes.ClassText
                                   | Android.Text.InputTypes.TextFlagNoSuggestions;
                editText.TransformationMethod = Android.Text.Method.PasswordTransformationMethod.Instance;
            }
            else if (editText.TransformationMethod is Android.Text.Method.PasswordTransformationMethod)
            {
                // A master-password field the eye just revealed (IsPassword off): keep the plain input
                // type and drop the mask, so the show/hide eye keeps working.
                editText.InputType = Android.Text.InputTypes.ClassText
                                   | Android.Text.InputTypes.TextFlagNoSuggestions;
                editText.TransformationMethod = null;
            }
        });

        // The show/hide eye toggles IsPassword, and MAUI's own IsPassword mapping re-sets the password /
        // visible input type on EVERY toggle. The property-less BmbNoAutofill above only runs when the
        // handler connects (full UpdateProperties), not on a single property change, so it cannot follow
        // the eye. Appending under the real IsPassword key runs AFTER MAUI's mapping on each change:
        // keep a plain input type (never a password type, so the keyboard / password manager never offers
        // to save) and set the mask to match — hidden = masked, shown = plain. This holds for every
        // master-password field (all are the inner Entry of a PasswordBox): connect, unlock, change
        // master password, recovery-key confirmation, database encryption. SecretEntry and ordinary
        // entries are left to their own mappings. The eye's icon and accessible name already follow
        // IsPassword (PasswordBox.OnToggleClicked), so they stay in step with what is shown.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("IsPassword", (handler, view) =>
        {
            var editText = handler.PlatformView;
            if (editText is null) return; // can be null during handler teardown
            if (view is BeeMemoryBank.Mobile.Controls.SecretEntry) return;
            // A revealed master-password field has IsPassword off, so recognise it by its PasswordBox
            // ancestor; IsPassword on identifies it too (the connect-time case).
            if (!view.IsPassword && !IsInPasswordBox(view)) return;
            editText.InputType = Android.Text.InputTypes.ClassText
                               | Android.Text.InputTypes.TextFlagNoSuggestions;
            editText.TransformationMethod = view.IsPassword
                ? Android.Text.Method.PasswordTransformationMethod.Instance
                : null;
            // Setting the input type puts the cursor at the start; put it back at the end, so typing
            // after a tap on the eye continues the password instead of prefixing it (test phone 14.11).
            editText.SetSelection(editText.Text?.Length ?? 0);
        });

        // The mock-up's .input-shell / .textarea-input: a field is a rounded 16dp surface with the
        // strong border, not the platform underline. The colour comes from the theme-resolved
        // BackgroundColor of the style, so a theme change repaints every field (the mapper runs
        // again on every property change). A transparent field is drawn by the shell around it
        // (Controls/PasswordBox) and keeps no background of its own.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("BmbInputShell", (handler, view) =>
            Platforms.Android.PlatformFieldBackground.Apply(handler.PlatformView, (view as View)?.BackgroundColor, 14, 4, 6, 4));
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("BmbInputShell", (handler, view) =>
            Platforms.Android.PlatformFieldBackground.Apply(handler.PlatformView, (view as View)?.BackgroundColor, 14, 12, 14, 12));

        // The mock-up's switch: dark thumb on the yellow track when on, secondary thumb on the
        // surface-high track when off — a tint per state, in the colours of the current theme.
        Microsoft.Maui.Handlers.SwitchHandler.Mapper.AppendToMapping("BmbSwitchColors", (handler, view) =>
            Platforms.Android.PlatformSwitchColors.Apply(handler.PlatformView as AndroidX.AppCompat.Widget.SwitchCompat));

        // A button shows its WHOLE text at any system font. MAUI's Android ButtonHandler has no
        // LineBreakMode mapping (verified in the assembly), so a MaterialButton stays single-line and
        // cuts a long caption (the Russian "delete data from this..." button). Force every button to wrap — several lines,
        // no ellipsis, centred — and the button styles use MinimumHeightRequest so it grows in height
        // instead of clipping the second line. Appended under "Text" so it re-applies after MAUI sets
        // the text, on connect and on every text change.
        Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("Text", (handler, view) =>
        {
            if (handler.PlatformView is not Android.Widget.TextView button) return;
            // Chips and the segmented control's segments stay on ONE line (they are sized to their
            // text, not wrapped). The marker is an attached property, independent of the caption.
            if (view is BindableObject bo && Controls.BmbButton.GetSingleLine(bo))
            {
                button.SetSingleLine(true);
                button.Ellipsize = null;
                button.Gravity = Android.Views.GravityFlags.Center;
                return;
            }
            button.SetSingleLine(false);
            button.SetHorizontallyScrolling(false);
            button.SetMaxLines(int.MaxValue);
            button.Ellipsize = null;
            button.Gravity = Android.Views.GravityFlags.Center;
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
            .AddImageTranscoder()
            .AddSingleton(new MediaStorageOptions(mediaDir))
            .AddSingleton<SyncNotificationService>()
            .AddSingleton<SyncStatusService>()
            .AddSingleton<PhoneSealing>()
            .AddScoped<NodeSetupService>()
            .AddScoped<PeerChannelService>()
            .AddSingleton<IngestKeyEnroller>()
            .AddSingleton<PostUnlockRouter>()
            .AddSingleton<MobileUnlockHolder>()
            .AddSingleton<AutoLockService>()
            .AddLogging();

        // Step B (docs/design/step-b-spec.md 5.8, 12.1): a phone that may be encrypted, is encrypted, or holds an interrupted encryption starts through the
        // vault lifecycle - the interrupted swap is finished or undone, the folder diagnosed, the lease held. While encryption is hidden and the folder is
        // plain this is skipped and the app starts exactly as it did before.
        if (PhoneVaultLifecycle.NeedsGate(dataDir))
        {
            var phoneStart = PhoneVaultLifecycle.Begin(dataDir);
            builder.Services.AddSingleton<IPhoneEnvironment, MauiPhoneEnvironment>();
            builder.Services.AddPhoneVault(dataDir, phoneStart, PhoneVaultLifecycle.PhoneEncryptionAvailable);
            builder.Services.AddAndroidOpenWork();
        }

        // p5: friendly maintenance-mode messages for HTTP 503 from the BMB API. Only routes
        // through the named/DI HttpClient — raw `new HttpClient()` sites still see raw 503.
        builder.Services.AddTransient<Services.MaintenanceDetectingHandler>();
        // Pinned TLS keys (plan 4.4): every sync caller on the phone takes this default client, so it
        // carries the pinned handler — blind peers on their key only, HTTPS only, no redirects.
        builder.Services.AddHttpClient(string.Empty)
            .UsePinnedSyncHandler()
            .AddHttpMessageHandler<Services.MaintenanceDetectingHandler>();
        // A session over a peer's channels (PeerSessions.SyncWithPeerAsync) takes its client from the named sync client, not from the default one: it
        // carries the same friendly 503 text, so the Status page, the worker and the first download say the same as before.
        builder.Services.AddHttpClient(SyncScheduler.HttpClientName)
            .AddHttpMessageHandler<Services.MaintenanceDetectingHandler>();

        builder.Services.AddTransient<SnapshotJoinClient>(sp =>
            new SnapshotJoinClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                sp.GetRequiredService<DbConnectionFactory>(),
                dataDir,
                sp.GetRequiredService<ILogger<SnapshotJoinClient>>(),
                protocolPolicy: sp.GetRequiredService<BeeMemoryBank.Sync.Metadata.ISyncProtocolPolicy>()));
        // A join by code downloads its snapshot over the join's key-pinned connection (NodeSetupService).
        builder.Services.AddTransient<Func<HttpClient, SnapshotJoinClient>>(sp => http =>
            new SnapshotJoinClient(
                http,
                sp.GetRequiredService<DbConnectionFactory>(),
                dataDir,
                sp.GetRequiredService<ILogger<SnapshotJoinClient>>(),
                protocolPolicy: sp.GetRequiredService<BeeMemoryBank.Sync.Metadata.ISyncProtocolPolicy>()));

#if ANDROID
        builder.Services.AddSingleton<IBiometricService, BiometricService>();
        // The join code QR scanner: Google Play services' own screen, no CAMERA permission (the one new package).
        builder.Services.AddSingleton<IQrScanner, Platforms.Android.AndroidQrScanner>();
        // Hardware-backed ingest key + signer: lets background backup-sync authenticate while the
        // vault is locked / after reboot, without the master DEK. Overrides the default
        // SessionNodeAuthSigner registered in AddSync().
        builder.Services.AddSingleton<IIngestKeyStore, IngestKeyStore>();
        builder.Services.AddScoped<INodeAuthSigner, KeystoreNodeAuthSigner>();
        // Direct sync on the LAN (docs/design/lan-peer-sync.md 16): what NSD sees of the paired desktops lands in the cache the channel planner reads
        // (replacing the empty default AddSync leaves), and the discovery is started and stopped by the sync paths, never left running.
        builder.Services.AddSingleton<PeerDiscoveryCache>();
        builder.Services.AddSingleton<IPeerDiscovery>(sp => sp.GetRequiredService<PeerDiscoveryCache>());
        builder.Services.AddSingleton<NsdPeerDiscovery>();
        builder.Services.AddSingleton<ILanPeerScanner>(sp => sp.GetRequiredService<NsdPeerDiscovery>());
#endif

        builder.Services.AddTransient<Pages.WelcomePage>();
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
        builder.Services.AddTransient<Pages.RecoveryKeyPage>();
        builder.Services.AddTransient<Pages.TrashPage>();

        return builder.Build();
    }

#if ANDROID
    // The master-password fields are exactly the inner Entry of a PasswordBox. Walk the logical tree to
    // recognise one even when the eye has revealed it (IsPassword off), so the IsPassword mapping above
    // never touches an ordinary entry's keyboard / input type.
    private static bool IsInPasswordBox(Microsoft.Maui.IElement view)
    {
        for (var e = view as Microsoft.Maui.Controls.Element; e is not null; e = e.Parent)
            if (e is BeeMemoryBank.Mobile.Controls.PasswordBox) return true;
        return false;
    }
#endif
}
