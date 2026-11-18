using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Users page (release 2.12, F5), as the server draws it: the Edit dialog has no role choice across the superadmin line, a person who is not a superadmin gets
/// "Make superadmin..." and one who is gets "Remove superadmin rights...", and both actions have a dialog that says in plain words what they give or take away. Through
/// the real Web host over the real Api, signed in as a superadmin like the browser does. What the dialogs DO is <see cref="UsersPageScriptTests"/>; what the Api does with
/// what they send is <see cref="DemotionEndpointTests"/>.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class UsersPageSuperadminActionsTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPage12345";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _machine = null!;
    private HttpClient _admin = null!;
    private int _carolId;
    private int _bobId;

    public async Task InitializeAsync()
    {
        _machine = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "PageNode", password: AdminPassword);
        (await _machine.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        _carolId = await CreateUserAsync("carol", "superadmin");
        _bobId = await CreateUserAsync("bob", "user");
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

    private async Task<int> CreateUserAsync(string name, string role)
    {
        var created = await _machine.PostAsJsonAsync("/api/users", new { username = name, password = name + "Page12345", displayName = name, role, passwordHint = "the colour of the sea" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<string> UsersPageAsync()
    {
        using var response = await _admin.GetAsync("/Users");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>The markup of the table row of one user (from its <c>&lt;tr&gt;</c> to the next).</summary>
    private static string RowOf(string html, string username)
    {
        var match = Regex.Match(html, @"<tr>(?:(?!</tr>).)*<strong>" + Regex.Escape(username) + @"</strong>.*?</tr>", RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the table has a row for {username}");
        return match.Value;
    }

    private static string DialogOf(string html, string id)
    {
        var match = Regex.Match(html, @"<sl-dialog id=""" + id + @""".*?</sl-dialog>", RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the page has the dialog {id}");
        return match.Value;
    }

    [Fact]
    public async Task APersonWhoIsNotASuperadminGetsMakeSuperadmin_AndASuperadminGetsRemoveRights()
    {
        var html = await UsersPageAsync();

        var bob = RowOf(html, "bob");
        bob.Should().Contain("btn-user-make-superadmin").And.Contain("Make superadmin...").And.NotContain("btn-user-remove-superadmin");
        bob.Should().Contain($"data-uid=\"{_bobId}\"");

        var carol = RowOf(html, "carol");
        carol.Should().Contain("btn-user-remove-superadmin").And.Contain("Remove superadmin rights...").And.NotContain("btn-user-make-superadmin");
        carol.Should().Contain($"data-uid=\"{_carolId}\"");
        Regex.Match(carol, @"<sl-icon-button[^>]*btn-user-remove-superadmin[^>]*>").Value.Should().NotContain("disabled", "another superadmin may be the one who takes it away");
    }

    [Fact]
    public async Task NobodyRemovesTheirOwnRights_AndTheLastSuperadminCannotLoseThem()
    {
        var html = await UsersPageAsync();   // signed in as admin, who is one of two superadmins

        var admin = RowOf(html, "admin");
        Regex.Match(admin, @"<sl-icon-button[^>]*btn-user-remove-superadmin[^>]*>").Value.Should().Contain("disabled");
        admin.Should().Contain("You cannot remove your own superadmin rights");

        // The deletion primitive (the API reaches it for a superadmin only through the removal process, 2.12): admin is then the last superadmin.
        using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(_api.Services))
            await Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<BeeMemoryBank.Core.Services.UserService>(scope.ServiceProvider).DeleteUserAsync(_carolId);
        var alone = RowOf(await UsersPageAsync(), "admin");
        alone.Should().Contain("disabled");
    }

    [Fact]
    public async Task TheEditDialogHasNoRoleChoiceAcrossTheSuperadminLine()
    {
        var edit = DialogOf(await UsersPageAsync(), "dlg-edit-user");

        Regex.Match(edit, @"<sl-select id=""edit-role"".*?</sl-select>", RegexOptions.Singleline).Value.Should().NotContain("value=\"superadmin\"", "the choice of the Edit dialog never holds superadmin");
        edit.Should().Contain("id=\"edit-role-fixed\"", "a superadmin's role is shown, not chosen").And.Contain("shield-minus").And.Contain("asks what they may see afterwards");
        edit.Should().Contain("use the shield button of their row", "and the way to a promotion is named");
    }

    [Fact]
    public async Task MakeSuperadminSaysInPlainWordsWhatThePersonGets_AndThatTakingItBackIsAProcess()
    {
        var dialog = DialogOf(await UsersPageAsync(), "dlg-make-superadmin");

        dialog.Should().Contain("the key to all data");
        dialog.Should().Contain("see every folder");
        dialog.Should().Contain("full copies of the bank");
        dialog.Should().Contain("multi-step removal");
        dialog.Should().Contain("not one click");
        dialog.Should().Contain("btn-make-sa-confirm").And.Contain("data-dlg-cancel=\"dlg-make-superadmin\"", "it can be cancelled");
        dialog.Should().Contain("make-superadmin-error", "a refusal is shown in the dialog, where the button is");
    }

    [Fact]
    public async Task RemoveSuperadminRightsHasAWarningMark_ExplainsWhatHappens_AndStartsWithTheConfirmButtonOff()
    {
        var dialog = DialogOf(await UsersPageAsync(), "dlg-remove-superadmin");

        dialog.Should().Contain("exclamation-triangle", "a warning mark");
        dialog.Should().Contain("Taken away now").And.Contain("their password").And.Contain("recovery keys").And.Contain("remote access tokens").And.Contain("AI agents")
            .And.Contain("personally theirs");
        dialog.Should().Contain("cannot be cancelled").And.Contain("/Admin/Offboarding").And.Contain("master key is changed").And.Contain("<em>being removed</em>");
        dialog.Should().Contain("remove-sa-password", "the removal starts only with the administrator's own password");
        Regex.Match(dialog, @"<sl-radio-group id=""remove-sa-end""[^>]*>").Value.Should().NotContain("value=", "nothing is chosen for the administrator: deleted or an ordinary user");
        Regex.Match(dialog, @"<sl-button id=""btn-remove-sa-confirm""[^>]*>").Value.Should().Contain("disabled", "it cannot be confirmed before the choice is made");
        dialog.Should().Contain("Only these folders").And.Contain("A role that hides folders by default").And.Contain("open <em>User</em> role");
        // No choice is preselected: the radio group has no value.
        Regex.Match(dialog, @"<sl-radio-group id=""remove-sa-choice""[^>]*>").Value.Should().NotContain("value=");
        dialog.Should().Contain("remove-superadmin-error", "a refusal is shown in the dialog, where the button is");
    }

    [Fact]
    public async Task ThePageDoesNotSendTheRoleOfASuperadminAnywhereByItself()
    {
        // The Edit dialog's own markup has no superadmin option and the dialogs are filled by the script: the server-drawn page offers superadmin only in the Add dialog.
        var html = await UsersPageAsync();

        Regex.Matches(html, @"<sl-option value=""superadmin"">").Count.Should().Be(1, "one place offers it, the Add dialog, which says what it is");
        DialogOf(html, "dlg-add-user").Should().Contain("add-superadmin-warning");
    }

    [Fact]
    public async Task ThroughTheWebProxy_ADemotionWithoutAChoiceIsRefused_AndWithOneIsDone()
    {
        // What the dialog sends, through the same proxy route the script uses (the browser's cookie, the Web layer's forwarding, the Api's own rule).
        var noPassword = await SendAsync(new { displayName = "carol", role = "user", allowOpenRole = true });
        noPassword.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await noPassword.Content.ReadAsStringAsync()).Should().Contain("Removing a superadmin is a process");

        var refused = await SendAsync(new { displayName = "carol", role = "user", adminPassword = AdminPassword });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("Say which folders");

        var started = await SendAsync(new { displayName = "carol", role = "user", allowOpenRole = true, adminPassword = AdminPassword });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var row = RowOf(await UsersPageAsync(), "carol");
        row.Should().Contain("being removed, step", "never 'user' while a way back is open (decision 4)");
        row.Should().Contain("/Admin/Offboarding?id=").And.NotContain("btn-user-remove-superadmin", "only the removal changes the account now");
    }

    private async Task<HttpResponseMessage> SendAsync(object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api-proxy/users/{_carolId}") { Content = JsonContent.Create(body) };
        return await _admin.SendAsync(request);
    }
}
