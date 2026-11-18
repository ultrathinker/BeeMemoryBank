using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Media;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-307: the full Android app (<c>mobile/BeeMemoryBank.Mobile</c>) cannot be built or run by the test projects. What can be run IS run: the phone's
/// composition (<c>AddStorage().AddCore().AddSync()</c> and the pinned default client, as MauiProgram does it) is built here and its address change is
/// exercised end to end (BMB-307). What cannot - the Android services, the pages - is checked in its source: the pieces are shared libraries
/// that have their own tests (NoSyncServerTests, PeerAddressServiceTests, MetadataEnvelopeTests, PhoneVaultLifecycleTests), and these tests make sure the app
/// is wired to them.
/// </summary>
public class AndroidFullAppWiringTests
{
    private sealed class FakeEmbeddings : IEmbeddingGenerator
    {
        public int Dimension => 384;
        public string Version => "fake-v1";
        public float[] Generate(string text) => new float[Dimension];
    }

    private sealed class Peer(Guid id) : HttpMessageHandler
    {
        public bool Redirect { get; set; }
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (Redirect)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://login.local/portal?token=s3cret") } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    nodeId = id, displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]), protocolVersion = SyncProtocolVersion.Current,
                    capabilities = MetadataCapabilities.Advertised,
                }), Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>The phone's services as MauiProgram composes them (the platform pieces left out), with the network replaced by <paramref name="peer"/> UNDER the pin guard.</summary>
    private static ServiceProvider PhoneComposition(string dir, Peer peer)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEmbeddingGenerator, FakeEmbeddings>();
        services.AddStorage(Path.Combine(dir, "beememorybank.db")).AddCore().AddSync().AddSingleton(new MediaStorageOptions(Path.Combine(dir, "media")));
        services.AddHttpClient(string.Empty).UsePinnedSyncHandler();
        // The same chain a phone has - the pin guard that follows no redirect - over the fake network.
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b =>
            b.PrimaryHandler = new SpkiPinGuardHandler(b.Services.GetRequiredService<SpkiPinRegistry>(), peer)));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_phones_composition_is_the_shared_sync_stack_and_its_edit_url_runs_through_the_one_address_service()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-phone-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var peerId = Guid.NewGuid();
        var peer = new Peer(peerId);
        await using var provider = PhoneComposition(dir, peer);
        await provider.GetRequiredService<MigrationRunner>().RunMigrationsAsync();

        // What AddSync gives a phone: the protocol policy, the envelope opener of a full node, the shared client; and no loop of its own to tell (the foreground
        // service runs the sessions), so the address service has to work without one.
        Assert.IsType<SyncProtocolPolicy>(provider.GetRequiredService<ISyncProtocolPolicy>());
        Assert.IsType<BeeMemoryBank.Vault.Metadata.VaultEnvelopeOpener>(provider.GetRequiredService<IEnvelopeOpener>());
        Assert.Null(provider.GetService<IPeerStateResetter>());

        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.NotNull(sp.GetRequiredService<SyncClient>());
        await sp.GetRequiredService<InitializationService>().InitializeAsync("admin", "Phone", "pass");
        await provider.GetRequiredService<SessionService>().UnlockAsync("pass");
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = peerId, DisplayName = "Laptop", Ed25519PublicKey = new byte[32], ApiAddress = "http://old.local",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var service = sp.GetRequiredService<PeerAddressService>();

        // an address the policy refuses never reaches the network
        var refused = await service.ChangeAsync(peerId, "http://user:s3cret@peer.local", "phone", "phone");
        Assert.Equal(PeerAddressOutcome.Refused, refused.Outcome);
        Assert.DoesNotContain("s3cret", refused.Message);
        Assert.Equal(0, peer.Requests);

        // a redirect to a login page - through the phone's own pinned client - is "no sync server", so the page does not offer to save it
        peer.Redirect = true;
        var moved = await service.ChangeAsync(peerId, "http://peer.local", "phone", "phone");
        Assert.Equal(PeerAddressOutcome.NoSyncServer, moved.Outcome);
        Assert.DoesNotContain("s3cret", moved.Message);
        Assert.DoesNotContain("login.local", moved.Message);
        Assert.Equal("http://old.local", (await whitelist.GetByNodeIdAsync(peerId))!.ApiAddress);

        // the node that answers is saved: canonical, with the version the event minted
        peer.Redirect = false;
        var saved = await service.ChangeAsync(peerId, "Peer.Local", "phone", "phone");
        Assert.Equal(PeerAddressOutcome.Changed, saved.Outcome);
        var row = (await whitelist.GetByNodeIdAsync(peerId))!;
        Assert.Equal("https://peer.local", row.ApiAddress);
        Assert.True(row.LamportTs > 0);
        Assert.Single(await sp.GetRequiredService<IEventLogRepository>().GetRecentAsync(50, 0, EventTypes.WhitelistUpdate));

        // and the same address again changes nothing
        Assert.Equal(PeerAddressOutcome.Unchanged, (await service.ChangeAsync(peerId, "https://peer.local/", "phone", "phone")).Outcome);
        Assert.Single(await sp.GetRequiredService<IEventLogRepository>().GetRecentAsync(50, 0, EventTypes.WhitelistUpdate));
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) return dir.FullName;
        throw new InvalidOperationException("The repository root was not found.");
    }

    private static string App(params string[] path) =>
        File.ReadAllText(Path.Combine([Root, "mobile", "BeeMemoryBank.Mobile", .. path]));

    [Fact]
    public void The_app_composes_the_shared_sync_stack_and_keeps_no_client_of_its_own()
    {
        var program = App("MauiProgram.cs");

        // The composition above is what AddSync() gives; this makes sure MauiProgram is that composition (the protocol policy, the envelope opener, the
        // held-event drain, the key ring: the same code a desktop runs, and the app keeps no copy of any of it).
        Assert.Contains(".AddSync()", program);
        Assert.Contains("ISyncProtocolPolicy", program);   // the snapshot join clients are given the policy
        Assert.DoesNotContain("new SyncClient(", program);   // no private client that would declare something else
        Assert.DoesNotContain("api/sync/identity", program);
    }

    [Fact]
    public void The_background_loop_records_a_peers_failure_not_a_clean_cycle()
    {
        var service = App("Platforms", "Android", "SyncForegroundService.cs");
        var worker = App("Platforms", "Android", "SyncWorker.cs");

        // Problem carries what each peer said; Error alone is the cycle's own failure and stays null when only an address is wrong.
        Assert.Contains("r.Problem == null", service);
        Assert.DoesNotContain("r.Error == null", service);
        Assert.Contains("PeerSyncFailure.From(node, ex)", worker);
        Assert.DoesNotContain("err = ex.Message", worker);
    }

    /// <summary>
    /// the start-up task of the foreground sync service catches whatever it throws, logs it and clears IsRunning. Faulted and
    /// unobserved, the loop never started while IsRunning stayed true - and the WorkManager backstop skips its cycles while that flag is set, so
    /// background sync was silently dead until the next start command.
    /// </summary>
    [Fact]
    public void The_sync_service_start_up_task_catches_and_hands_over_to_the_backstop()
    {
        var service = App("Platforms", "Android", "SyncForegroundService.cs");
        var start = service.IndexOf("Task.Run(async () =>", StringComparison.Ordinal);
        Assert.True(start >= 0, "the start-up task is there");
        var task = service[start..service.IndexOf("// The look for the paired desktops", start, StringComparison.Ordinal)];
        var catchAll = task.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(catchAll >= 0, "the start-up task has a catch-all");
        Assert.Contains("IsRunning = false", task[catchAll..]);
        Assert.True(task.TrimEnd().EndsWith("});", StringComparison.Ordinal));
    }

    /// <summary>
    /// the page reads inside the unlocked shell catch what they throw and look again after their awaits whether the vault is
    /// still unlocked. A lock that lands mid-read (the idle timer, leaving the app, Lock now) closes an encrypted database under the read; an async
    /// void page event that rethrows it ends the app.
    /// </summary>
    [Fact]
    public void The_page_reads_inside_the_shell_survive_a_lock_that_lands_mid_read()
    {
        foreach (var (page, method) in GuardedPageReads)
        {
            var body = MethodBody(App("Pages", page), method);
            Assert.True(body.Contains("catch (Exception", StringComparison.Ordinal), $"{page} {method} catches: a lock mid-read must not end the app");
            Assert.True(body.Contains("IsUnlocked", StringComparison.Ordinal), $"{page} {method} looks again whether the vault is still unlocked");
        }
    }

    private static readonly (string Page, string Method)[] GuardedPageReads =
    [
        ("SecurityPage.xaml.cs", "OnAppearing"),
        ("TrashPage.xaml.cs", "OnAppearing"),
    ];

    /// <summary>The body of the async method <paramref name="method"/> (braces matched), for the source guards.</summary>
    private static string MethodBody(string source, string method)
    {
        var head = System.Text.RegularExpressions.Regex.Match(source, @"async (?:void|Task) " + method + @"\([^)]*\)\s*\{");
        Assert.True(head.Success, $"the async method {method} is there");
        var depth = 1;
        var i = head.Index + head.Length;
        for (; i < source.Length && depth > 0; i++) depth += source[i] == '{' ? 1 : source[i] == '}' ? -1 : 0;
        return source[(head.Index + head.Length)..i];
    }

    /// <summary>
    /// every wipe of the Android app - "delete all data" in the settings, "cancel and delete" of the first sync, the setup
    /// reset - deletes the whole sealed file set through the one shared helper (boot store, database, chat database, the hint mirror, the seal
    /// journal, with their sidecars), as the node's reset does. Deleting the database alone left the boot store behind, and the next start was a
    /// dead end: an encrypted phone was unusable until its app data was cleared in the system settings. None deletes the database by name itself.
    /// </summary>
    [Fact]
    public void Every_wipe_deletes_the_whole_sealed_file_set()
    {
        foreach (var page in new[] { "SecurityPage.xaml.cs", "InitialSyncPage.xaml.cs", "SetupPage.xaml.cs" })
        {
            var source = App("Pages", page);
            Assert.True(source.Contains("PhoneWipe.ReturnToPlainAsync(_services)", StringComparison.Ordinal), $"{page} wipes through PhoneWipe (PhoneVaultLifecycle.ReturnToPlainAsync)");
            Assert.False(source.Contains("\"beememorybank.db\"", StringComparison.Ordinal), $"{page} names no single vault file: the shared set is the whole of it");
        }
        var wipe = App("Services", "PhoneWipe.cs");
        Assert.Contains("lifecycle.ReturnToPlainAsync(", wipe);
        Assert.Contains("PhoneVaultLifecycle.DeleteVaultFiles(", wipe);
    }

    /// <summary>
    /// The Android side: on an encrypted phone the database is closed until the unlock page opens it, and what runs before that
    /// reads nothing from it. The boot-started sync service does not read the event log while the vault is closed (that read faulted its whole
    /// loop: no background sync even after the unlock); the Lamport clock is set by the open steps, as on the iPhone; a shared text waits for the
    /// unlock instead of opening the editor over the unlock page; and the cold start catches what it throws.
    /// </summary>
    [Fact]
    public void Nothing_before_the_unlock_reads_the_closed_database_and_the_start_catches()
    {
        var service = App("Platforms", "Android", "SyncForegroundService.cs");
        Assert.Contains("catch (VaultSealedException)", service);

        var openWork = App("Services", "AndroidOpenWork.cs");
        Assert.Contains("GetMaxLamportTimestampAsync", openWork);
        Assert.Contains("LamportClock>().Initialize(", openWork);

        var activity = App("Platforms", "Android", "MainActivity.cs");
        Assert.Contains("IsUnlocked", activity[activity.IndexOf("private void HandleShareIntent", StringComparison.Ordinal)..]);
        var router = App("Services", "PostUnlockRouter.cs");
        Assert.Contains("ShareIntentHandler.PendingText", router);

        var app = App("App.xaml.cs");
        var start = app[app.IndexOf("protected override async void OnStart()", StringComparison.Ordinal)..];
        start = start[..start.IndexOf("private async Task StartWithoutDatabaseAsync", StringComparison.Ordinal)];
        Assert.Contains("catch (Exception", start);
    }

    [Fact]
    public void Sync_now_and_the_address_check_use_the_shared_answers()
    {
        var status = App("Services", "SyncStatusService.cs");
        var page = App("Pages", "StatusPage.xaml.cs");

        Assert.Contains("Task<IReadOnlyList<PeerSyncFailure>> SyncNowAsync()", status);
        Assert.Contains("PeerSyncFailure.From(node, ex)", status);

        // Edit URL is the one address service (run end to end above); the page writes no row, no event and no dialog with an address of its own.
        Assert.Contains("PeerAddressService", page);
        Assert.Contains("saveWhenUnanswered", page);
        Assert.DoesNotContain("api/sync/identity", page);
        Assert.DoesNotContain("SyncAddress.CheckAsync(", page);
        Assert.DoesNotContain("LogWhitelistUpdateAsync", page);
        Assert.DoesNotContain("entry.ApiAddress =", page);
        Assert.DoesNotContain("{address}", page);
    }

    [Fact]
    public void The_phone_encrypts_itself_right_after_setup_with_no_button()
    {
        var securityXaml = App("Pages", "SecurityPage.xaml");
        var securityCode = App("Pages", "SecurityPage.xaml.cs");
        var setup = App("Pages", "SetupPage.xaml.cs");
        var unlock = App("Pages", "UnlockPage.xaml.cs");
        var program = App("MauiProgram.cs");
        var shell = App("AppShell.xaml.cs");
        var sealing = App("Services", "PhoneSealing.cs");

        // every database is encrypted with no button - right after a create and right
        // after a join, never at unlock. The encrypt button, its card and the page it opened are gone;
        // the composition still wires the phone vault lifecycle the sealing runs on.
        Assert.DoesNotContain("EncryptDatabaseButton", securityXaml);
        Assert.DoesNotContain("OnEncryptDatabaseClicked", securityCode);
        Assert.DoesNotContain("EncryptionCard", securityXaml);
        Assert.DoesNotContain("EncryptDatabasePage", program);
        Assert.DoesNotContain("encryptDatabase", shell);
        Assert.False(File.Exists(Path.Combine(Root, "mobile", "BeeMemoryBank.Mobile", "Pages", "EncryptDatabasePage.xaml.cs")),
            "the encrypt page is removed; the sealing runs by itself after setup");
        Assert.Contains("AddPhoneVault(dataDir, phoneStart, PhoneVaultLifecycle.PhoneEncryptionAvailable)", program);
        Assert.True(BeeMemoryBank.Vault.Sealed.PhoneVaultLifecycle.PhoneEncryptionAvailable, "the build offers the phone's own encryption");
        Assert.Contains("class PhoneSealing", sealing);
        Assert.Contains("Task SealAsync(bool startsNetwork", sealing);
        Assert.Contains("await EncryptIfNeededAsync()", setup);
        Assert.Contains("sealing.SealAsync(await StartsNetworkAsync())", setup);
        // An unlock never encrypts: a phone holding an old plain copy is set up again.
        Assert.DoesNotContain("PhoneSealing", unlock);
        Assert.DoesNotContain("SealAsync", unlock);
    }

    [Fact]
    public void The_android_app_scans_the_join_code_with_the_one_approved_package()
    {
        var project = App("BeeMemoryBank.Mobile.csproj");
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Packages.props"));

        // BMB-389: exactly one new package, the Google code scanner (Play services shows its own
        // screen, no CAMERA permission). It sits in the Android-only group, its version is pinned centrally on the
        // latest stable 116.x, and the empty-successor AndroidX overrides keep the dexer from duplicate classes.
        var androidGroup = project[project.IndexOf("<ItemGroup Condition=\"$(TargetFramework.Contains('-android'))\">", StringComparison.Ordinal)..];
        Assert.Contains("Xamarin.GooglePlayServices.Code.Scanner", androidGroup);
        Assert.Contains("<PackageReference Include=\"Xamarin.GooglePlayServices.Code.Scanner\" />", project);
        Assert.Contains("Xamarin.GooglePlayServices.Code.Scanner\" Version=\"116.", props);
        Assert.Contains("Xamarin.AndroidX.Fragment.Ktx\" VersionOverride=\"1.9.0\"", project);
        Assert.Contains("Xamarin.AndroidX.Tracing.Tracing.Ktx\" VersionOverride=\"2.0.0\"", project);
        // The scan button is on the join page, above the code field, and its words are the app's own (en + ru).
        var setup = App("Pages", "SetupPage.xaml");
        Assert.Contains("ScanQrButton", setup);
        Assert.Contains("JoinScanQr", setup);
        var resx = App("Resources", "Strings", "AppResources.resx");
        Assert.Contains("name=\"JoinScanQr\"", resx);
        Assert.Contains("name=\"QrScannerUnavailable\"", resx);
        // Closing the scanner without a code must not close the app: the Play services task is awaited through our own listeners,
        // never through AsAsync<Barcode>(), which crashed the app on a real phone when the scanner was closed (16.11).
        var scanner = App("Platforms", "Android", "AndroidQrScanner.cs");
        Assert.DoesNotContain("AsAsync<", scanner);
        Assert.Contains("AddOnCanceledListener", scanner);
    }

    [Fact]
    public void Unreachable_peers_stay_yellow_on_the_badge_and_the_status_page()
    {
        var status = App("Services", "SyncStatusService.cs");
        var badge = App("Controls", "SyncIndicatorView.xaml.cs");
        var page = App("Pages", "StatusPage.xaml.cs");
        var xaml = App("Pages", "StatusPage.xaml");

        // a computer that is simply not reachable is not an error - yellow, never red.
        // The same classification as the iPhone's FullSync.Classify, and the screens say it in their own words.
        Assert.Contains("public static string Classify(Exception ex, bool pinned)", status);
        Assert.Contains("ProblemUnreachable", status);
        Assert.Contains("LastUnreachable", badge);
        Assert.Contains("SyncBadgeUnreachable", badge);
        Assert.Contains("case \"unreachable\":", badge);
        Assert.Contains("UnreachableBanner", xaml);
        Assert.Contains("SyncText.Unreachable", page);
        Assert.Contains("SyncStateUnreachable", page);
    }

    /// <summary>
    /// 2.12: when a computer proves that an administrator disconnected this phone (ThisDeviceRevokedException, run end to end by DeviceRevokedRefusalTests and
    /// ThisDeviceRevokedTests), the status page says one calm sentence in both languages instead of a sync error, and "Sync now" does not turn it into a failure
    /// dialog; the background loop is the shared SyncScheduler, which asks such a peer again only every ClosedRetry.
    /// </summary>
    [Fact]
    public void A_phone_an_administrator_disconnected_says_one_calm_sentence()
    {
        var status = App("Services", "SyncStatusService.cs");
        var page = App("Pages", "StatusPage.xaml.cs");
        var xaml = App("Pages", "StatusPage.xaml");
        var text = App("Controls", "SyncText.cs");
        var en = App("Resources", "Strings", "AppResources.resx");
        var ru = App("Resources", "Strings", "AppResources.ru.resx");

        Assert.Contains("ThisDeviceRevokedException => ProblemDisconnected,", status);
        Assert.True(status.IndexOf("ThisDeviceRevokedException => ProblemDisconnected", StringComparison.Ordinal)
            < status.IndexOf("HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => ProblemRefused", StringComparison.Ordinal),
            "the proven revocation is classified before the generic refusal it also is");
        Assert.Contains("disconnected = true;", status);
        Assert.Contains("SyncStatusService.ProblemDisconnected => AppStrings.Get(\"SyncProblemDisconnected\")", text);
        Assert.Contains("DisconnectedBanner.IsVisible = disconnected;", page);
        Assert.Contains("var error = _statusSvc.LastProblemCode is not null && !disconnected;", page);
        Assert.Contains("x:Name=\"DisconnectedBanner\"", xaml);
        foreach (var key in new[] { "SyncProblemDisconnected", "SyncDisconnectedTitle", "SyncStateDisconnected", "SlotRecoveryOwnerFormat", "SlotRecoveryNoOwner" })
        {
            Assert.Contains($"name=\"{key}\"", en);
            Assert.Contains($"name=\"{key}\"", ru);
        }
        Assert.Contains(ThisDeviceRevokedException.Sentence, en);
    }

    /// <summary>
    /// 2.12: a recovery key is personal. The phone issues it for the person whose master password was just confirmed (PhoneOwner, run on a real vault by the
    /// iPhone's PhoneOwnerTests: the Android app links the same file), never with no owner; and no slot of a kind the vault refuses is asked for (the
    /// "dev" password was always refused; a recovery key goes only through its own issue).
    /// </summary>
    [Fact]
    public void A_recovery_key_is_issued_for_its_owner_and_no_refused_slot_kind_is_asked_for()
    {
        var page = App("Pages", "RecoveryKeyPage.xaml.cs");
        var security = App("Pages", "SecurityPage.xaml.cs");
        var project = App("BeeMemoryBank.Mobile.csproj");

        Assert.Contains(@"..\BeeMemoryBank.FullIos\Services\PhoneOwner.cs", project);
        Assert.Contains("_owner = await Task.Run(() => PhoneOwner.ForPasswordAsync(scope.ServiceProvider, password));", page);
        Assert.Contains("PhoneOwner.IssueRecoveryKeyAsync(sp.GetRequiredService<KeyManagementService>(), _owner, name)", page);
        foreach (var source in Directory.GetFiles(Path.Combine(Root, "mobile", "BeeMemoryBank.Mobile"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)).Select(File.ReadAllText))
        {
            Assert.DoesNotMatch(@"\bAddRecoveryKeyAsync\(\s*\)", source);
            Assert.DoesNotMatch(@"AddPasswordSlotAsync\(\s*""(dev|recovery)""", source);
        }
        Assert.Contains("AddPasswordSlotAsync(\"user\", password)", security);
        Assert.Contains("OwnerName = s.OwnerUserId is { } owner && names.TryGetValue(owner, out var name) ? name : null,", security);
    }

    /// <summary>
    /// the Debug slot card offers to delete a "user" slot only when it is the dev password's slot the card added itself
    /// (DevSlot, linked from the iPhone app and run in FullIos.Tests' DevSlotTests); a "user" slot no account points at may still be a person's door.
    /// </summary>
    [Fact]
    public void The_debug_slot_card_offers_no_user_slot_but_its_own_dev_password()
    {
        var security = App("Pages", "SecurityPage.xaml.cs");
        var project = App("BeeMemoryBank.Mobile.csproj");

        Assert.Contains(@"..\BeeMemoryBank.FullIos\Services\DevSlot.cs", project);
        Assert.Contains("CanDelete = DevSlot.CanDelete(s, loginSlots, devSlotId, slots.Count),", security);
        Assert.Contains("Preferences.Default.Set(DevSlot.PreferenceKey, added)", security);
        Assert.DoesNotContain("CanDelete = !loginSlots.Contains(s.SlotId)", security);
    }
}
