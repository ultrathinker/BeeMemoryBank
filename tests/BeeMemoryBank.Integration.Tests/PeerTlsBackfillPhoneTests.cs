using System.Buffers.Text;
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
using BeeMemoryBank.Sync.Channels;
using BeeMemoryBank.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-337 on the Android phone. The app cannot be run by the test projects, so what can be run IS run: the phone's composition (<c>AddStorage().AddCore().AddSync()</c>,
/// as MauiProgram composes it) is built here, and the entry point the phone's "Sync now", its WorkManager worker and its first download all call
/// (<see cref="PeerSessions.SyncWithPeerAsync"/>) is run against a hub that answers my-standing. What cannot be run - the pages and the Android services - is checked
/// in its source: that each of them reaches the shared sync client, which is where the key is taken.
/// </summary>
public class PeerTlsBackfillPhoneTests
{
    private static readonly string Pin = Base64Url.EncodeToString(Enumerable.Repeat((byte)5, 32).ToArray());

    private sealed class FakeEmbeddings : IEmbeddingGenerator
    {
        public int Dimension => 384;
        public string Version => "fake-v1";
        public float[] Generate(string text) => new float[Dimension];
    }

    /// <summary>The hub as a node answers it: identity, the challenge handshake, an empty log, and a my-standing that lists the blind node with its pin.</summary>
    private sealed class Hub(Guid id, byte[] key, Guid blind, bool standingCarriesTheKey = true) : HttpMessageHandler
    {
        public int StandingCalls;

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/sync/my-standing") Interlocked.Increment(ref StandingCalls);
            return Task.FromResult(path switch
            {
                "/api/sync/identity" => Json(new { nodeId = id, displayName = "Hub", ed25519PublicKeyB64 = Convert.ToBase64String(key), protocolVersion = SyncProtocolVersion.Current }),
                "/api/sync/challenge" => Json(new { challenge = Convert.ToBase64String(new byte[32]), serverNodeId = id }),
                "/api/sync/authenticate" => Json(new { token = "test-token" }),
                "/api/sync/events" when request.Method == HttpMethod.Get => Json(Array.Empty<object>()),
                "/api/sync/events" => Json(new { applied = 0, skipped = 0, dropped = 0 }),
                "/api/sync/report-position" => new HttpResponseMessage(HttpStatusCode.OK),
                "/api/sync/my-standing" => Json(new
                {
                    responder_node_id = id, protocol = 4, caller_is_superadmin = true, reseed_needed = false,
                    peers = new[]
                    {
                        standingCarriesTheKey
                            ? new { node_id = blind, display_name = "Blind", last_protocol_version = 4, created_at = DateTime.UtcNow, tls_trust = (string?)"pin", tls_spki = (string?)Pin }
                            : new { node_id = blind, display_name = "Blind", last_protocol_version = 4, created_at = DateTime.UtcNow, tls_trust = (string?)null, tls_spki = (string?)null },
                    },
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }
    }

    /// <summary>The phone's services as MauiProgram composes them (the platform pieces left out).</summary>
    private static ServiceProvider PhoneComposition(string dir)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEmbeddingGenerator, FakeEmbeddings>();
        services.AddStorage(Path.Combine(dir, "beememorybank.db")).AddCore().AddSync().AddSingleton(new MediaStorageOptions(Path.Combine(dir, "media")));
        services.AddHttpClient(string.Empty).UsePinnedSyncHandler();
        return services.BuildServiceProvider();
    }

    private static async Task<(ServiceProvider Provider, IServiceScope Scope)> PhoneAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-phone-backfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var provider = PhoneComposition(dir);
        await provider.GetRequiredService<MigrationRunner>().RunMigrationsAsync();
        var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "Phone", "pass");
        await provider.GetRequiredService<SessionService>().UnlockAsync("pass");
        return (provider, scope);
    }

    [Fact]
    public async Task APhoneThatJoinedBefore251_TakesTheBlindNodesKey_InTheSessionWithItsHub_AndHoldsTheNodeToItFromTheNextOne()
    {
        var hubId = Guid.NewGuid();
        var blindId = BlindNodeId.NewId();
        var hubKey = Enumerable.Repeat((byte)7, 32).ToArray();
        var (provider, scope) = await PhoneAsync();
        await using var _ = provider;
        using var __ = scope;
        var sp = scope.ServiceProvider;
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        // The rows as a phone that joined with 2.3.0 really has them: written by the statement of that join (the hub, an administrator, with its address; the blind node, with an
        // address and neither a mode nor a key), then the real migration 045 of the start of 2.8.0 over them (tests/shared/LegacyWhitelistRows.cs).
        var db = provider.GetRequiredService<DbConnectionFactory>();
        await LegacyWhitelistRows.InsertAsOf230JoinAsync(db, hubId, "Hub", hubKey, "https://hub.example", isSuperadmin: true);
        await LegacyWhitelistRows.InsertAsOf230JoinAsync(db, blindId, "Blind", new byte[32], "https://blind.example:5610", isSuperadmin: false);
        await LegacyWhitelistRows.RunMigration045Async(db);
        var hubRow = (await whitelist.GetByNodeIdAsync(hubId))!;
        var blindBefore = (await whitelist.GetByNodeIdAsync(blindId))!;
        (blindBefore.TlsTrust, blindBefore.TlsSpki).Should().Be(((string?)null, (string?)null));
        blindBefore.IsUndated(WhitelistConcerns.Tls).Should().BeTrue("migration 045 leaves the TLS trust of a row nobody versioned undated: the state the fill may write into");
        (await PeerSessions.PlanAsync(sp, blindBefore)).Anchor.Should().BeNull("with no key the blind node is checked through the public CAs: its certificate is refused");
        var hub = new Hub(hubId, hubKey, blindId);
        using var http = new HttpClient(hub);

        // What the phone's "Sync now", its worker and its first download call, for the hub.
        await PeerSessions.SyncWithPeerAsync(sp, http, hubRow, CancellationToken.None);

        hub.StandingCalls.Should().Be(1, "the standing is asked after the session: the shared client is composed with the reachability that asks it");
        var blindAfter = (await whitelist.GetByNodeIdAsync(blindId))!;
        (blindAfter.TlsTrust, blindAfter.TlsSpki).Should().Be(("pin", Pin));
        // The next session with the blind node reads the row again and is held to the key.
        var anchor = (await PeerSessions.PlanAsync(sp, blindAfter)).Anchor;
        anchor.Should().NotBeNull();
        anchor!.LeafPins.Should().Equal(Pin);
        anchor.AllowPublicCa.Should().BeFalse();
        // No event, no version: the fill is this phone's own bookkeeping.
        (await sp.GetRequiredService<IEventLogRepository>().GetRecentAsync(50, 0, EventTypes.WhitelistUpdate)).Should().BeEmpty();
        blindAfter.Version.Should().Be(blindBefore.Version);
    }

    [Fact]
    public async Task AHubOfAnOlderVersion_WhoseStandingCarriesNoKey_LeavesThePhoneAsItWas()
    {
        var hubId = Guid.NewGuid();
        var blindId = BlindNodeId.NewId();
        var hubKey = Enumerable.Repeat((byte)7, 32).ToArray();
        var (provider, scope) = await PhoneAsync();
        await using var _ = provider;
        using var __ = scope;
        var sp = scope.ServiceProvider;
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var db = provider.GetRequiredService<DbConnectionFactory>();
        await LegacyWhitelistRows.InsertAsOf230JoinAsync(db, hubId, "Hub", hubKey, "https://hub.example", isSuperadmin: true);
        await LegacyWhitelistRows.InsertAsOf230JoinAsync(db, blindId, "Blind", new byte[32], "https://blind.example:5610", isSuperadmin: false);
        await LegacyWhitelistRows.RunMigration045Async(db);
        using var http = new HttpClient(new Hub(hubId, hubKey, blindId, standingCarriesTheKey: false));

        await PeerSessions.SyncWithPeerAsync(sp, http, (await whitelist.GetByNodeIdAsync(hubId))!, CancellationToken.None);

        var blind = (await whitelist.GetByNodeIdAsync(blindId))!;
        (blind.TlsTrust, blind.TlsSpki).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public async Task ThePhonesComposition_RegistersTheFill_AndTheReachabilityThatAsksTheStanding()
    {
        var (provider, scope) = await PhoneAsync();
        await using var _ = provider;
        using var __ = scope;

        provider.GetService<PeerReachability>().Should().NotBeNull("SyncClient asks for the standing only when it has one");
        scope.ServiceProvider.GetService<PeerTlsBackfill>().Should().NotBeNull();
        scope.ServiceProvider.GetService<SyncClient>().Should().NotBeNull();
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
    public void EveryWayTheAndroidAppSyncsWithAPeer_ReachesTheSharedClient_WhichAsksTheStandingAndTakesTheKey()
    {
        // The status service ("Sync now" on the Status page, pull to refresh, the lists), the WorkManager worker and the first download all run the shared session.
        Assert.Contains("PeerSessions.SyncWithPeerAsync(sp, http, node, token)", App("Services", "SyncStatusService.cs"));
        Assert.Contains("PeerSessions.SyncWithPeerAsync(sp, http, node, CancellationToken.None)", App("Platforms", "Android", "SyncWorker.cs"));
        Assert.Contains("PeerSessions.SyncWithPeerAsync(sp, http, peer, token)", App("Pages", "InitialSyncPage.xaml.cs"));
        // The foreground service runs the shared scheduler, whose cycle takes the shared client from the container.
        Assert.Contains("new SyncScheduler(", App("Platforms", "Android", "SyncForegroundService.cs"));

        // Their callers are the Status page's buttons and the lists' pull to refresh, through the status service.
        Assert.Contains("_statusSvc.SyncNowAsync()", App("Pages", "StatusPage.xaml.cs"));

        // The app registers the shared stack and no sync client or reachability of its own (a private one would not ask the standing).
        var program = App("MauiProgram.cs");
        Assert.Contains(".AddSync()", program);
        Assert.DoesNotContain("PeerReachability", program);
        Assert.DoesNotContain("SyncClient", program);
    }

    [Fact]
    public void TheSharedClient_AsksTheStandingOnlyThroughItsReachability_AndTheFillIsWiredIntoThatAnswer()
    {
        var client = File.ReadAllText(Path.Combine(Root, "libs", "BeeMemoryBank.Sync", "SyncClient.cs"));

        // The one place the standing is asked, and what is done with the answer.
        Assert.Contains("reachability is not null && reachability.ShouldAskStanding(", client);
        Assert.Contains("await FillMissingTlsKeysAsync(remote.NodeId, standing, self);", client);   // the identity the session was verified against, never the answer's own
    }
}
