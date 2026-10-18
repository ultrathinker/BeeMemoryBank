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
/// exercised end to end (BMB-307 fix round, F11). What cannot - the Android services, the pages - is checked in its source: the pieces are shared libraries
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
    public void The_security_page_offers_encrypt_this_phones_database()
    {
        var xaml = App("Pages", "SecurityPage.xaml");
        var code = App("Pages", "SecurityPage.xaml.cs");
        var program = App("MauiProgram.cs");

        Assert.Contains("Encrypt this phone's database", xaml);
        Assert.Contains("Clicked=\"OnEncryptDatabaseClicked\"", xaml);
        Assert.Contains("PhoneVaultLifecycle.PhoneEncryptionAvailable && gate is not null", code);
        Assert.Contains("AddPhoneVault(dataDir, phoneStart, PhoneVaultLifecycle.PhoneEncryptionAvailable)", program);
        Assert.True(BeeMemoryBank.Vault.Sealed.PhoneVaultLifecycle.PhoneEncryptionAvailable, "the build offers the phone's own encryption");
    }
}
