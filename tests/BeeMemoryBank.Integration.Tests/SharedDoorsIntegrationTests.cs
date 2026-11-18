using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Sealed;
using BeeMemoryBank.Vault.SharedDoors;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

[Collection("DerivationCounter")]
public class SharedDoorsIntegrationTests
    : IClassFixture<SealedNodeTemplate>, IAsyncLifetime
{
    private sealed class LoginDerivationRecorder : IStartupFilter
    {
        public List<long> Counts { get; } = [];

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nxt) =>
            {
                if (ctx.Request.Path.Equals("/api/session/login", StringComparison.OrdinalIgnoreCase))
                {
                    using var scoped = KeyDerivation.BeginScopedCountForTests();
                    await nxt();
                    Counts.Add(scoped.Count);
                }
                else
                {
                    await nxt();
                }
            });
            next(app);
        };
    }

    private sealed class RecordingWebApplicationFactory(LoginDerivationRecorder recorder) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s => s.AddSingleton<IStartupFilter>(recorder));
        }
    }

    private readonly LoginDerivationRecorder _sharedRecorder = new();
    private readonly SealedNodeTemplate _sealedTemplate;
    private readonly BmbWebApplicationFactory _homeFactory = new();
    private readonly BmbWebApplicationFactory _sharedFactory;
    private HttpClient _homeClient = null!;
    private HttpClient _sharedClient = null!;

    public SharedDoorsIntegrationTests(SealedNodeTemplate sealedTemplate)
    {
        _sealedTemplate = sealedTemplate;
        _sharedFactory = new RecordingWebApplicationFactory(_sharedRecorder);
    }

    private const string AlicePass = "AliceHome1";
    private const string BobPass = "BobHome22";
    private const string SharedInstallerPass = "SharedInstaller1";

    public async Task InitializeAsync()
    {
        _homeClient = _homeFactory.CreateClient();
        _sharedClient = _sharedFactory.CreateClient();

        await _homeFactory.InitializeNodeAsync(displayName: "HomeNode", password: AlicePass);
        await _sharedFactory.InitializeNodeAsync(displayName: "SharedBackup", password: SharedInstallerPass);

        (_homeClient.PostAsJsonAsync("/api/session/unlock", new { password = AlicePass })).GetAwaiter().GetResult().EnsureSuccessStatusCode();
        (_sharedClient.PostAsJsonAsync("/api/session/unlock", new { password = SharedInstallerPass })).GetAwaiter().GetResult().EnsureSuccessStatusCode();

        // Add bob as second superadmin on HomeNode.
        using (var homeScope = _homeFactory.Services.CreateScope())
        {
            var userSvc = homeScope.ServiceProvider.GetRequiredService<UserService>();
            await userSvc.CreateUserAsync("bob", "Bob", BobPass, UserRoles.Superadmin, passwordHint: "second admin clue");
        }

        // Wire mutual trust as shared peers.
        using var homeS = _homeFactory.Services.CreateScope();
        using var sharedS = _sharedFactory.Services.CreateScope();
        var homeId = (await homeS.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var sharedId = (await sharedS.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;

        var homeWl = homeS.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        await homeWl.CreateAsync(new WhitelistEntry
        {
            NodeId = sharedId.NodeId,
            DisplayName = sharedId.DisplayName,
            Ed25519PublicKey = sharedId.Ed25519PublicKey,
            Status = WhitelistStatuses.Active,
            IsSuperadmin = true,
            Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await homeWl.RecordCapabilitiesAsync(sharedId.NodeId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);

        var sharedWl = sharedS.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        await sharedWl.CreateAsync(new WhitelistEntry
        {
            NodeId = homeId.NodeId,
            DisplayName = homeId.DisplayName,
            Ed25519PublicKey = homeId.Ed25519PublicKey,
            Status = WhitelistStatuses.Active,
            IsSuperadmin = true,
            Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await sharedWl.RecordCapabilitiesAsync(homeId.NodeId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);

        await sharedS.ServiceProvider.GetRequiredService<ISharedDoorsStateRepository>()
            .SetSelfOwnershipAsync(OwnershipKinds.Shared, 0, homeId.NodeId);

        _homeFactory.Services.GetRequiredService<SuperadminDoorsPublisher>().DebounceDelay = TimeSpan.Zero;

        // Set accounts_home to HomeNode via HTTP PUT /api/accounts-home and propagate control events to SharedBackup.
        var setResp = await SuperadminPost(_homeClient, HttpMethod.Put, "/api/accounts-home", new { nodeId = homeId.NodeId });
        setResp.EnsureSuccessStatusCode();
        await DeliverControlEventsAsync();
    }

    public Task DisposeAsync()
    {
        _homeClient.Dispose();
        _sharedClient.Dispose();
        _homeFactory.Dispose();
        _sharedFactory.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<HttpResponseMessage> SuperadminPost(HttpClient client, HttpMethod method, string url, object? body = null, int userId = 1)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-User-Role", "superadmin");
        req.Headers.Add("X-User-Id", userId.ToString());
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    private async Task DeliverControlEventsAsync()
    {
        using var homeS = _homeFactory.Services.CreateScope();
        using var sharedS = _sharedFactory.Services.CreateScope();
        var homeLog = homeS.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var sharedLog = sharedS.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var applier = sharedS.ServiceProvider.GetRequiredService<EventApplier>();

        var events = (await homeLog.GetAllAfterSequenceAsync(0, 200))
            .Where(e => e.EventType is EventTypes.AccountsHomeSet or EventTypes.SuperadminDoorsSet or EventTypes.NodeState)   // node_state
            .ToList();

        foreach (var evt in events)
        {
            if (await sharedLog.ExistsAsync(evt.EventId)) continue;
            await applier.ApplyAsync(evt);
        }

        await sharedS.ServiceProvider.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
    }

    [Fact]
    public async Task SharedNode_EndToEnd_Login_Join_PasswordChange_Removal_And_Refusals()
    {
        // 1. Both listed superadmins sign in on SharedBackup with their HomeNode passwords (installer/admin's old password is gone!).
        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = SharedInstallerPass }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Lock SharedBackup to also verify locked plain-node login uses ONE Argon2 derivation and unlocks cleanly!
        _sharedFactory.Services.GetRequiredService<SessionService>().Lock();
        UserService.BurnPasswordVerification("warmup");
        _sharedRecorder.Counts.Clear();
        var aliceLogin = await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = AlicePass });
        aliceLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        _sharedRecorder.Counts.Should().ContainSingle().Which.Should().Be(1, "plain locked shared login must reuse the door's single Argon2 derivation");
        var aliceBody = await aliceLogin.Content.ReadFromJsonAsync<LoginResponse>();
        aliceBody!.Role.Should().Be(UserRoles.Superadmin);
        aliceBody.IsUnlocked.Should().BeTrue();

        var bobLogin = await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "bob", password = BobPass });
        bobLogin.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. /api/join on SharedBackup accepts Bob's password, and rejects installer's old password.
        var (joinPub, _) = Ed25519Signer.GenerateKeyPair();
        var badJoin = await _sharedClient.PostAsJsonAsync("/api/join", new
        {
            nodeId = Guid.NewGuid(),
            displayName = "BadJoiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(joinPub),
            masterPassword = SharedInstallerPass,
        });
        badJoin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var okJoin = await _sharedClient.PostAsJsonAsync("/api/join", new
        {
            nodeId = Guid.NewGuid(),
            displayName = "GoodJoiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(joinPub),
            masterPassword = BobPass,
        });
        okJoin.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. HTTP refusals on SharedBackup for replicated rows & creating local superadmin.
        var usersResp = await SuperadminPost(_sharedClient, HttpMethod.Get, "/api/users", userId: aliceBody!.UserId);
        usersResp.EnsureSuccessStatusCode();
        var usersList = await usersResp.Content.ReadFromJsonAsync<List<UserListItemResponse>>();
        var sharedBob = usersList!.Single(u => u.Username == "bob");
        sharedBob.Replicated.Should().BeTrue();

        (await SuperadminPost(_sharedClient, HttpMethod.Post, "/api/users", new
        {
            username = "local2", displayName = "Local2", password = "Local2Pass!", role = UserRoles.Superadmin, passwordHint = "hint"
        }, userId: aliceBody.UserId)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await SuperadminPost(_sharedClient, HttpMethod.Put, $"/api/users/{sharedBob.Id}", new { displayName = "HackedBob" }, userId: aliceBody.UserId))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await SuperadminPost(_sharedClient, HttpMethod.Delete, $"/api/users/{sharedBob.Id}", userId: aliceBody.UserId))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await SuperadminPost(_sharedClient, HttpMethod.Post, "/api/keys/change-password",
            new { oldPassword = BobPass, newPassword = "BobNewPass1", passwordHint = "new" }, userId: sharedBob.Id))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Recover-access: mint HomeNode admin's personal recovery key on SharedBackup -> wrong key or naming another superadmin (bob)
        // returns 401 (the no-oracle rule); naming admin with admin's own key returns 409 naming HomeNode.
        var recResp = await SuperadminPost(_sharedClient, HttpMethod.Post, "/api/keys/add-recovery", userId: aliceBody.UserId);
        recResp.EnsureSuccessStatusCode();
        var recKey = (await recResp.Content.ReadFromJsonAsync<RecoveryKeyResponse>())!.RecoveryKey;

        (await _sharedClient.PostAsJsonAsync("/api/session/recover-access", new
        {
            username = "bob", recoveryKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", newPassword = "BobNewPass1", passwordHint = "hint"
        })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await _sharedClient.PostAsJsonAsync("/api/session/recover-access", new
        {
            username = "bob", recoveryKey = recKey, newPassword = "BobNewPass1", passwordHint = "hint"
        })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var recValid = await _sharedClient.PostAsJsonAsync("/api/session/recover-access", new
        {
            username = aliceBody.Username, recoveryKey = recKey, newPassword = "AliceNewPass1", passwordHint = "hint"
        });
        recValid.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await recValid.Content.ReadAsStringAsync()).Should().Contain("HomeNode");

        // 4. Password change on HomeNode -> publishes new list -> old fails, new works on SharedBackup.
        using (var homeS = _homeFactory.Services.CreateScope())
        {
            var homeUsers = homeS.ServiceProvider.GetRequiredService<UserService>();
            var homeBob = (await homeS.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("bob"))!;
            await homeUsers.ChangePasswordAsync(homeBob.Id, BobPass, "BobUpdated2", passwordHint: "updated");
        }
        await DeliverControlEventsAsync();

        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "bob", password = BobPass }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "bob", password = "BobUpdated2" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 5. Demote Bob on HomeNode -> removes Bob's door/mirror row on SharedBackup -> login AND /api/join fail!
        using (var homeS = _homeFactory.Services.CreateScope())
        {
            var homeUsers = homeS.ServiceProvider.GetRequiredService<UserService>();
            var homeBob = (await homeS.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("bob"))!;
            await homeUsers.DemoteSuperadminAsync(homeBob.Id, UserRoles.User, allowOpenRole: true);
        }
        await DeliverControlEventsAsync();

        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "bob", password = "BobUpdated2" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _sharedClient.PostAsJsonAsync("/api/join", new
        {
            nodeId = Guid.NewGuid(),
            displayName = "RemovedBobJoiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(joinPub),
            masterPassword = "BobUpdated2",
        })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Identity re-key x shared doors seam: a shared node gives itself a new identity key. A list the accounts node publishes before it learns the rotation is sealed to
    /// the retired key and cannot be opened; once node_key_rotated reaches the accounts node, the doors notifier makes it seal the next list to
    /// the NEW key (the publisher reads tbl_whitelist.ed25519_public_key, which the rotation moved), and the shared node opens it.
    /// </summary>
    [Fact]
    public async Task A_shared_node_that_rekeyed_receives_and_opens_the_next_list_once_its_rotation_reaches_the_accounts_node()
    {
        // The shared node re-keys; the accounts node has not seen the rotation yet.
        SyncEvent rotation;
        using (var sharedS = _sharedFactory.Services.CreateScope())
        {
            var result = await sharedS.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("test", "test");
            result.Done.Should().BeTrue(result.Refusal);
            rotation = (await sharedS.ServiceProvider.GetRequiredService<IEventLogRepository>().GetRecentAsync(1, 0, EventTypes.NodeKeyRotated)).Single();
        }

        // A new superadmin on the accounts node: its list is sealed to the shared node's retired key, which no longer opens anything there.
        using (var homeS = _homeFactory.Services.CreateScope())
            await homeS.ServiceProvider.GetRequiredService<UserService>().CreateUserAsync("carol", "Carol", "CarolHome3", UserRoles.Superadmin, passwordHint: "third");
        await DeliverControlEventsAsync();
        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "carol", password = "CarolHome3" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the list was sealed to the key the shared node retired");

        // The rotation reaches the accounts node: it follows the new key and seals the list again, to it.
        using (var homeS = _homeFactory.Services.CreateScope())
        {
            (await homeS.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(rotation)).Should().Be(EventApplyResult.Applied);
            var sharedId = (await _sharedFactory.Services.CreateScope().ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            (await homeS.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(sharedId.NodeId))!
                .Ed25519PublicKey.Should().Equal(sharedId.Ed25519PublicKey);
        }
        await DeliverControlEventsAsync();
        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "carol", password = "CarolHome3" }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the next list is sealed to the shared node's new key");
    }

    /// <summary>
    /// Identity re-key x shared doors seam: the accounts node's own superadmin_doors_set and accounts_home_set are authored inside the event-write gate, like every own
    /// event. An identity re-key holds that gate while it reads the node's own events and swaps the key; a publish that
    /// started meanwhile waits and signs with the key that is current when it writes, never with one retired a moment earlier (peers would
    /// refuse it: it is above the rotation's checkpoint and not on its list).
    /// </summary>
    [Fact]
    public async Task The_accounts_nodes_doors_and_home_events_are_signed_by_the_key_current_when_they_are_written()
    {
        var newKey = await PublishWhileTheKeyIsSwappedAsync(async sp =>
        {
            await sp.GetRequiredService<ISharedDoorsStateRepository>().InvalidatePublishedDigestAsync();
            await sp.GetRequiredService<SuperadminDoorsPublisher>().NotifyChangedAsync(immediate: true);
        });
        await NewestOwnEventVerifiesUnderAsync(EventTypes.SuperadminDoorsSet, newKey);

        newKey = await PublishWhileTheKeyIsSwappedAsync(async sp =>
        {
            var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            await sp.GetRequiredService<AccountsHomeService>().SetAsync(self.NodeId, actor: "test");
        });
        await NewestOwnEventVerifiesUnderAsync(EventTypes.AccountsHomeSet, newKey);
    }

    /// <summary>
    /// Holds the event-write gate as a re-key does, starts <paramref name="publish"/> on the accounts node, gives the node a new identity key
    /// while the gate is held, then lets the gate go. Returns the new public key.
    /// </summary>
    private async Task<byte[]> PublishWhileTheKeyIsSwappedAsync(Func<IServiceProvider, Task> publish)
    {
        var (newPublicKey, newSeed) = Ed25519Signer.GenerateKeyPair();
        Task running;
        using (await EventWriteGate.Instance.QuiesceAsync())
        {
            var scope = _homeFactory.Services.CreateScope();
            running = Task.Run(async () =>
            {
                try { await publish(scope.ServiceProvider); }
                finally { scope.Dispose(); }
            });
            await Task.Delay(500);
            var self = (await _homeFactory.Services.CreateScope().ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            var dek = _homeFactory.Services.GetRequiredService<SessionService>().GetMasterDek();
            try
            {
                var (wrapped, iv) = NodeIdentityVault.EncryptPrivateKey(newSeed, dek, self.NodeId);
                using var conn = _homeFactory.Services.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>().CreateConnection();
                await conn.ExecuteAsync(
                    @"UPDATE tbl_node_identity SET ed25519_public_key = @pk, ed25519_private_key = @key, ed25519_private_key_iv = @iv,
                             ed25519_private_key_v = 1 WHERE node_id = @id COLLATE NOCASE",
                    new { pk = newPublicKey, key = wrapped, iv, id = self.NodeId.ToString("D") });
            }
            finally
            {
                Array.Clear(dek);
                Array.Clear(newSeed);
            }
        }
        await running;
        return newPublicKey;
    }

    private async Task NewestOwnEventVerifiesUnderAsync(string eventType, byte[] key)
    {
        using var scope = _homeFactory.Services.CreateScope();
        var evt = (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetRecentAsync(1, 0, eventType)).Single();
        NodeEventVerifier.SignatureVerifies(key, EventSignature.BuildPayload(evt), evt.Signature)
            .Should().BeTrue($"the {eventType} written after the key changed is signed by the current key");
    }

    [Fact]
    public async Task SealedSharedNode_Login_OpensFromBootStoreAndReusesUnlockDerivation()
    {
        var sealedDir = await SealedApiDirBuilder.BuildAsync(_sealedTemplate);
        // Apply the REAL superadmin_doors_set list from HomeNode onto the unlocked sealed node
        // (no raw SQL UPDATE on tbl_user) so SharedDoorsApplier seals doors and rewrites boot store.
        using (var prepFactory = new SealedApiFactory(sealedDir.Dir) { KeepData = true })
        {
            using var prepClient = prepFactory.CreateClient();
            (await prepClient.PostAsJsonAsync("/api/session/unlock", new { password = SealedNodeTemplate.Password }))
                .EnsureSuccessStatusCode();

            using var homeS = _homeFactory.Services.CreateScope();
            using var scope = prepFactory.Services.CreateScope();
            var homeIdentity = (await homeS.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            var sealedIdentity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;

            // Wire mutual trust and set HomeNode -> home (or publish for the new sealed peer).
            var homeWl = homeS.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            await homeWl.CreateAsync(new WhitelistEntry
            {
                NodeId = sealedIdentity.NodeId,
                DisplayName = sealedIdentity.DisplayName,
                Ed25519PublicKey = sealedIdentity.Ed25519PublicKey,
                Status = WhitelistStatuses.Active,
                IsSuperadmin = true,
                Ownership = OwnershipKinds.Shared,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await homeWl.RecordCapabilitiesAsync(sealedIdentity.NodeId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);
            await _homeFactory.Services.GetRequiredService<SuperadminDoorsPublisher>().PublishIfDueAsync();

            var prepWl = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            await prepWl.CreateAsync(new WhitelistEntry
            {
                NodeId = homeIdentity.NodeId,
                DisplayName = homeIdentity.DisplayName,
                Ed25519PublicKey = homeIdentity.Ed25519PublicKey,
                Status = WhitelistStatuses.Active,
                IsSuperadmin = true,
                Ownership = OwnershipKinds.Shared,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await prepWl.RecordCapabilitiesAsync(homeIdentity.NodeId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);
            await scope.ServiceProvider.GetRequiredService<ISharedDoorsStateRepository>()
                .SetSelfOwnershipAsync(OwnershipKinds.Shared, 0, homeIdentity.NodeId);

            var homeLog = homeS.ServiceProvider.GetRequiredService<IEventLogRepository>();
            var prepLog = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
            foreach (var evt in (await homeLog.GetAllAfterSequenceAsync(0, 200))
                         .Where(e => e.EventType is EventTypes.AccountsHomeSet or EventTypes.SuperadminDoorsSet or EventTypes.NodeState))
            {
                if (!await prepLog.ExistsAsync(evt.EventId))
                    await applier.ApplyAsync(evt);
            }

            // EventApplier triggers SharedDoorsApplier automatically when unlocked and marked shared;
            // verify the real door list was applied (version > 0 and bob is replicated).
            var stateRepo = scope.ServiceProvider.GetRequiredService<ISharedDoorsStateRepository>();
            await scope.ServiceProvider.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
            var appliedState = await stateRepo.GetAsync();
            appliedState.AppliedVersion.Should().BeGreaterThan(0);
            appliedState.LastError.Should().BeNullOrEmpty();
            var usersAfter = await scope.ServiceProvider.GetRequiredService<IUserRepository>().ListActiveAsync();
            usersAfter.Should().Contain(u => u.Username == "bob" && u.Replicated);
        }

        // Cold start over the closed sealed node directory: old installer password fails,
        // HomeNode's Bob opens via boot store and reuses the unlock derivation (no extra AuthenticateAsync pass).
        long bobCount;
        var coldRecorder = new LoginDerivationRecorder();
        using (var coldFactory = new SealedApiFactory(sealedDir.Dir, s => s.AddSingleton<IStartupFilter>(coldRecorder)) { KeepData = true })
        {
            using var coldClient = coldFactory.CreateClient();
            (await coldClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = SealedNodeTemplate.Password }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            UserService.BurnPasswordVerification("warmup");
            coldRecorder.Counts.Clear();
            var loginResp = await coldClient.PostAsJsonAsync("/api/session/login", new { username = "bob", password = BobPass });
            loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
            bobCount = coldRecorder.Counts.Should().ContainSingle().Subject;
            bobCount.Should().BeInRange(1, 2);
            var loginDto = await loginResp.Content.ReadFromJsonAsync<LoginResponse>();
            loginDto!.IsUnlocked.Should().BeTrue();
            loginDto.Role.Should().Be(UserRoles.Superadmin);
        }

        // Second cold start logging in as HomeNode's admin: with 2 doors in the boot store and no extra
        // AuthenticateAsync derivation on either login, the two door-search counts must total 1 + 2 = 3.
        var coldRecorder2 = new LoginDerivationRecorder();
        using var coldFactory2 = new SealedApiFactory(sealedDir.Dir, s => s.AddSingleton<IStartupFilter>(coldRecorder2)) { KeepData = true };
        using var coldClient2 = coldFactory2.CreateClient();
        coldRecorder2.Counts.Clear();
        var adminResp = await coldClient2.PostAsJsonAsync("/api/session/login", new { username = "admin", password = AlicePass });
        adminResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminCount = coldRecorder2.Counts.Should().ContainSingle().Subject;
        adminCount.Should().BeInRange(1, 2);
        (bobCount + adminCount).Should().Be(3, "sealed shared login reuses the single door that opened the boot store without running AuthenticateAsync");
    }

    [Fact]
    public async Task SinglePersonBank_SharedDoorsStatus_NeverOffersAccountsHome()
    {
        using var soloFactory = new BmbWebApplicationFactory();
        using var soloClient = soloFactory.CreateClient();
        await soloFactory.InitializeNodeAsync(displayName: "SoloBank", password: "SoloPass1!");
        (await soloClient.PostAsJsonAsync("/api/session/unlock", new { password = "SoloPass1!" })).EnsureSuccessStatusCode();

        // Even with a blind backup peer and a personal backup laptop, 1-person bank never offers accounts_home.
        using (var s = soloFactory.Services.CreateScope())
        {
            var wl = s.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var (pub1, _) = Ed25519Signer.GenerateKeyPair();
            var (pub2, _) = Ed25519Signer.GenerateKeyPair();
            await wl.CreateAsync(new WhitelistEntry
            {
                NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = pub1,
                Status = WhitelistStatuses.Active, Ownership = OwnershipKinds.Shared,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await wl.CreateAsync(new WhitelistEntry
            {
                NodeId = Guid.NewGuid(), DisplayName = "PersonalLaptop", Ed25519PublicKey = pub2,
                Status = WhitelistStatuses.Active, Ownership = OwnershipKinds.Personal,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }

        var statusResp = await SuperadminPost(soloClient, HttpMethod.Get, "/api/shared-doors/status");
        statusResp.EnsureSuccessStatusCode();
        var status = await statusResp.Content.ReadFromJsonAsync<SharedDoorsStatusResponse>();
        status!.ShouldOfferAccountsHome.Should().BeFalse();
    }

    [Fact]
    public async Task QuietRotation_OnSharedNode_ReSealsAllReplicatedDoors_AndPreservesSignInAndJoin()
    {
        var bobLoginBefore = await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "bob", password = BobPass });
        bobLoginBefore.StatusCode.Should().Be(HttpStatusCode.OK);
        var bobDto = await bobLoginBefore.Content.ReadFromJsonAsync<LoginResponse>();

        byte[] oldDek;
        using (var s = _sharedFactory.Services.CreateScope())
            oldDek = s.ServiceProvider.GetRequiredService<SessionService>().GetMasterDek();

        // Trigger a quiet DEK rotation on SharedBackup initiated with Alice's password (not Bob's).
        var adminLogin = await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = AlicePass });
        adminLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminDto = await adminLogin.Content.ReadFromJsonAsync<LoginResponse>();

        var propose = await SuperadminPost(_sharedClient, HttpMethod.Post, "/api/dek-rotation/propose",
            new { masterPassword = AlicePass }, userId: adminDto!.UserId);
        propose.StatusCode.Should().Be(HttpStatusCode.OK);
        var commitEventId = (await propose.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("commitEventId").GetGuid().ToString();

        var accept = await SuperadminPost(_sharedClient, HttpMethod.Post, "/api/dek-rotation/accept",
            new { commitEventId, masterPassword = AlicePass }, userId: adminDto.UserId);
        accept.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var progressResp = await SuperadminPost(_sharedClient, HttpMethod.Get, "/api/dek-rotation/progress", userId: adminDto.UserId);
            progressResp.EnsureSuccessStatusCode();
            var progress = await progressResp.Content.ReadFromJsonAsync<JsonElement>();
            var step = Enum.Parse<DekRotationFlowStep>(progress.GetProperty("currentStep").GetString()!);
            if (step == DekRotationFlowStep.Completed) break;
            step.Should().NotBe(DekRotationFlowStep.Failed, progress.ToString());
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("DEK rotation did not complete within 60s");
            await Task.Delay(200);
        }

        byte[] newDek;
        using (var s = _sharedFactory.Services.CreateScope())
            newDek = s.ServiceProvider.GetRequiredService<SessionService>().GetMasterDek();
        try
        {
            newDek.Should().NotEqual(oldDek);
            var newFp = DekFingerprint.Of(newDek);

            // Every replicated superadmin v2 door on SharedBackup — including Bob's whose password wasn't used — was re-sealed!
            using (var s = _sharedFactory.Services.CreateScope())
            {
                var users = await s.ServiceProvider.GetRequiredService<IUserRepository>().ListActiveAsync();
                var slots = await s.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync();
                var superadmins = users.Where(u => u.Role == UserRoles.Superadmin && u.Replicated).ToList();
                superadmins.Should().HaveCount(2);
                foreach (var sa in superadmins)
                {
                    var slot = slots.Single(k => k.SlotId == sa.KeySlotId);
                    slot.DoorVersion.Should().Be(Doors.V2);
                    slot.DekFingerprint.Should().Be(newFp);
                }

                // Device box published for primary_person_id's door (first listed superadmin on HomeNode).
                var connFactory = s.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
                using var c = connFactory.CreateConnection();
                var state = await s.ServiceProvider.GetRequiredService<ISharedDoorsStateRepository>().GetAsync();
                var primaryUser = superadmins.Single(u => u.PersonId == state.PrimaryPersonId);
                var primarySalt = slots.Single(k => k.SlotId == primaryUser.KeySlotId).Salt!;
                var boxSalt = await c.QueryFirstOrDefaultAsync<byte[]?>(
                    "SELECT salt FROM tbl_recovery_box WHERE kind = 'device' AND status = 'A' ORDER BY lamport_ts DESC LIMIT 1");
                boxSalt.Should().NotBeNull("RepublishForDoorsAsync must publish an active device box on the shared node");
                boxSalt.Should().Equal(primarySalt);
            }
        }
        finally
        {
            Array.Clear(oldDek);
            Array.Clear(newDek);
        }

        // Lock SharedBackup and verify BOTH admin and Bob still sign in (re-sealed doors work!) and /api/join still accepts Bob!
        _sharedFactory.Services.GetRequiredService<SessionService>().Lock();
        var bobLoginAfter = await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "bob", password = BobPass });
        bobLoginAfter.StatusCode.Should().Be(HttpStatusCode.OK);
        var bobAfterDto = await bobLoginAfter.Content.ReadFromJsonAsync<LoginResponse>();
        bobAfterDto!.UserId.Should().Be(bobDto!.UserId);
        bobAfterDto.IsUnlocked.Should().BeTrue();

        _sharedFactory.Services.GetRequiredService<SessionService>().Lock();
        var aliceLoginAfter = await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = AlicePass });
        aliceLoginAfter.StatusCode.Should().Be(HttpStatusCode.OK);
        (await aliceLoginAfter.Content.ReadFromJsonAsync<LoginResponse>())!.IsUnlocked.Should().BeTrue();

        var (joinPub, _) = Ed25519Signer.GenerateKeyPair();
        var joinAfter = await _sharedClient.PostAsJsonAsync("/api/join", new
        {
            nodeId = Guid.NewGuid(),
            displayName = "PostRotationJoiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(joinPub),
            masterPassword = BobPass,
        });
        joinAfter.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HttpSyncIngress_ValidSignedDoorsApplied_InvalidSignatureOrBlindEnvelopeRejected()
    {
        // Authenticate HomeNode as a real HTTP sync peer against SharedBackup (/api/sync/challenge + /api/sync/authenticate).
        NodeIdentity homeIdentity;
        byte[] homeSeed;
        using (var homeS = _homeFactory.Services.CreateScope())
        {
            homeIdentity = (await homeS.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            var dek = homeS.ServiceProvider.GetRequiredService<SessionService>().GetMasterDek();
            homeSeed = NodeIdentityVault.GetDecryptedPrivateKey(
                homeIdentity.Ed25519PrivateKey, homeIdentity.Ed25519PrivateKeyIV,
                homeIdentity.Ed25519PrivateKeyV, homeIdentity.NodeId, dek);
            Array.Clear(dek);
        }

        string bearerToken;
        try
        {
            var chalResp = await _sharedClient.PostAsync("/api/sync/challenge", null);
            chalResp.EnsureSuccessStatusCode();
            var chal = (await chalResp.Content.ReadFromJsonAsync<SyncChallengeResponse>())!;
            var chalBytes = Convert.FromBase64String(chal.Challenge);
            var tagged = "BMB-CHALLENGE-V2\0"u8.ToArray()
                .Concat(chal.ServerNodeId.ToByteArray())
                .Concat(chalBytes)
                .ToArray();
            var sig = Ed25519Signer.Sign(homeSeed, tagged);
            var authResp = await _sharedClient.PostAsJsonAsync("/api/sync/authenticate", new SyncAuthRequest(
                homeIdentity.NodeId, chal.Challenge, Convert.ToBase64String(sig),
                SyncProtocolVersion.Current, MetadataCapabilities.Advertised));
            authResp.EnsureSuccessStatusCode();
            bearerToken = (await authResp.Content.ReadFromJsonAsync<SyncAuthResponse>())!.Token;

            // Create a new superadmin Carol on HomeNode -> publishes a new real superadmin_doors_set event.
            SyncEvent realDoorsEvt;
            using (var homeS = _homeFactory.Services.CreateScope())
            {
                await homeS.ServiceProvider.GetRequiredService<UserService>()
                    .CreateUserAsync("carol", "Carol", "CarolHome3!", UserRoles.Superadmin);
                realDoorsEvt = (await homeS.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAllAfterSequenceAsync(0, 200))
                    .Where(e => e.EventType == EventTypes.SuperadminDoorsSet)
                    .OrderByDescending(e => e.LamportTs)
                    .First();
            }

            async Task<SyncApplyResult> PushAsync(SyncEvent evt)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/api/sync/events");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                req.Content = JsonContent.Create(new[] { evt });
                var resp = await _sharedClient.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                return (await resp.Content.ReadFromJsonAsync<SyncApplyResult>())!;
            }

            // 1. Tampered Ed25519 signature rejected at HTTP sync ingress.
            var badSig = (byte[])realDoorsEvt.Signature.Clone();
            badSig[0] ^= 0xFF;
            var badSigEvt = new SyncEvent
            {
                EventId = Guid.NewGuid(),
                NodeId = realDoorsEvt.NodeId,
                LamportTs = realDoorsEvt.LamportTs + 50,
                EventType = realDoorsEvt.EventType,
                Payload = realDoorsEvt.Payload,
                Signature = badSig,
                ProtocolVersion = realDoorsEvt.ProtocolVersion,
                CreatedAt = realDoorsEvt.CreatedAt,
            };
            (await PushAsync(badSigEvt)).Skipped.Should().Be(1);

            // 2. Blind recipient in list_envelopes.peers (even with valid HomeNode signature) rejected by EventInvariants at HTTP ingress.
            var origPayload = JsonSerializer.Deserialize<SuperadminDoorsSetPayload>(realDoorsEvt.Payload)!;
            var blindPayload = origPayload with
            {
                ListEnvelopes = origPayload.ListEnvelopes with
                {
                    Peers = new Dictionary<string, DekEnvelopeBox>(origPayload.ListEnvelopes.Peers)
                    {
                        [BlindNodeId.NewId().ToString("D").ToUpperInvariant()] = new DekEnvelopeBox("AA==", "AA==")
                    }
                }
            };
            var blindEvt = new SyncEvent
            {
                EventId = Guid.NewGuid(),
                NodeId = realDoorsEvt.NodeId,
                LamportTs = realDoorsEvt.LamportTs + 51,
                EventType = EventTypes.SuperadminDoorsSet,
                Payload = JsonSerializer.Serialize(blindPayload),
                Signature = [],
                ProtocolVersion = realDoorsEvt.ProtocolVersion,
                CreatedAt = DateTime.UtcNow,
            };
            blindEvt.Signature = Ed25519Signer.Sign(homeSeed, EventSignature.BuildPayload(blindEvt));
            (await PushAsync(blindEvt)).Skipped.Should().Be(1);

            // 3. Valid signed superadmin_doors_set accepted at HTTP ingress -> Carol can immediately sign in on SharedBackup!
            var okRes = await PushAsync(realDoorsEvt);
            okRes.Applied.Should().Be(1);
            okRes.Skipped.Should().Be(0);
            (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "carol", password = "CarolHome3!" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            Array.Clear(homeSeed);
        }
    }
}
