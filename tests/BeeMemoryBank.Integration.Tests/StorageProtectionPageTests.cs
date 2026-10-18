using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-305, the third state of the node: its database is encrypted. There is nothing left to switch on, so the Admin card and its page are the node's
/// storage protection - the status and, on a server, whether it starts unlocked after a restart - and no "Encrypt this node" is offered. Through the real Web
/// host in front of a real Api over a sealed data folder (the open node and the node with metadata encryption on are in <see cref="AdminCardsTests"/>).
/// </summary>
public sealed class StorageProtectionPageTests(SealedNodeTemplate template) : IClassFixture<SealedNodeTemplate>, IAsyncLifetime
{
    private SealedApiNode _node = null!;
    private BmbWebHostFactory _web = null!;
    private HttpClient _apiClient = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _node = await SealedApiNode.StartAsync(template);
        _apiClient = _node.Client();
        (await _node.LoginAsync(_apiClient, SealedNodeTemplate.Password)).EnsureSuccessStatusCode();   // opens the vault
        _web = new BmbWebHostFactory();
        _web.RouteOutboundHttpThrough(_node.Factory.Server.CreateHandler());
        _admin = await WebLogin.LoginAsync(_web, "admin", SealedNodeTemplate.Password);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _apiClient.Dispose();
        ((IDisposable)_web).Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> GetAsync(string url)
    {
        using var response = await _admin.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_admin_card_of_an_encrypted_node_is_its_storage_protection_and_offers_no_encryption()
    {
        (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/vault/status")).GetProperty("mode").GetString().Should().Be("sealed");

        var admin = await GetAsync("/Admin");

        var card = Regex.Match(admin, "<h3 id=\"card-database-encryption\".*?</div>", RegexOptions.Singleline).Value;
        card.Should().Contain("Storage protection").And.Contain("This node's database is encrypted.").And.Contain("href=\"/VaultEncryption\"");
        card.Should().NotContain("Database encryption").And.NotContain("a plain file").And.NotContain("Encrypt this node");
        admin.Should().NotContain("Encrypt this node");
    }

    [Fact]
    public async Task The_page_of_an_encrypted_node_is_called_Storage_protection_and_keeps_the_status_and_the_start_unlocked_switch()
    {
        var page = await GetAsync("/VaultEncryption");

        page.Should().Contain("<title>Storage protection");
        Regex.Match(page, "<h2[^>]*>.*?</h2>", RegexOptions.Singleline).Value.Should().Contain("Storage protection").And.NotContain("Database encryption");
        page.Should().Contain("id=\"vault-sealed-card\"").And.Contain(">encrypted<").And.Contain("This node's database is encrypted");
        page.Should().Contain("Start unlocked after a restart", "the permanent switch of a server stays");
        page.Should().Contain("id=\"server-auto-unlock-card\"");
        page.Should().NotContain("Encrypt this node").And.NotContain("not encrypted").And.NotContain("vault-plain-card").And.NotContain("seal-form");
    }

    [Fact]
    public async Task A_refusal_of_the_start_unlocked_switch_ends_at_its_card_and_says_why_there()
    {
        var token = Regex.Match(await GetAsync("/VaultEncryption"), "__RequestVerificationToken[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var page = await _admin.GetAsync("/VaultEncryption");
        var cookies = string.Join("; ", _admin.DefaultRequestHeaders.GetValues("Cookie"))
            + "; " + string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var body = new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["password"] = "not the password", ["confirmation"] = "START UNLOCKED" };
        using var post = new HttpRequestMessage(HttpMethod.Post, "/VaultEncryption?handler=EnableServerAutoUnlock") { Content = new FormUrlEncodedContent(body) };
        post.Headers.TryAddWithoutValidation("Cookie", cookies);

        using var response = await _admin.SendAsync(post);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = WebUtility.UrlDecode(response.Headers.Location!.OriginalString);
        location.Should().EndWith("#card-auto-unlock").And.Contain("card=card-auto-unlock").And.Contain("err=");
        var followed = await GetAsync(location);
        var section = followed[followed.IndexOf("id=\"card-auto-unlock\"", StringComparison.Ordinal)..];
        var error = Regex.Match(location, "[?&]err=([^&#]*)").Groups[1].Value;
        section[..section.IndexOf("id=\"server-auto-unlock-card\"", StringComparison.Ordinal)].Should().Contain(error, "the reason is in the section of the switch");
        followed[..followed.IndexOf("id=\"card-database-encryption\"", StringComparison.Ordinal)].Should().NotContain(error, "and not at the top of the page");
    }
}
