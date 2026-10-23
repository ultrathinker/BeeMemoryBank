using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-305, the third state of the node: its database is encrypted. There is nothing left to switch on, so the Admin page and the encryption page are the node's
/// storage protection - the status and, on a server, whether it starts unlocked after a restart - and no "Encrypt this node" is offered (it is one page since 2.7.2:
/// the status, the encryption and the switch of a server are on <c>/Admin/StorageProtection</c>). Through the real Web
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
    public async Task The_admin_page_of_an_encrypted_node_is_its_storage_protection_and_offers_no_encryption()
    {
        (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/vault/status")).GetProperty("mode").GetString().Should().Be("sealed");

        var admin = await GetAsync("/Admin/StorageProtection");

        var body = admin[admin.IndexOf("id=\"admin-page-body\"", StringComparison.Ordinal)..];
        admin.Should().Contain("<title>Database encryption", "the page keeps the name of the index item in both states of the node");
        body.Should().Contain("This node's database is encrypted").And.NotContain("href=\"/VaultEncryption\"");
        body.Should().NotContain("a plain file").And.NotContain("Encrypt this node");
        admin.Should().NotContain("Encrypt this node");
        (await GetAsync("/Admin")).Should().NotContain("Encrypt this node", "the index only names the page");
    }

    [Fact]
    public async Task The_page_of_an_encrypted_node_is_still_called_Database_encryption_and_keeps_the_status_and_the_start_unlocked_switch()
    {
        var page = await GetAsync("/Admin/StorageProtection");

        page.Should().Contain("<title>Database encryption");
        Regex.Match(page, "<h1[^>]*>.*?</h1>", RegexOptions.Singleline).Value.Should().Contain("Database encryption").And.NotContain("Storage protection");
        (await GetAsync("/Admin")).Should().Contain(">Database encryption<", "the index item and the title of the page say the same");
        page.Should().Contain("id=\"vault-sealed-card\"").And.Contain(">encrypted<").And.Contain("This node's database is encrypted");
        page.Should().Contain("Start unlocked after a restart", "the permanent switch of a server stays");
        page.Should().Contain("id=\"server-auto-unlock-card\"");
        page.Should().NotContain("Encrypt this node").And.NotContain("not encrypted").And.NotContain("vault-plain-card").And.NotContain("seal-form");
    }

    [Fact]
    public async Task A_refusal_of_the_start_unlocked_switch_ends_at_its_page_and_says_why_under_the_title()
    {
        var token = Regex.Match(await GetAsync("/Admin/StorageProtection"), "__RequestVerificationToken[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var page = await _admin.GetAsync("/Admin/StorageProtection");
        var cookies = string.Join("; ", _admin.DefaultRequestHeaders.GetValues("Cookie"))
            + "; " + string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var body = new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["password"] = "not the password", ["confirmation"] = "START UNLOCKED" };
        using var post = new HttpRequestMessage(HttpMethod.Post, "/Admin/StorageProtection?handler=EnableServerAutoUnlock") { Content = new FormUrlEncodedContent(body) };
        post.Headers.TryAddWithoutValidation("Cookie", cookies);

        using var response = await _admin.SendAsync(post);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = WebUtility.UrlDecode(response.Headers.Location!.OriginalString);
        location.Should().StartWith("/Admin/StorageProtection?").And.Contain("err=");
        var followed = await GetAsync(location);
        var error = Regex.Match(location, "[?&]err=([^&#]*)").Groups[1].Value;
        var message = Regex.Match(followed, "<div id=\"admin-page-message\"[^>]*>(.*?)</div>", RegexOptions.Singleline).Groups[1].Value;
        message.Should().Contain(error, "the reason is under the title, where the Admin pages say what came of an action");
        followed.Should().Contain("id=\"server-auto-unlock-card\"", "and the switch it was about is on the same page");
    }
}
