using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The invitation client and the full node's invitations, together, over real TLS: two hubs (two real full nodes on Kestrel, each its own mesh, on a certificate a test CA vouches
/// for) and ONE device - a real <see cref="BlindAccountHost"/> in this process - with an account from each. The owner's side is the hub's own admin API (issue, confirm with the master
/// password, refuse, revoke); the device's side is the account host's own flow (preview, add, poll, first load, sync, remove).
/// </summary>
public sealed class BlindInviteE2EWorld : IAsyncLifetime
{
    public const string Password = "blindInviteE2EPassword1";

    public TestPki Pki { get; } = new();
    public TlsTrustAnchors Anchors { get; private set; } = null!;
    internal BlindHubHost One { get; private set; } = null!;
    internal BlindHubHost Two { get; private set; } = null!;
    public OffsetTimeProvider ClockOne { get; } = new();
    public OffsetTimeProvider ClockTwo { get; } = new();
    internal HubOwner OwnerOne { get; private set; } = null!;
    internal HubOwner OwnerTwo { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var leaf = Pki.IssueServer("localhost");
        Anchors = new TlsTrustAnchors(Pki.Anchors);
        One = await StartAsync(leaf, "Alice's hub", ClockOne);
        Two = await StartAsync(leaf, "Bob's hub", ClockTwo);
        OwnerOne = new HubOwner(One, ClockOne);
        OwnerTwo = new HubOwner(Two, ClockTwo);
    }

    private async Task<BlindHubHost> StartAsync(System.Security.Cryptography.X509Certificates.X509Certificate2 leaf, string name, OffsetTimeProvider clock)
    {
        var hub = await BlindHubHost.StartAsync(leaf, name, Password, services =>
        {
            services.AddSingleton(Anchors);
            services.AddSingleton(new HubTrustProbe(Anchors, HubProbeAddressGuardTests.AnyAddress));
            services.AddSingleton<TimeProvider>(clock);
            // Each hub is the only computer of its own network, with no full peer to ask whether it is a superadmin (and no metadata activation to prove it by): it speaks for itself,
            // so that the baseline a copy installs lists it as the superadmin that authored the events it then pulls (its own confirmations among them).
            services.AddScoped<IReplicaProducerAuthority>(_ => new HubSpeaksForItself());
        });
        hub.Admin.DefaultRequestHeaders.Add("X-User-Id", "owner");
        return hub;
    }

    public async Task DisposeAsync()
    {
        await One.DisposeAsync();
        await Two.DisposeAsync();
        Pki.Dispose();
    }

    /// <summary>
    /// The hub answers at most 30 sync challenges a minute to one address (a real copy makes one round in 15 minutes); a test that makes a first load and several rounds in a few seconds
    /// would find the next test's budget spent, so every test starts with a full one. The limiter keeps its window in a private field; it is replaced by an empty one.
    /// </summary>
    private static void ResetChallengeBudget(BlindHubHost hub)
    {
        var limiter = hub.Services.GetRequiredService<SyncEndpoints.SyncChallengeRateLimiter>();
        var field = typeof(SyncEndpoints.SyncChallengeRateLimiter).GetField("_limiter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(limiter, new BeeMemoryBank.Hosting.AspNetCore.SlidingWindowRateLimiter(30, TimeSpan.FromMinutes(1)));
    }

    /// <summary>Every test starts from: the real clocks, full budgets, unlocked hubs.</summary>
    public async Task ResetAsync()
    {
        ClockOne.Offset = TimeSpan.Zero;
        ClockTwo.Offset = TimeSpan.Zero;
        foreach (var owner in new[] { OwnerOne, OwnerTwo })
        {
            owner.Hub.Services.GetRequiredService<BlindInviteRateLimits>().ResetAll();
            ResetChallengeBudget(owner.Hub);
            await owner.UnlockAsync();
        }
    }
}

internal sealed class HubSpeaksForItself : IReplicaProducerAuthority
{
    public Task<bool> IsSuperadminAsync(CancellationToken ct) => Task.FromResult(true);
}

[CollectionDefinition(Name)]
public sealed class BlindInviteE2ECollection : ICollectionFixture<BlindInviteE2EWorld>
{
    public const string Name = "Blind invitations end to end";
}

/// <summary>What the owner of a hub does, through the hub's own admin API.</summary>
internal sealed class HubOwner(BlindHubHost hub, OffsetTimeProvider clock)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public BlindHubHost Hub => hub;
    public OffsetTimeProvider Clock => clock;

    public async Task<(string InviteId, string Text)> IssueAsync(string sourceName, int minutes = 30)
    {
        var response = await hub.Admin.PostAsJsonAsync("/api/blind-nodes/invites", new { label = sourceName + "'s friend", sourceName, lifetimeMinutes = minutes, address = hub.Origin, trust = "auto" });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
        return (body.GetProperty("inviteId").GetString()!, body.GetProperty("text").GetString()!);
    }

    public async Task ConfirmAsync(string inviteId)
    {
        var response = await hub.Admin.PostAsJsonAsync($"/api/blind-nodes/invites/{inviteId}/confirm", new { password = BlindInviteE2EWorld.Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    public async Task RefuseAsync(string inviteId) =>
        (await hub.Admin.PostAsync($"/api/blind-nodes/invites/{inviteId}/refuse", null)).EnsureSuccessStatusCode();

    /// <summary>The owner's page: the rows of the invitations (never the secret).</summary>
    public async Task<List<JsonElement>> ListAsync() =>
        (await hub.Admin.GetFromJsonAsync<JsonElement>("/api/blind-nodes/invites/", Web)).EnumerateArray().Select(e => e.Clone()).ToList();

    public async Task<JsonElement> RowAsync(string inviteId) => (await ListAsync()).Single(r => r.GetProperty("inviteId").GetString() == inviteId);

    public async Task RevokeAsync(Guid node) =>
        (await hub.Admin.DeleteAsync($"/api/whitelist/{node}")).EnsureSuccessStatusCode();

    public async Task LockAsync() => (await hub.Admin.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

    public async Task UnlockAsync()
    {
        if (!hub.Services.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>().IsUnlocked)
            (await hub.Admin.PostAsJsonAsync("/api/session/unlock", new { password = BlindInviteE2EWorld.Password })).EnsureSuccessStatusCode();
    }

    /// <summary>A folder written on the hub: a note that must reach the accounts of THIS hub and no other. Returns its path.</summary>
    public async Task<string> WriteNoteAsync(string prefix)
    {
        var path = "/" + prefix + Guid.NewGuid().ToString("N")[..8];
        (await hub.Admin.PostAsJsonAsync("/api/folders", new { path })).EnsureSuccessStatusCode();
        return path;
    }

    public async Task<long> AuditCountAsync(string action, string entityId)
    {
        using var conn = hub.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = @action AND entity_id = @entityId", new { action, entityId });
    }

    public async Task<WhitelistEntry?> WhitelistRowAsync(Guid node)
    {
        using var scope = hub.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(node);
    }
}

/// <summary>A device: the app's root folder, its stores, and an account host over them whose HTTP pipeline trusts the test CA through the same handler the app builds.</summary>
internal sealed class DeviceRig : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb-invite-e2e", Guid.NewGuid().ToString("N"));
    public InMemoryBlindSecretStoreFactory Secrets { get; } = new();
    public InMemoryBlindStateStoreFactory States { get; } = new();
    public BlindAccountHost Host { get; }

    public DeviceRig(TlsTrustAnchors anchors, TimeProvider? time = null, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null, Func<HttpMessageHandler>? bottom = null)
    {
        Directory.CreateDirectory(Root);
        Host = new BlindAccountHost(new BlindAccountHostOptions
        {
            RootDirectory = Root,
            LegacyDatabasePath = Path.Combine(Root, "beememorybank.db"),
            Secrets = Secrets,
            States = States,
            Time = time ?? TimeProvider.System,
            DisplayNameFactory = () => "E2E phone",
            RootLockWait = TimeSpan.FromMilliseconds(200),
            PrimaryHandlerFactory = _ => () =>
            {
                HttpMessageHandler handler = bottom?.Invoke() ?? BlindHttpHandler.CreatePublicCaHandler(null, anchors);
                return wrap?.Invoke(handler) ?? handler;
            },
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    public string AccountFolder(string accountId) => Path.Combine(Root, "accounts", accountId);

    public async Task<long> CountAsync(string accountId, string sql, object? args = null)
    {
        using var conn = Host.ServicesFor(accountId).GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>(sql, args);
    }

    private async Task<string> DiagnosticsAsync(string accountId)
    {
        try
        {
            using var qconn = Host.ServicesFor(accountId).GetRequiredService<DbConnectionFactory>().CreateConnection();
            var quarantined = string.Join("; ", (await qconn.QueryAsync<(string, string, string)>("SELECT event_type, origin_node_id, last_error FROM tbl_sync_quarantine")).Select(q => q.Item1 + " from " + q.Item2 + ": " + q.Item3));
            var position = await CountAsync(accountId, "SELECT COALESCE(MAX(last_sequence_num), -1) FROM tbl_sync_position");
            var rows = string.Join(", ", (await qconn.QueryAsync<(string, long, string)>("SELECT node_id, is_superadmin, status FROM tbl_whitelist")).Select(r => $"{r.Item1[..8]} super={r.Item2} {r.Item3}"));
            var events = await CountAsync(accountId, "SELECT COUNT(*) FROM tbl_event");
            var log = string.Join(" / ", Host.For(accountId).GetStatus().RecentLog.Take(6).Select(l => l.Message));
            return $"[quarantined={quarantined}, whitelist=({rows}), position={position}, events={events}, log: {log}]";
        }
        catch (Exception ex)
        {
            return "[diagnostics failed: " + ex.GetType().Name + "]";
        }
    }

    public Guid NodeOf(string accountId) => Host.For(accountId).GetStatus().NodeId!.Value;

    /// <summary>Does any file of the account's folder hold <paramref name="text"/> (a canary written on a hub)?</summary>
    public bool FolderHolds(string accountId, string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        foreach (var file in Directory.EnumerateFiles(AccountFolder(accountId), "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            if (bytes.AsSpan().IndexOf(needle) >= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Syncs the account until the database holds a folder at <paramref name="path"/>. A round is cheap but the hub answers at most 30 challenges a minute to one address (the rhythm of a
    /// real copy is one round in 15 minutes), so the rounds are a second apart.
    /// </summary>
    public async Task SyncUntilFolderAsync(string accountId, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        var rounds = new List<string>();
        while (true)
        {
            rounds.Add(await Host.For(accountId).RequestSyncAsync());
            if (await CountAsync(accountId, "SELECT COUNT(*) FROM tbl_folder WHERE path = @path", new { path }) == 1) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The folder {path} did not arrive. Rounds: {string.Join(" | ", rounds.Distinct())} {await DiagnosticsAsync(accountId)}");
            await Task.Delay(1000);
        }
    }
}

[Collection(BlindInviteE2ECollection.Name)]
public sealed class BlindInviteEndToEndTests(BlindInviteE2EWorld world)
{
    private sealed record TwoAccounts(DeviceRig Device, string One, string Two, string InviteOne, string InviteTwo, OffsetTimeProvider Clock);

    private static string CardLine(BlindAccountHost host, string accountId) =>
        BlindAccountCard.Build(host.GetSnapshot().Accounts.Single(a => a.Entry.Id == accountId), TimeZoneInfo.Utc, DateTimeOffset.UtcNow).State;

    /// <summary>One device, an account from each hub, both confirmed by their owners, both first-loaded.</summary>
    private async Task<TwoAccounts> BothActiveAsync()
    {
        await world.ResetAsync();
        var clock = new OffsetTimeProvider();
        var device = new DeviceRig(world.Anchors, clock);
        await device.Host.StartAsync();
        var inviteOne = await world.OwnerOne.IssueAsync("Alice");
        var inviteTwo = await world.OwnerTwo.IssueAsync("Bob");

        var one = await device.Host.AddFromInvitationAsync(inviteOne.Text);
        var two = await device.Host.AddFromInvitationAsync(inviteTwo.Text);
        one.Ok.Should().BeTrue(one.Error);
        two.Ok.Should().BeTrue(two.Error);

        await world.OwnerOne.ConfirmAsync(inviteOne.InviteId);
        await world.OwnerTwo.ConfirmAsync(inviteTwo.InviteId);
        (await device.Host.PollInvitationsAsync(force: true)).Should().Be(0);
        foreach (var id in new[] { one.AccountId!, two.AccountId! })
        {
            device.Host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry.Phase.Should().Be(BlindAccountPhases.Active);
            await device.Host.For(id).RunHeavyAsync(forceBackup: false);
            device.Host.For(id).GetStatus().InitialLoadDone.Should().BeTrue("the first load ran through the account's own limits guard and finished");
        }
        return new TwoAccounts(device, one.AccountId!, two.AccountId!, inviteOne.InviteId, inviteTwo.InviteId, clock);
    }

    // ---- two accounts, two hubs -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoAccountsFromTwoHubs_BothConfirmedByTheirOwners_BothFirstLoadAndSync_AndANoteOnEachHubReachesOnlyItsOwnAccount()
    {
        await world.ResetAsync();
        await using var device = new DeviceRig(world.Anchors);
        await device.Host.StartAsync();
        var inviteOne = await world.OwnerOne.IssueAsync("Alice");
        var inviteTwo = await world.OwnerTwo.IssueAsync("Bob");
        var host = device.Host;

        var one = (await host.AddFromInvitationAsync(inviteOne.Text)).AccountId!;
        var two = (await host.AddFromInvitationAsync(inviteTwo.Text)).AccountId!;

        // each account waits for its own owner, with the code that is on that owner's page, and has received nothing
        foreach (var (id, owner, invite) in new[] { (one, world.OwnerOne, inviteOne), (two, world.OwnerTwo, inviteTwo) })
        {
            var entry = host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry;
            entry.Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
            var row = await owner.RowAsync(invite.InviteId);
            row.GetProperty("state").GetString().Should().Be("redeemed");
            row.GetProperty("matchCode").GetString().Should().Be(entry.MatchCode, "the owner reads the same code the friend reads");
            row.GetProperty("deviceName").GetString().Should().Be("E2E phone");
            host.For(id).GetStatus().IsPaired.Should().BeFalse("before the owner confirms the device receives nothing");
            (await owner.WhitelistRowAsync(device.NodeOf(id))).Should().BeNull("a redemption is not membership");
        }
        device.NodeOf(one).Should().NotBe(device.NodeOf(two), "each account has its own identity");

        await world.OwnerOne.ConfirmAsync(inviteOne.InviteId);
        await world.OwnerTwo.ConfirmAsync(inviteTwo.InviteId);
        (await host.PollInvitationsAsync(force: true)).Should().Be(0);
        foreach (var id in new[] { one, two })
        {
            host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry.Phase.Should().Be(BlindAccountPhases.Active);
            await host.For(id).RunHeavyAsync(forceBackup: false);
            host.For(id).GetStatus().InitialLoadDone.Should().BeTrue();
        }

        var noteOne = await world.OwnerOne.WriteNoteAsync("AliceNote");
        var noteTwo = await world.OwnerTwo.WriteNoteAsync("BobNote");
        await device.SyncUntilFolderAsync(one, noteOne);
        await device.SyncUntilFolderAsync(two, noteTwo);

        (await device.CountAsync(one, "SELECT COUNT(*) FROM tbl_folder WHERE path = @path", new { path = noteTwo })).Should().Be(0, "Bob's note never reaches Alice's account");
        (await device.CountAsync(two, "SELECT COUNT(*) FROM tbl_folder WHERE path = @path", new { path = noteOne })).Should().Be(0, "Alice's note never reaches Bob's account");
        SqliteConnection.ClearAllPools();
        device.FolderHolds(one, noteTwo).Should().BeFalse();
        device.FolderHolds(two, noteOne).Should().BeFalse();
        device.FolderHolds(one, noteOne).Should().BeTrue("the note is in the account's own database");
        device.FolderHolds(two, noteTwo).Should().BeTrue();
        host.For(one).GetStatus().Endpoint.Should().Be(world.One.Origin);
        host.For(two).GetStatus().Endpoint.Should().Be(world.Two.Origin);
    }

    /// <summary>
    /// What "Let blind copies call this node" (Admin, Trusted Nodes) gates: the old way of pairing (the list of nodes a pair code can name, <c>BlindPhonePairingService</c>) and the hub list
    /// of the Blind nodes page - both read the whitelist row of the called node, which another full node of the network sets. Nothing a copy of an invitation calls reads it. These hubs are each
    /// the only computer of their network, so no other node can have set the switch on them: it is off, and the copy still redeems, is confirmed, loads and syncs.
    /// </summary>
    [Fact]
    public async Task WithTheHubSwitchOff_ACopyOfAnInvitation_StillRedeemsIsConfirmedLoadsAndSyncs()
    {
        await world.ResetAsync();
        var hubId = (await world.One.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        (await world.OwnerOne.WhitelistRowAsync(hubId)).Should().BeNull("the switch is a record on the called node's row, set from another node of the network: there is none here, so it is off");
        var listeners = await world.One.Admin.GetFromJsonAsync<JsonElement>("/api/blind-nodes/android/listeners");
        listeners.EnumerateArray().Should().BeEmpty("with the switch off this node is not offered to the old way of pairing");

        await using var device = new DeviceRig(world.Anchors);
        await device.Host.StartAsync();
        var invitation = await world.OwnerOne.IssueAsync("Alice");
        var id = (await device.Host.AddFromInvitationAsync(invitation.Text)).AccountId!;
        await world.OwnerOne.ConfirmAsync(invitation.InviteId);
        await device.Host.PollInvitationsAsync(force: true);
        device.Host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry.Phase.Should().Be(BlindAccountPhases.Active);
        await device.Host.For(id).RunHeavyAsync(forceBackup: false);
        device.Host.For(id).GetStatus().InitialLoadDone.Should().BeTrue("the first load goes through the route the invitation's call code names, whatever the switch says");

        var note = await world.OwnerOne.WriteNoteAsync("SwitchOff");
        await device.SyncUntilFolderAsync(id, note);
        (await world.OwnerOne.WhitelistRowAsync(hubId)).Should().BeNull("nothing in the whole path made the switch matter");
    }

    [Fact]
    public async Task RevokingOnHubOne_ShowsTheCardRemovedOnAccountOne_WhileAccountTwoStillSyncs()
    {
        var both = await BothActiveAsync();
        await using var device = both.Device;
        var host = device.Host;
        var firstNote = await world.OwnerOne.WriteNoteAsync("BeforeRevoke");
        await device.SyncUntilFolderAsync(both.One, firstNote);

        await world.OwnerOne.RevokeAsync(device.NodeOf(both.One));
        // two refusals in a row mean "removed": the first is only remembered, the account goes on asking and says so neutrally
        (await host.For(both.One).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence);
        host.GetSnapshot().Accounts.Single(a => a.Entry.Id == both.One).Entry.Phase.Should().Be(BlindAccountPhases.Active);
        CardLine(host, both.One).Should().Be(BlindSourceRemoval.FirstRefusalSentence);
        both.Clock.Offset += BlindSourceRemoval.MinimumGap;       // one normal retry interval later
        var result = await host.For(both.One).RequestSyncAsync();

        result.Should().Be("The source no longer accepts this device. The copy stays until you remove it.");
        host.GetSnapshot().Accounts.Single(a => a.Entry.Id == both.One).Entry.Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        CardLine(host, both.One).Should().Be("Alice removed this device from the network. The copy stays until you remove it.");
        // the copy stays: its data is still there, and it can be read as before (the owner's data is ciphertext and sealed metadata)
        (await device.CountAsync(both.One, "SELECT COUNT(*) FROM tbl_folder WHERE path = @path", new { path = firstNote })).Should().Be(1);

        // no retry storm: the scheduler leaves the removed account alone
        await using var scheduler = new BlindDeviceScheduler(host);
        await scheduler.TickAsync();
        host.GetSnapshot().Accounts.Single(a => a.Entry.Id == both.One).Entry.Phase.Should().Be(BlindAccountPhases.RemovedByOwner);

        // account two is not affected
        var secondNote = await world.OwnerTwo.WriteNoteAsync("StillSyncing");
        await device.SyncUntilFolderAsync(both.Two, secondNote);
        host.GetSnapshot().Accounts.Single(a => a.Entry.Id == both.Two).Entry.Phase.Should().Be(BlindAccountPhases.Active);
    }

    [Fact]
    public async Task TheOwnerRevokesTheDeviceBeforeItsFirstLoad_TheCardSaysRemovedByOwner_AndTheFirstLoadIsNotRetried()
    {
        // Review MAJOR-8: confirmed, then revoked before the first download. The challenge answers 401 to the first load; that is the source removing the device, not a first load to retry for ever.
        await world.ResetAsync();
        var clock = new OffsetTimeProvider();
        await using var device = new DeviceRig(world.Anchors, clock);
        await device.Host.StartAsync();
        var invitation = await world.OwnerOne.IssueAsync("Alice");
        var added = await device.Host.AddFromInvitationAsync(invitation.Text);
        added.Ok.Should().BeTrue(added.Error);
        await world.OwnerOne.ConfirmAsync(invitation.InviteId);
        await device.Host.PollInvitationsAsync(force: true);
        var id = added.AccountId!;
        device.Host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry.Phase.Should().Be(BlindAccountPhases.Active);
        device.Host.For(id).GetStatus().InitialLoadDone.Should().BeFalse("nothing has been downloaded yet");

        await world.OwnerOne.RevokeAsync(device.NodeOf(id));
        (await device.Host.For(id).RunHeavyAsync(forceBackup: false)).Should().Be(BlindSourceRemoval.FirstRefusalSentence);
        device.Host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry.Phase.Should().Be(BlindAccountPhases.Active, "one refusal is only remembered");
        clock.Offset += BlindSourceRemoval.MinimumGap;            // one normal retry interval later
        var result = await device.Host.For(id).RunHeavyAsync(forceBackup: false);

        result.Should().Be("The source no longer accepts this device. The copy stays until you remove it.");
        device.Host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry.Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        CardLine(device.Host, id).Should().Be("Alice removed this device from the network. The copy stays until you remove it.");
        device.Host.For(id).GetStatus().LastFailure.Should().BeNull("it is not a first load that failed and will be tried again");

        await using var scheduler = new BlindDeviceScheduler(device.Host);
        await scheduler.TickAsync();
        device.Host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry.Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        device.Host.For(id).GetStatus().LastFailure.Should().BeNull("the removed account is left alone: no first load, no retry storm");
    }

    [Fact]
    public async Task WhenTheFriendRemovesAnAccount_TheHubIsToldBeforeTheKeysGo_AndNothingIsRevoked()
    {
        var both = await BothActiveAsync();
        await using var device = both.Device;
        var host = device.Host;
        var node = device.NodeOf(both.One);

        await host.RemoveAsync(both.One);

        (await world.OwnerOne.AuditCountAsync("blind_copy_left", node.ToString())).Should().Be(1, "the hub's page now says the device's owner removed this copy");
        (await world.OwnerOne.WhitelistRowAsync(node))!.Status.Should().Be("A", "only the owner of the network can revoke: the hub just notes it");
        host.GetSnapshot().Accounts.Should().ContainSingle(a => a.Entry.Id == both.Two);
        Directory.Exists(device.AccountFolder(both.One)).Should().BeFalse();
        device.Secrets.BlobCount(new BlindAccountEntry(both.One, BlindAccountLayouts.Dir, "x")).Should().Be(0);
        // the other account is untouched and still syncs
        var note = await world.OwnerTwo.WriteNoteAsync("AfterTheOtherLeft");
        await device.SyncUntilFolderAsync(both.Two, note);
    }

    // ---- an eavesdropper ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEavesdropperWhoRedeemsFirst_GetsAPendingRequestTheOwnerRefuses_AndTheFriendIsToldItWasAlreadyUsed()
    {
        await world.ResetAsync();
        await using var friend = new DeviceRig(world.Anchors);
        await using var eavesdropper = new DeviceRig(world.Anchors);
        await friend.Host.StartAsync();
        await eavesdropper.Host.StartAsync();
        var invite = await world.OwnerOne.IssueAsync("Alice");

        var stolen = await eavesdropper.Host.AddFromInvitationAsync(invite.Text);
        stolen.Ok.Should().BeTrue(stolen.Error);
        var friendAdds = await friend.Host.AddFromInvitationAsync(invite.Text);

        friendAdds.Ok.Should().BeFalse();
        friendAdds.Error.Should().Be(BlindInvitationWords.NotValid("Alice"), "one sentence for everything that is not valid: it was already used");
        friend.Host.GetSnapshot().Accounts.Should().BeEmpty("nothing is left on the friend's device");
        var row = await world.OwnerOne.RowAsync(invite.InviteId);
        row.GetProperty("state").GetString().Should().Be("redeemed");
        var stolenEntry = eavesdropper.Host.GetSnapshot().Accounts.Single().Entry;
        row.GetProperty("matchCode").GetString().Should().Be(stolenEntry.MatchCode, "the owner sees the eavesdropper's code - which the friend, who never got one, cannot read out");
        (await world.OwnerOne.WhitelistRowAsync(eavesdropper.NodeOf(stolenEntry.Id))).Should().BeNull("nothing before a confirmation");

        await world.OwnerOne.RefuseAsync(invite.InviteId);
        await eavesdropper.Host.PollInvitationsAsync(force: true);

        eavesdropper.Host.GetSnapshot().Accounts.Single().Entry.Phase.Should().Be(BlindAccountPhases.Refused);
        eavesdropper.Host.For(stolenEntry.Id).GetStatus().IsPaired.Should().BeFalse("and no data, ever");
        (await world.OwnerOne.WhitelistRowAsync(eavesdropper.NodeOf(stolenEntry.Id))).Should().BeNull();
    }

    // ---- a lost answer, an expired invitation, a locked hub, an old hub -------------------------------------------------------------------------

    private sealed class LosesTheFirstRedeemAnswer : DelegatingHandler
    {
        private int _lost;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath == "/api/sync/invite/redeem" && Interlocked.Exchange(ref _lost, 1) == 0)
            {
                response.Dispose();
                throw new HttpRequestException("the answer was lost on the way");
            }
            return response;
        }
    }

    [Fact]
    public async Task ALost202_IsRetriedWithTheSameIdentity_AndTheHubKeepsOneRedemption()
    {
        await world.ResetAsync();
        var clock = new OffsetTimeProvider();
        await using var device = new DeviceRig(world.Anchors, clock, wrap: inner => new LosesTheFirstRedeemAnswer { InnerHandler = inner });
        await device.Host.StartAsync();
        var invite = await world.OwnerOne.IssueAsync("Alice");

        var added = await device.Host.AddFromInvitationAsync(invite.Text);

        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;
        device.Host.GetSnapshot().Accounts.Single().Entry.Phase.Should().Be(BlindAccountPhases.Pending);
        var row = await world.OwnerOne.RowAsync(invite.InviteId);
        row.GetProperty("state").GetString().Should().Be("redeemed", "the hub took it; only its answer was lost");
        row.GetProperty("deviceId").GetString().Should().Be(device.NodeOf(id).ToString());

        clock.Offset = TimeSpan.FromMinutes(1);
        await device.Host.PollInvitationsAsync(force: true);

        var entry = device.Host.GetSnapshot().Accounts.Single().Entry;
        entry.Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
        var after = await world.OwnerOne.RowAsync(invite.InviteId);
        after.GetProperty("deviceId").GetString().Should().Be(device.NodeOf(id).ToString(), "the same identity asked again");
        after.GetProperty("matchCode").GetString().Should().Be(entry.MatchCode);
        (await world.OwnerOne.ListAsync()).Count(r => r.GetProperty("inviteId").GetString() == invite.InviteId).Should().Be(1);
    }

    [Fact]
    public async Task AnInvitationTheHubSaysHasExpired_IsNotValid_AndLeavesNoAccount()
    {
        await world.ResetAsync();
        await using var device = new DeviceRig(world.Anchors);
        await device.Host.StartAsync();
        var invite = await world.OwnerOne.IssueAsync("Alice", minutes: 15);
        world.ClockOne.Offset = TimeSpan.FromMinutes(16);          // the hub's clock moved: this device's clock has not

        var added = await device.Host.AddFromInvitationAsync(invite.Text);

        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.NotValid("Alice"));
        device.Host.GetSnapshot().Accounts.Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(Path.Combine(device.Root, "accounts")).Should().BeEmpty();
    }

    [Fact]
    public async Task OnADeviceWhoseClockIsPastTheExpiry_ThePreviewRefusesBeforeAnythingIsSent()
    {
        await world.ResetAsync();
        var late = new OffsetTimeProvider { Offset = TimeSpan.FromMinutes(20) };
        await using var device = new DeviceRig(world.Anchors, late);
        await device.Host.StartAsync();
        var invite = await world.OwnerOne.IssueAsync("Alice", minutes: 15);

        var preview = device.Host.PreviewInvitation(invite.Text);

        preview.CanAdd.Should().BeFalse();
        preview.RefusalKind.Should().Be(BlindInvitationRefusals.Expired);
        (await world.OwnerOne.RowAsync(invite.InviteId)).GetProperty("state").GetString().Should().Be("issued", "nothing was sent");
    }

    [Fact]
    public async Task AHubThatIsLocked_Answers503_TheAccountWaits_AndGoesOnWhenTheOwnerUnlocks()
    {
        await world.ResetAsync();
        var clock = new OffsetTimeProvider();
        await using var device = new DeviceRig(world.Anchors, clock);
        await device.Host.StartAsync();
        var invite = await world.OwnerOne.IssueAsync("Alice");
        await world.OwnerOne.LockAsync();

        var added = await device.Host.AddFromInvitationAsync(invite.Text);

        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;
        device.Host.GetSnapshot().Accounts.Single().Entry.Phase.Should().Be(BlindAccountPhases.Pending, "the hub could not take the backup key: nothing was stored, and the account waits");
        device.Host.GetSnapshot().Accounts.Single().Status!.NodeId.Should().NotBeNull();

        await world.OwnerOne.UnlockAsync();
        clock.Offset = TimeSpan.FromMinutes(6);
        (await device.Host.PollInvitationsAsync(force: true)).Should().Be(1);

        device.Host.GetSnapshot().Accounts.Single().Entry.Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
        await world.OwnerOne.ConfirmAsync(invite.InviteId);
        await device.Host.PollInvitationsAsync(force: true);
        device.Host.GetSnapshot().Accounts.Single().Entry.Phase.Should().Be(BlindAccountPhases.Active);
        device.NodeOf(id).Should().Be(device.NodeOf(id));
    }

    /// <summary>A full node older than 2.7, as far as invitations go: it answers its identity, and the routes of an invitation do not exist.</summary>
    private sealed class OlderHubStub(Guid nodeId, byte[] publicKey) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Method.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath != "/api/sync/identity") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { nodeId, displayName = "Old hub", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey), protocolVersion = 3 },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task AHubWithoutTheRoutes_404_GetsTheExactSentence_AndNoAccount_AndNoAutomaticFallback()
    {
        await world.ResetAsync();
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        var hubId = Guid.NewGuid();
        var stub = new OlderHubStub(hubId, publicKey);
        await using var device = new DeviceRig(world.Anchors, bottom: () => stub);
        await device.Host.StartAsync();
        var invitation = BlindInvitation.CreateSigned("https://old-hub.example:5300", hubId, BlindTrust.PublicCa, null, publicKey, RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(30), "Alex", payload => Ed25519Signer.Sign(seed, payload));

        var added = await device.Host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeFalse();
        added.Error.Should().Be("The owner's Bee Memory Bank is older than 2.7 and cannot take invitations; ask the owner to update, or pair with a code the old way.");
        device.Host.GetSnapshot().Accounts.Should().BeEmpty();
        stub.Requests.Should().Equal("GET /api/sync/identity", "POST /api/sync/invite/redeem");
    }

    [Fact]
    public async Task AnInvitationForAHubThatPresentsADifferentCertificate_IsRefused_AndNothingIsSent()
    {
        await world.ResetAsync();
        // a device whose trust anchors do not include the test CA: the hub's certificate is not one it can vouch for
        await using var device = new DeviceRig(TlsTrustAnchors.None);
        await device.Host.StartAsync();
        var invite = await world.OwnerOne.IssueAsync("Alice");

        var added = await device.Host.AddFromInvitationAsync(invite.Text);

        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.CertificateRefused(new Uri(world.One.Origin).Host));
        (await world.OwnerOne.RowAsync(invite.InviteId)).GetProperty("state").GetString().Should().Be("issued", "the secret never left the device");
        device.Host.GetSnapshot().Accounts.Should().BeEmpty();
    }
}
