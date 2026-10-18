using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-306, the owner's rule for a fresh installation: whatever sets a node up ends with an ENCRYPTED database under the master password it was given
/// and with metadata encryption already on (protocol 4), and the server does not start unlocked by itself. The tests run the product's own Api over an
/// empty folder, set it up through <c>POST /api/init/standalone</c> (what the Web Setup, Docker and a script call), and judge the disk and the node
/// after a restart, not what the endpoint says.
/// </summary>
public sealed class FreshNodeSealedSetupTests
{
    private const string Password = "Fresh-setup-pw-1";

    private static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_fresh_setup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static object Body(string password = Password) => new { adminUsername = "admin", displayName = "Fresh node", password };

    private static byte[] Head(string path, int count = 16)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"{url} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task A_standalone_setup_leaves_an_encrypted_database_a_boot_store_and_no_plain_file()
    {
        using var node = SealedApiNode.PlainOver(NewFolder());
        using var client = node.Client();

        var response = await client.PostAsJsonAsync("/api/init/standalone", Body());

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        VaultStoreFiles.DetectMode(node.Dir).Should().Be(VaultStoreMode.Sealed, "the folder is sealed");
        File.Exists(Path.Combine(node.Dir, VaultStoreFiles.Boot)).Should().BeTrue("the boot store is there");
        Head(Path.Combine(node.Dir, VaultStoreFiles.Database)).Should().NotEqual("SQLite format 3\0"u8.ToArray(), "the database file is not a plain SQLite file");
        if (File.Exists(Path.Combine(node.Dir, VaultStoreFiles.Chat)))
            Head(Path.Combine(node.Dir, VaultStoreFiles.Chat)).Should().NotEqual("SQLite format 3\0"u8.ToArray());
        Directory.EnumerateFiles(node.Dir, "*.seal-*", SearchOption.AllDirectories).Should().BeEmpty("no replaced plain file is left");
        File.Exists(Path.Combine(node.Dir, VaultStoreFiles.SealJournal)).Should().BeFalse("the migration is over");
        node.Gate.IsSealed.Should().BeTrue();

        var status = await GetAsync(client, "/api/vault/status");
        status.GetProperty("mode").GetString().Should().Be("sealed");
    }

    [Fact]
    public async Task A_standalone_setup_switches_metadata_encryption_on_for_the_new_node()
    {
        using var node = SealedApiNode.PlainOver(NewFolder());
        using var client = node.Client();
        (await client.PostAsJsonAsync("/api/init/standalone", Body())).StatusCode.Should().Be(HttpStatusCode.OK);

        node.Services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeTrue("the node declares protocol 4 from its first start");

        // After a restart the node is locked; the owner signs in with the setup password, and the readiness report says it is on.
        node.Restart();
        using var after = node.Client();
        (await node.LoginAsync(after, Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        var readiness = await GetAsync(after, "/api/sync/readiness?peers=false");
        readiness.GetProperty("activated").GetBoolean().Should().BeTrue("/api/sync/readiness says activated");
        var identity = await GetAsync(after, "/api/sync/identity");
        identity.GetProperty("protocolVersion").GetInt32().Should().Be(EventClasses.SealedProtocol, "/api/sync/identity says protocol 4");
    }

    [Fact]
    public async Task The_node_is_locked_after_the_setup_and_after_a_restart_until_the_password_is_given_and_does_not_start_unlocked_by_itself()
    {
        using var node = SealedApiNode.PlainOver(NewFolder());
        using var client = node.Client();
        (await client.PostAsJsonAsync("/api/init/standalone", Body())).StatusCode.Should().Be(HttpStatusCode.OK);
        node.Session.IsUnlocked.Should().BeFalse("the setup does not leave the key in memory");

        node.Restart();

        node.Session.IsUnlocked.Should().BeFalse("auto-unlock is off by default: a restart leaves the server locked");
        using var after = node.Client();
        var status = await GetAsync(after, "/api/vault/status");
        status.GetProperty("state").GetString().Should().Be("Closed");
        status.GetProperty("auto_unlock").GetProperty("server").GetBoolean().Should().BeFalse("the server's \"start unlocked after a restart\" is off");
        status.GetProperty("auto_unlock").GetProperty("os").GetBoolean().Should().BeFalse();
        File.Exists(Path.Combine(node.Dir, "server-auto-unlock.enabled")).Should().BeFalse();
        (await node.LoginAsync(after, "not the setup password")).StatusCode.Should().NotBe(HttpStatusCode.OK, "a wrong password opens nothing");
        (await node.LoginAsync(after, Password)).StatusCode.Should().Be(HttpStatusCode.OK, "the password of the setup opens it");
        node.Session.IsUnlocked.Should().BeTrue();
    }

    [Fact]
    public async Task What_the_setup_wrote_is_there_after_the_encryption_and_the_unlock()
    {
        using var node = SealedApiNode.PlainOver(NewFolder());
        using var client = node.Client();
        (await client.PostAsJsonAsync("/api/init/standalone", Body())).StatusCode.Should().Be(HttpStatusCode.OK);
        node.Restart();
        (await node.Session.UnlockAsync(Password)).Should().BeTrue();

        using var scope = node.Services.CreateScope();
        var identity = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
        identity.Should().NotBeNull();
        identity!.DisplayName.Should().Be("Fresh node");
        (await scope.ServiceProvider.GetRequiredService<IUserRepository>().ListActiveAsync()).Should().ContainSingle(u => u.Username == "admin");
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetSentinelAsync()).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_setup_that_cannot_encrypt_the_node_is_taken_back_and_says_so()
    {
        // The migration refuses to start (a blocker the host adds): the node must not stay behind as an initialized node in the open.
        using var node = SealedApiNode.PlainOver(NewFolder(), services =>
        {
            var original = services.Single(d => d.ServiceType == typeof(ISealMigrationHost)).ImplementationFactory!;
            services.Replace(ServiceDescriptor.Singleton<ISealMigrationHost>(sp => new RefusingHost((ISealMigrationHost)original(sp))));
        });
        using var client = node.Client();

        var response = await client.PostAsJsonAsync("/api/init/standalone", Body());

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        error.Should().Contain("could not be encrypted").And.Contain("run the setup again");
        using var scope = node.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync())
            .Should().BeFalse("the half-made node was taken back: nothing of the failed setup is left to run in the open");
        VaultStoreFiles.DetectMode(node.Dir).Should().NotBe(VaultStoreMode.Sealed);
        File.Exists(Path.Combine(node.Dir, VaultStoreFiles.Boot)).Should().BeFalse();
    }

    [Fact]
    public async Task A_second_setup_over_the_encrypted_node_is_refused()
    {
        using var node = SealedApiNode.PlainOver(NewFolder());
        using var client = node.Client();
        (await client.PostAsJsonAsync("/api/init/standalone", Body())).StatusCode.Should().Be(HttpStatusCode.OK);

        using var again = await client.PostAsJsonAsync("/api/init/standalone", Body("Another-password-2"));

        again.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable);
    }

    private sealed class RefusingHost(ISealMigrationHost inner) : ISealMigrationHost
    {
        public Task PrepareVaultAsync(CancellationToken ct) => inner.PrepareVaultAsync(ct);

        public Task<IReadOnlyList<SealBlocker>> HostBlockersAsync(SealOptions options, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SealBlocker>>([new SealBlocker("test_refusal", "The test host refuses to encrypt.")]);

        public Task<IReadOnlyList<SealWarning>> HostWarningsAsync(CancellationToken ct) => inner.HostWarningsAsync(ct);
    }
}
