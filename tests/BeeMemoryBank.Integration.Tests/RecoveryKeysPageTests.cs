using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Admin "Recovery keys" page (release 2.12), as the server draws it and through the real Web host over the real Api: the list names each
/// key's owner (or "owner unknown" for a pre-2.12 key), revoking one key or all ownerless keys works from the page, and revoking another
/// person's key asks for a confirmation that names them.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class RecoveryKeysPageTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPage12345";
    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _machine = null!;
    private HttpClient _admin = null!;
    private int _adminId;

    public async Task InitializeAsync()
    {
        _machine = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "PageNode", password: AdminPassword);
        (await _machine.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        using var scope = _api.Services.CreateScope();
        _adminId = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.Id;
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _admin = await WebLogin.LoginAsync(_web, "admin", AdminPassword);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _machine.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    private async Task<int> IssueKeyAsync(int ownerId, string? label = null)
    {
        using var issuer = _api.CreateClient();
        issuer.DefaultRequestHeaders.Add("X-User-Id", ownerId.ToString());
        var resp = await issuer.PostAsJsonAsync("/api/keys/add-recovery", new { label });
        resp.EnsureSuccessStatusCode();
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync())
            .Where(s => s.SlotType == "recovery").OrderByDescending(s => s.SlotId).First().SlotId;
    }

    private async Task<string> PageAsync()
    {
        using var response = await _admin.GetAsync("/Admin/RecoveryKey");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> PostHandlerAsync(string handler, params (string Key, string Value)[] fields)
    {
        var html = await PageAsync();
        var token = Token.Match(html).Groups[1].Value;
        token.Should().NotBeNullOrEmpty();
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };
        foreach (var (k, v) in fields) form[k] = v;
        return await _admin.PostAsync($"/Admin/RecoveryKey?handler={handler}", new FormUrlEncodedContent(form));
    }

    [Fact]
    public async Task ThePage_ListsAKeysOwnerAndLabel_AndSaysAKeyIsPersonal()
    {
        await IssueKeyAsync(_adminId, "the safe card");

        var html = await PageAsync();

        html.Should().Contain("the safe card");
        html.Should().Contain("admin", "the owner's name is listed");
        html.Should().Contain("personal", "the About text says a recovery key is personal");
        html.Should().Contain("handler=RevokeKey", "each key has a revoke action");
        html.Should().Contain("issue-recovery-key-btn", "the issue button is kept");
    }

    [Fact]
    public async Task RevokingAKeyFromThePage_RemovesIt_AndSaysSo()
    {
        var slotId = await IssueKeyAsync(_adminId);

        var response = await PostHandlerAsync("RevokeKey", ("slotId", slotId.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("msg=");
        // Follow the redirect: the message of the action travels in its address and is drawn where the button was.
        using var followed = await _admin.GetAsync(response.Headers.Location!.ToString());
        var html = WebUtility.HtmlDecode(await followed.Content.ReadAsStringAsync());
        html.Should().NotContain($"value=\"{slotId}\"", "the revoked key is no longer listed");
        html.Should().Contain("Recovery key revoked");
    }

    [Fact]
    public async Task APre212Key_ShowsOwnerUnknown_AndTheRevokeAllUnknownButtonAppears()
    {
        await IssueKeyAsync(_adminId);
        await AddOwnerlessKeyAsync();

        var html = await PageAsync();

        html.Should().Contain("Owner unknown");
        html.Should().Contain("issued before 2.12");
        html.Should().Contain("handler=RevokeUnknownKeys");

        var response = await PostHandlerAsync("RevokeUnknownKeys");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var after = await PageAsync();
        after.Should().NotContain("Owner unknown", "the ownerless key is gone");
        after.Should().NotContain("handler=RevokeUnknownKeys", "with no ownerless keys left the button is gone");
    }

    [Fact]
    public async Task RevokingAnotherPersonsKey_AsksForAConfirmationNamingThem()
    {
        var carolId = await CreateSuperadminAsync("carol");
        await IssueKeyAsync(carolId);

        var html = await PageAsync();

        // carol's row carries a confirmation that names her, not the signed-in admin's own simple confirm.
        var row = Regex.Match(html, @"<tr>(?:(?!</tr>).)*carol.*?</tr>", RegexOptions.Singleline).Value;
        row.Should().NotBeEmpty();
        row.Should().Contain("data-name=\"carol\"");
        row.Should().Contain("{name}", "the confirmation pattern names the person");
    }

    private async Task<int> CreateSuperadminAsync(string name)
    {
        var created = await _machine.PostAsJsonAsync("/api/users", new { username = name, password = name + "Page12345", displayName = name, role = "superadmin", passwordHint = "the colour of the sea" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task AddOwnerlessKeyAsync()
    {
        using var scope = _api.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(Convert.ToBase64String(SecureRandom.GetBytes(32)), salt);
        var dek = session.GetMasterDek();
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().CreateAsync(new MasterKeyStore
            {
                SlotType = "recovery", EncryptedMasterDek = wrapped, IV = iv, Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory, ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow, DoorVersion = 1, OwnerUserId = null,
            });
        }
        finally { Array.Clear(kek); Array.Clear(dek); }
    }
}
