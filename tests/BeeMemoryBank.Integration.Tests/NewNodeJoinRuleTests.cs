using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-306, the owner's rule for a node that is set up by joining: it joins ONLY a node that encrypts its metadata (protocol 4) - with a clear refusal
/// and no workaround - and it ends encrypted under the password it was given. The tests run the product's own Api as the joiner (the Setup page calls
/// <c>POST /api/init/join</c>) over an empty folder, against a real host in the same process, and judge the joiner's disk and what a restart shows.
/// </summary>
[Collection(ProcessWideRateLimiterCollection.Name)]
public sealed class NewNodeJoinRuleTests
{
    private const string Password = StepANode.Password;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_join_rule_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Head(string path, int count = 16)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static Task<HttpResponseMessage> JoinAsync(HttpClient client, string displayName) =>
        client.PostAsJsonAsync("/api/init/join", new { adminUsername = "admin", displayName, remoteUrl = "http://host", password = Password }, JsonOpts);

    [Fact]
    public async Task A_new_node_does_not_join_a_node_that_does_not_encrypt_its_metadata_and_nothing_is_written_or_kept()
    {
        RateLimitMiddleware.ResetForTests();
        using var host = await StepANode.CreateAsync("PlainHost");   // initialized and unlocked, never activated: it declares protocol 3
        await StepANode.SeedWithCanariesAsync(host);
        host.Services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeFalse("the control: this host is on protocol 3");

        using var joiner = SealedApiNode.PlainOver(NewFolder());
        joiner.Factory.RouteOutboundHttpThrough(host.Server.CreateHandler());
        using var client = joiner.Client();

        var response = await JoinAsync(client, "Refused joiner");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("error").GetString();
        error.Should().Contain("Cannot join: the other computer does not encrypt its metadata yet").And.Contain("protocol 3")
            .And.Contain("Switch metadata encryption on there first").And.NotContain(Password);
        using (var scope = joiner.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync()).Should().BeFalse("no node was made");
        VaultStoreFiles.DetectMode(joiner.Dir).Should().NotBe(VaultStoreMode.Sealed, "nothing was encrypted either: there was nothing to encrypt");
        Directory.EnumerateFiles(joiner.Dir, "*", SearchOption.AllDirectories)
            .Where(f => f.Contains("work-", StringComparison.Ordinal) || f.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || f.Contains(".seal-", StringComparison.Ordinal))
            .Should().BeEmpty("no archive was fetched and no migration ran");

        // The host was told, so it does not keep a never-synced peer that nobody holds the key of.
        using var hostScope = host.Services.CreateScope();
        var rows = await hostScope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        rows.Should().NotContain(r => r.DisplayName == "Refused joiner", "the abandoned join's row on the host was taken back");
    }

    [Fact]
    public async Task A_new_node_that_joins_a_node_on_protocol_4_ends_encrypted_and_opens_with_the_password_it_was_given()
    {
        RateLimitMiddleware.ResetForTests();
        using var host = await StepANode.CreateAsync("ActivatedHost");
        var vault = await StepANode.SeedWithCanariesAsync(host);
        await StepANode.ActivateAsync(host);

        using var joiner = SealedApiNode.PlainOver(NewFolder());
        joiner.Factory.RouteOutboundHttpThrough(host.Server.CreateHandler());
        using (var client = joiner.Client())
        {
            var response = await JoinAsync(client, "Joiner");
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        // The disk: an encrypted database, a boot store, no plain file, no migration left over.
        VaultStoreFiles.DetectMode(joiner.Dir).Should().Be(VaultStoreMode.Sealed);
        File.Exists(Path.Combine(joiner.Dir, VaultStoreFiles.Boot)).Should().BeTrue("the boot store is there");
        Head(Path.Combine(joiner.Dir, VaultStoreFiles.Database)).Should().NotEqual("SQLite format 3\0"u8.ToArray(), "the database is not a plain SQLite file");
        Directory.EnumerateFiles(joiner.Dir, "*.seal-*", SearchOption.AllDirectories).Should().BeEmpty("no replaced plain file is left");
        File.Exists(Path.Combine(joiner.Dir, VaultStoreFiles.SealJournal)).Should().BeFalse("the migration is over");
        joiner.Session.IsUnlocked.Should().BeFalse("the join does not leave the key in memory: the first sign-in opens the node");

        // After a restart it is locked, auto-unlock is off, and the password it was given opens it with the host's notes and the activation.
        joiner.Restart();
        using var after = joiner.Client();
        joiner.Session.IsUnlocked.Should().BeFalse();
        (await joiner.LoginAsync(after, "not the password")).StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await joiner.LoginAsync(after, Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SealedApiNode.GetJsonAsync(after, "/api/vault/status")).GetProperty("mode").GetString().Should().Be("sealed");
        (await SealedApiNode.GetJsonAsync(after, "/api/sync/identity")).GetProperty("protocolVersion").GetInt32().Should().Be(EventClasses.SealedProtocol);
        using var scope = joiner.Services.CreateScope();
        var articles = await scope.ServiceProvider.GetRequiredService<IArticleRepository>().GetByIdsAsync(vault.ArticleIds);
        articles.Select(a => a.Id).Should().BeEquivalentTo(vault.ArticleIds, "the host's notes came with the join");
    }
}
