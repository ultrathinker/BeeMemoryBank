using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-305 / BMB-303, through the real Web host in front of a real Api, with the cookies of a browser (the TempData of the page travels in one). The Admin area is a
/// page per block now (the class keeps its name: it began as the tests of the cards of the one long page): the three states of the node (open, metadata encryption
/// on, database encrypted) show what is left to do and nothing else; the answer of "Ask every device now" sits next to its button; every action ends at the page of
/// its button and says what happened in the message area of that page; a switch the node refuses leaves its form; the master password is a password field.
/// </summary>
public sealed class AdminCardsTests : IAsyncLifetime
{
    private const string Password = "AdminCards123";

    private const string MetadataPage = "/Admin/MetadataEncryption";

    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _apiClient = null!;
    private HttpClient _admin = null!;
    private readonly Dictionary<string, string> _cookies = [];

    public async Task InitializeAsync()
    {
        _apiClient = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "WebNode", password: Password);
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _admin = await WebLogin.LoginAsync(_web, "admin", Password);
        foreach (var pair in string.Join("; ", _admin.DefaultRequestHeaders.GetValues("Cookie")).Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            _cookies[pair[..eq]] = pair[(eq + 1)..];
        }
    }

    public Task DisposeAsync()
    {
        _apiClient.Dispose();
        _admin.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ the three states

    [Fact]
    public async Task An_open_node_shows_the_whole_page_the_way_to_encrypt_the_database_and_a_link_to_the_page_a_blocker_names()
    {
        var metadata = await GetAsync(MetadataPage);
        Body(metadata).Should().Contain("Ask every device now");
        About(metadata).Should().Contain("Before you switch it on, take a fresh backup of this node and keep it offline");
        Body(metadata).Should().NotContain("On since");

        var storage = await GetAsync("/Admin/StorageProtection");
        Body(storage).Should().Contain("Database encryption").And.Contain("a plain file").And.Contain("href=\"/VaultEncryption\"");
        storage.Should().NotContain("Storage protection");

        var page = await GetAsync("/VaultEncryption");
        page.Should().Contain("<title>Database encryption").And.Contain("Encrypt this node");
        page.Should().NotContain("Storage protection");
        // The blocker that names a page of the Admin area links to that page.
        var blocker = Regex.Match(page, "<li data-blocker=\"not_activated\">(.*?)</li>", RegexOptions.Singleline).Groups[1].Value;
        blocker.Should().Contain("Activate the new sync format first")
            .And.Contain("<a href=\"/Admin/MetadataEncryption\">Admin > Metadata encryption</a>")
            .And.NotContain("Admin > Sync");
    }

    [Fact]
    public async Task With_metadata_encryption_on_the_page_is_one_status_line_and_a_plain_database_still_offers_its_encryption()
    {
        await AskAsync();
        var done = await PostAdminAsync(MetadataPage, "ActivateMetadata", new() { ["masterPassword"] = Password, ["backupTaken"] = "true" });
        done.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var admin = await FollowAsync(done);
        var body = Body(admin);
        Regex.Match(body, "<p class=\"text-sm\" id=\"metadata-status\".*?</p>", RegexOptions.Singleline).Value
            .Should().MatchRegex(@"On\s*</strong>\s*since \d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC\.");
        body.Should().NotContain("Ask every device now").And.NotContain("Switch metadata encryption on").And.NotContain("name=\"masterPassword\"")
            .And.NotContain("name=\"backupTaken\"");
        About(admin).Should().NotContain("Before you switch it on").And.NotContain("Ask every device now");
        Message(admin).Should().Contain("Metadata encryption is on. Every device switches as it syncs.", "the result of the switch is on the page, where the button was");
        Regex.Matches(body, "<form").Should().BeEmpty("a finished switch has no form");

        Body(await GetAsync("/Admin/StorageProtection")).Should().Contain("Database encryption", "the database of this node is still a plain file");

        // The next visit of the page: the status line alone.
        var again = await GetAsync(MetadataPage);
        Body(again).Should().Contain("<strong>On</strong>");
        Message(again).Should().NotContain("Metadata encryption is on. Every device");
        var page = await GetAsync("/VaultEncryption");
        page.Should().NotContain("Activate the new sync format first", "the sync format is active now");
    }

    // ------------------------------------------------------------------ "Ask every device now"

    [Fact]
    public async Task What_Ask_every_device_now_found_sits_next_to_its_button_and_the_page_opens_where_the_button_was()
    {
        var asked = await PostAdminAsync(MetadataPage, "CheckMetadataReadiness", []);

        asked.StatusCode.Should().Be(HttpStatusCode.Redirect);
        WebUtility.UrlDecode(asked.Headers.Location!.OriginalString).Should().Be(MetadataPage, "the same page, with nothing to say in the address");

        var admin = await FollowAsync(asked);
        var form = Regex.Match(Body(admin), "<form[^>]*id=\"metadata-ask-form\".*?</form>", RegexOptions.Singleline).Value;
        form.Should().NotBeEmpty();
        form.Should().Contain("data-busy", "the button spins while the devices answer").And.Contain("data-busy-note").And.Contain("Asking every device");
        form.Should().Contain("id=\"metadata-ask-result\"").And.Contain("No other device to ask.").And.Contain("Asked just now.");
        form.IndexOf("Ask every device now", StringComparison.Ordinal).Should().BeLessThan(form.IndexOf("metadata-ask-result", StringComparison.Ordinal),
            "the result follows the button in the same row");
        Body(admin).Should().Contain("Switch metadata encryption on", "every device was asked and none stands in the way");
    }

    [Fact]
    public async Task A_switch_the_node_refuses_leaves_the_form_and_the_reason_is_in_the_message_area()
    {
        await AskAsync();

        var wrong = await PostAdminAsync(MetadataPage, "ActivateMetadata", new() { ["masterPassword"] = "not the password", ["backupTaken"] = "true" });
        var admin = await FollowAsync(wrong);

        Message(admin).Should().Contain("Wrong master password.", "the refusal is on the page");
        Body(admin).Should().Contain("id=\"metadata-activate-form\"", "the form stays after a refusal").And.Contain("Switch metadata encryption on");
        Body(admin).Should().NotContain("Wrong master password.", "a message is not repeated in the content");
        (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/readiness?peers=false")).GetProperty("activated").GetBoolean().Should().BeFalse();

        // ...and again after the next refusal (no backup box), and when the page is simply opened again.
        var noBox = await PostAdminAsync(MetadataPage, "ActivateMetadata", new() { ["masterPassword"] = Password });
        var second = await FollowAsync(noBox);
        Message(second).Should().Contain("Take a fresh backup of this node first");
        Body(second).Should().Contain("id=\"metadata-activate-form\"");
        Body(await GetAsync(MetadataPage)).Should().Contain("id=\"metadata-activate-form\"");
    }

    [Fact]
    public async Task The_master_password_of_the_switch_is_a_password_field_with_autocomplete_off()
    {
        await AskAsync();

        var admin = await GetAsync(MetadataPage);

        var form = Regex.Match(Body(admin), "<form[^>]*id=\"metadata-activate-form\".*?</form>", RegexOptions.Singleline).Value;
        var field = Regex.Match(form, "<sl-input[^>]*name=\"masterPassword\"[^>]*>", RegexOptions.Singleline).Value;
        field.Should().Contain("type=\"password\"").And.Contain("autocomplete=\"off\"").And.NotContain("bmb-secret");
        foreach (var neighbour in new[] { "/Admin/MasterPassword", "/Admin/DekRotation", "/Admin/ResetNode" })
            Regex.Matches(Body(await GetAsync(neighbour)), "<sl-input[^>]*>", RegexOptions.Singleline).Select(m => m.Value).Should().OnlyContain(
                tag => !tag.Contains("password", StringComparison.OrdinalIgnoreCase) || (tag.Contains("type=\"password\"") && tag.Contains("autocomplete=\"off\"")),
                $"the password fields of {neighbour} are password fields with autocomplete off");
    }

    // ------------------------------------------------------------------ every action ends at its page

    [Theory]
    [InlineData("MasterPassword", "ChangeMasterPassword")]
    [InlineData("ResetNode", "ResetNode")]
    [InlineData("DekRotation", "ProposeDekRotation")]
    [InlineData("DekRotation", "CancelDekRotation")]
    [InlineData("MetadataEncryption", "ActivateMetadata")]
    [InlineData("Agents", "DeleteAgent")]
    [InlineData("TrustedNodes", "RevokeNode")]
    [InlineData("TrustedNodes", "ChangeNodeAddress")]
    [InlineData("Snapshots", "DeleteSnapshot")]
    public async Task An_action_ends_at_the_page_of_its_button_and_says_what_came_of_it_there(string page, string handler)
    {
        var fields = new Dictionary<string, string>
        {
            ["masterPassword"] = "", ["oldPassword"] = "", ["newPassword"] = "", ["commitEventId"] = Guid.NewGuid().ToString(), ["eventId"] = Guid.NewGuid().ToString(),
            ["agentId"] = "999999", ["nodeId"] = Guid.NewGuid().ToString(), ["newApiAddress"] = "http://127.0.0.1:1", ["password"] = "x", ["fileName"] = "no-such-snapshot.tar.gz",
        };

        var response = await PostAdminAsync("/Admin/" + page, handler, fields);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = WebUtility.UrlDecode(response.Headers.Location!.OriginalString);
        location.Should().StartWith($"/Admin/{page}?");
        var message = Regex.Match(location, "[?&](?:err|msg)=([^&#]*)").Groups[1].Value;
        message.Should().NotBeNullOrWhiteSpace($"{handler} says what came of it: {location}");
        var admin = await FollowAsync(response);
        Message(admin).Should().Contain(message, "the reason is in the message area of the page");
        Body(admin).Should().NotContain(message, "and not repeated in its content");
    }

    [Fact]
    public async Task A_device_that_cannot_be_confirmed_is_said_on_the_page_of_the_restored_devices()
    {
        // No device is waiting: the page says so, and the refusal is told in its message area.
        var response = await PostAdminAsync("/Admin/RestoredDevices", "ConfirmRestoredPeer", new() { ["nodeId"] = Guid.NewGuid().ToString() });

        var admin = await FollowAsync(response);

        Body(admin).Should().NotContain("id=\"restored-peers\"").And.Contain("No device is waiting for your confirmation.");
        Message(admin).Should().Contain("Could not confirm the device.");
    }

    [Fact]
    public async Task The_forms_that_send_something_slow_are_marked_busy_so_that_they_spin_and_cannot_be_sent_twice()
    {
        await AskAsync();

        foreach (var (page, handler) in new[]
                 {
                     ("MetadataEncryption", "CheckMetadataReadiness"), ("MetadataEncryption", "ActivateMetadata"), ("MasterPassword", "ChangeMasterPassword"),
                     ("DekRotation", "ProposeDekRotation"), ("TagGraph", "RebuildGraph"),
                 })
            Regex.Match(await GetAsync("/Admin/" + page), "<form[^>]*handler=" + handler + "[^>]*>", RegexOptions.Singleline).Value.Should().Contain("data-busy", $"the form of {handler}");
        // The one owner of the busy state of a form is bmb-busy.js (BMB-311); site.js had a second copy that let the form go after two minutes.
        var script = await GetAsync("/js/bmb-busy.js");
        script.Should().Contain("form[data-busy]").And.Contain("data-busy-note");
        (await GetAsync("/js/site.js")).Should().NotContain("form[data-busy]", "one owner of the state of a busy form").And.NotContain("bmbSetFormBusy");
    }

    [Fact]
    public async Task The_question_of_a_form_that_asks_first_is_asked_on_every_page_not_only_by_the_script_of_the_Admin_pages()
    {
        // The encryption page carries no script of the Admin pages; its "Encrypt this node" and "Erase these copies" forms ask first (data-confirm).
        var site = await GetAsync("/js/site.js");
        var admin = await GetAsync("/js/pages/admin.js");

        site.Should().Contain("form.form-confirm");
        admin.Should().NotContain("form.form-confirm", "a second question would be asked on the Admin pages");
        (await GetAsync("/VaultEncryption")).Should().Contain("/js/site.js");
    }

    // ------------------------------------------------------------------ BMB-313: Invisible Mode lives here, the contact times are read as contact

    [Fact]
    public async Task Invisible_Mode_is_switched_in_Admin_and_the_header_only_shows_a_badge_while_it_is_on()
    {
        var admin = await GetAsync("/Admin/InvisibleMode");
        var body = Body(admin);
        body.Should().Contain("Turn Invisible Mode on").And.Contain("data-busy").And.Contain(">off<");
        admin.Should().Contain("id=\"syncInvisibleBadge\"", "the sync icon carries the badge");

        var on = await PostAdminAsync("/Admin/InvisibleMode", "SetInvisibleMode", new() { ["enabled"] = "true" });
        try
        {
            WebUtility.UrlDecode(on.Headers.Location!.OriginalString).Should().StartWith("/Admin/InvisibleMode?msg=Invisible Mode is on");
            var after = await FollowAsync(on);
            Message(after).Should().Contain("Invisible Mode is on");
            Body(after).Should().Contain("Turn Invisible Mode off").And.Contain(">on<");
            (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/invisible")).GetProperty("isInvisible").GetBoolean().Should().BeTrue();
            (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/delivery-status")).GetProperty("isInvisible").GetBoolean().Should().BeTrue(
                "the header reads this to show its badge");
        }
        finally
        {
            var off = await PostAdminAsync("/Admin/InvisibleMode", "SetInvisibleMode", new() { ["enabled"] = "false" });
            var after = await FollowAsync(off);
            Message(after).Should().Contain("Invisible Mode is off");
            Body(after).Should().Contain("Turn Invisible Mode on");
        }
    }

    [Fact]
    public async Task The_header_script_has_no_invisible_mode_switch_any_more_and_asks_a_node_on_a_double_click()
    {
        var script = await GetAsync("/js/site-sync-status.js");

        script.Should().NotContain("InvisibleModeSwitch").And.NotContain("sl-switch", "the switch left the dropdown and the dialog");
        script.Should().Contain("syncInvisibleBadge").And.Contain("Change it in Admin").And.Contain("/Admin/InvisibleMode");
        script.Should().NotContain("/Admin#", "the page of the Invisible Mode is a page of its own, not a card of the Admin page");
        script.Should().Contain("dblclick").And.Contain("/api-proxy/sync/nodes/").And.Contain("/check");
        script.Should().Contain("lastContactDirection").And.Contain("notAnsweringSince").And.Contain("lastPushAt");
        script.Should().NotContain("last sync ", "the time shown is the last contact, and the last push has its own words");
        // One implementation for both places that show the status (the dropdown and the dialog of the article page).
        System.Text.RegularExpressions.Regex.Matches(script, "function getRelativeTime").Should().HaveCount(1);
    }

    [Fact]
    public async Task Trusted_nodes_shows_the_last_contact_with_its_direction_and_for_a_node_that_calls_in_when_it_last_did()
    {
        var phone = Guid.NewGuid();
        using (var scope = _api.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = phone, DisplayName = "Kitchen phone", Ed25519PublicKey = new byte[32], ApiAddress = null,
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        _api.Services.GetRequiredService<PeerContactLog>().NoteInbound(phone, DateTime.UtcNow.AddMinutes(-3));

        var admin = await GetAsync("/Admin/TrustedNodes");

        var row = System.Text.RegularExpressions.Regex.Match(admin, "<tr>(?:(?!</tr>).)*Kitchen phone.*?</tr>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        row.Should().NotBeEmpty();
        var cell = System.Text.RegularExpressions.Regex.Match(row, "<td[^>]*data-contact-state=.*?</td>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        cell.Should().Contain("3 min ago").And.Contain("it → us").And.NotContain("Never");
        admin.Should().Contain("Last contact", "the column that used to be \"Last Sync\"");
    }

    // ------------------------------------------------------------------ the browser

    private async Task AskAsync()
    {
        var asked = await PostAdminAsync(MetadataPage, "CheckMetadataReadiness", []);
        asked.StatusCode.Should().Be(HttpStatusCode.Redirect);
        await FollowAsync(asked);   // the page reads the answer
    }

    // ------------------------------------------------------------------ BMB-305 fix round (B1, B3, B4, B5)

    /// <summary>A sealed row held for a key that has not arrived: the node then refuses the switch with the blocker "events_held".</summary>
    private async Task HoldARowAsync()
    {
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().AppendAsync(
            new SyncEvent
            {
                EventId = Guid.NewGuid(), NodeId = Guid.NewGuid(), LamportTs = 5, EventType = "sealed", Payload = "{}",
                Signature = new byte[64], ProtocolVersion = 4, CreatedAt = DateTime.UtcNow,
            },
            null,
            new EventRowLocal(ApplyState: EventApplyStates.Held, HoldReason: "key", OriginSuperadmin: false));
    }

    [Fact]
    public async Task A_switch_refused_for_a_condition_keeps_the_form_lists_the_condition_and_says_so_on_the_page()
    {
        await AskAsync();   // nothing stands in the way yet: the form is there
        await HoldARowAsync();   // ... and now something does

        var refused = await PostAdminAsync(MetadataPage, "ActivateMetadata", new() { ["masterPassword"] = Password, ["backupTaken"] = "true" });
        var page = await FollowAsync(refused);

        Message(page).Should().Contain("Not every condition is met yet", "the refusal is on the page");
        Body(page).Should().Contain("id=\"metadata-blockers\"").And.Contain("This node holds changes it has not applied yet", "what stands in the way is listed");
        Body(page).Should().Contain("id=\"metadata-activate-form\"", "the form stays after a refusal: the person fixes what is said and presses it again");
        (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/readiness?peers=false")).GetProperty("activated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_switch_that_finds_metadata_encryption_already_on_is_not_a_refusal_and_the_page_shows_the_real_state()
    {
        await AskAsync();
        var fields = new Dictionary<string, string> { ["masterPassword"] = Password, ["backupTaken"] = "true" };
        (await PostAdminAsync(MetadataPage, "ActivateMetadata", fields)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var again = await PostAdminAsync(MetadataPage, "ActivateMetadata", fields);   // a second tab, a repeated click

        var location = WebUtility.UrlDecode(again.Headers.Location!.OriginalString);
        location.Should().Contain("Metadata encryption is already on.").And.NotContain("Not every condition").And.NotContain("err=");
        var page = await FollowAsync(again);
        Body(page).Should().Contain("id=\"metadata-status\"", "the page says what is true: it is on").And.Contain("since");
        Body(page).Should().NotContain("metadata-activate-form").And.NotContain("Not yet:").And.NotContain("Ask every device now");
    }

    [Fact]
    public async Task The_kept_answer_is_in_a_cookie_of_its_own_that_expires_by_itself_and_never_outgrows_a_cookie()
    {
        using (var scope = _api.Services.CreateScope())
        {
            var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            for (var i = 0; i < 150; i++)   // devices that never answered, with names that do not compress
                await whitelist.CreateAsync(new WhitelistEntry
                {
                    NodeId = Guid.NewGuid(), DisplayName = $"Device {i} {Guid.NewGuid():N}{Guid.NewGuid():N}", Ed25519PublicKey = new byte[32], ApiAddress = null,
                    Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
        }

        var asked = await PostAdminAsync(MetadataPage, "CheckMetadataReadiness", []);

        var cookie = asked.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("bmb_metadata_readiness=", StringComparison.Ordinal));
        cookie.Length.Should().BeLessThan(4096, "a browser drops a cookie that is bigger, and the page would lose its answer");
        var attributes = cookie.ToLowerInvariant();
        attributes.Should().Contain("max-age=600", "it expires by itself, not when a later visit happens to look at its age").And.Contain("httponly");
        attributes.Should().Contain("path=/admin/metadataencryption", "and it is sent to the page that reads it, not to every page of the Admin area");
        asked.Headers.GetValues("Set-Cookie").Should().NotContain(c => c.StartsWith(".AspNetCore.Mvc.CookieTempDataProvider=", StringComparison.Ordinal) && c.Length > 1000,
            "the answer no longer rides in the TempData cookie");
        var body = Body(await FollowAsync(asked));
        body.Should().Contain("150 of 150 devices are not ready.", "the summary counts all of them, though the list that was kept is cut to fit");
        body.Should().Contain("never seen by this node or the devices it asked", "and the ones that are not ready are the ones listed");
    }

    [Fact]
    public async Task A_damaged_or_foreign_kept_answer_is_ignored_and_the_page_starts_from_the_quick_look()
    {
        // A browser of its own, with no memory but what is handed to it: the session, and a cookie that this site did not protect.
        using var browser = _web.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var get = new HttpRequestMessage(HttpMethod.Get, MetadataPage);
        get.Headers.TryAddWithoutValidation("Cookie", $"bee_session={_cookies["bee_session"]}; bmb_metadata_readiness=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        var response = await browser.SendAsync(get);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a damaged cookie is not an error of the page");
        Body(html).Should().Contain("Ask every device now");
        Body(html).Should().NotContain("metadata-activate-form", "nothing was asked that this page can vouch for").And.NotContain("Asked ");
        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("bmb_metadata_readiness=;", StringComparison.Ordinal), "and the damaged cookie is dropped");
    }

    [Theory]
    [InlineData("", "DismissPasswordNotice", "")]
    [InlineData("/MasterPassword", "DismissPasswordNotice", "")]
    [InlineData("", "PeerAcceptDekRotation", "eventId")]
    [InlineData("", "PeerRejectDekRotation", "eventId")]
    [InlineData("/DekRotation", "PeerAcceptDekRotation", "eventId")]
    [InlineData("/DekRotation", "PeerRejectDekRotation", "eventId")]
    public async Task Every_post_goes_back_to_its_page_also_the_notice_and_the_peer_rotation_answers(string page, string handler, string idField)
    {
        var fields = idField.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { [idField] = Guid.NewGuid().ToString() };

        var response = await PostAdminAsync("/Admin" + page, handler, fields);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = WebUtility.UrlDecode(response.Headers.Location!.OriginalString);
        location.Should().StartWith("/Admin" + page + "?");
        location.Should().MatchRegex("[?&](msg|err)=.+");
    }

    [Fact]
    public void Every_master_password_input_of_the_pages_keeps_the_browser_from_remembering_it()
    {
        var pages = Path.Combine(RepoRoot(), "server", "BeeMemoryBank.Web", "Pages");
        var inputs = 0;
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match tag in Regex.Matches(text, @"<sl-input\b[^>]*>", RegexOptions.Singleline))
            {
                if (!Regex.IsMatch(tag.Value, @"name=""masterPassword""|label=""[^""]*[Mm]aster [Pp]assword", RegexOptions.None)) continue;
                inputs++;
                if (!tag.Value.Contains("autocomplete=\"off\"", StringComparison.Ordinal))
                    problems.Add($"{Path.GetRelativePath(pages, file)}:{text[..tag.Index].Count(c => c == '\n') + 1}");
            }
        }
        inputs.Should().BeGreaterThan(8, "the scan should find the master password inputs of the pages");
        problems.Should().BeEmpty("a master password field carries autocomplete=\"off\"");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    /// <summary>The content of an Admin page: everything after the description (the template's <c>admin-page-body</c>).</summary>
    private static string Body(string html)
    {
        var at = html.IndexOf("id=\"admin-page-body\"", StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, "the page is drawn by the template of the Admin area");
        return html[at..];
    }

    /// <summary>The description the page gives of itself (the template's <c>admin-page-about</c>), up to its content.</summary>
    private static string About(string html)
    {
        var from = html.IndexOf("id=\"admin-page-about\"", StringComparison.Ordinal);
        var to = html.IndexOf("id=\"admin-page-body\"", StringComparison.Ordinal);
        (from >= 0 && to > from).Should().BeTrue("the page is drawn by the template of the Admin area");
        return html[from..to];
    }

    /// <summary>The message area of the template: where what the last action did is shown, right under the title.</summary>
    private static string Message(string html)
    {
        var from = html.IndexOf("id=\"admin-page-message\"", StringComparison.Ordinal);
        var to = html.IndexOf("id=\"admin-page-about\"", StringComparison.Ordinal);
        (from >= 0 && to > from).Should().BeTrue("the page is drawn by the template of the Admin area");
        return html[from..to];
    }

    private async Task<string> GetAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task<string> FollowAsync(HttpResponseMessage redirect)
    {
        redirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return await GetAsync(redirect.Headers.Location!.OriginalString);
    }

    /// <summary>Posts a handler of an Admin page the way the browser does: the page's antiforgery token, then the cookies.</summary>
    private async Task<HttpResponseMessage> PostAdminAsync(string page, string handler, Dictionary<string, string> fields)
    {
        var html = await GetAsync(page);
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = Token.Match(html).Groups[1].Value };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{page}?handler={handler}") { Content = new FormUrlEncodedContent(body) };
        return await SendAsync(request);
    }

    /// <summary>Sends with the cookies a browser would hold, and keeps the ones the answer sets (the page's TempData is one of them).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(c => c.Key + "=" + c.Value)));
        var response = await _admin.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var set))
            foreach (var cookie in set)
            {
                var pair = cookie.Split(';')[0];
                var eq = pair.IndexOf('=');
                var name = pair[..eq];
                var value = pair[(eq + 1)..];
                if (value.Length == 0 || cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)) _cookies.Remove(name);
                else _cookies[name] = value;
            }
        return response;
    }
}
