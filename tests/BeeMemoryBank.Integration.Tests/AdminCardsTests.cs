using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-305 / BMB-303, through the real Web host in front of a real Api, with the cookies of a browser (the TempData of the page travels in one):
/// the three states of the node (open, metadata encryption on, database encrypted) show what is left to do and nothing else; the answer of "Ask every device
/// now" sits next to its button; every action ends at the card of its button and says what happened in that card; a switch the node refuses leaves its form;
/// the master password is a password field.
/// </summary>
public sealed class AdminCardsTests : IAsyncLifetime
{
    private const string Password = "AdminCards123";

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
    public async Task An_open_node_shows_the_whole_card_the_way_to_encrypt_the_database_and_a_link_to_the_card_a_blocker_names()
    {
        var admin = await GetAsync("/Admin");
        var card = Region(admin, "card-metadata");
        card.Should().Contain("Ask every device now").And.Contain("Before you switch it on, take a fresh backup of this node");
        card.Should().NotContain("On since");
        Region(admin, "card-database-encryption").Should().Contain("Database encryption").And.Contain("a plain file").And.Contain("href=\"/VaultEncryption\"");
        admin.Should().NotContain("Storage protection");

        var page = await GetAsync("/VaultEncryption");
        page.Should().Contain("<title>Database encryption").And.Contain("Encrypt this node");
        page.Should().NotContain("Storage protection");
        // The blocker that names a section of the Admin page links to the card of that section.
        var blocker = Regex.Match(page, "<li data-blocker=\"not_activated\">(.*?)</li>", RegexOptions.Singleline).Groups[1].Value;
        blocker.Should().Contain("Activate the new sync format first")
            .And.Contain("<a href=\"/Admin#card-metadata\">Admin > Metadata encryption</a>")
            .And.NotContain("Admin > Sync");
    }

    [Fact]
    public async Task With_metadata_encryption_on_the_card_is_one_status_line_and_a_plain_database_still_offers_its_encryption()
    {
        await AskAsync();
        var done = await PostAdminAsync("ActivateMetadata", new() { ["masterPassword"] = Password, ["backupTaken"] = "true" });
        done.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var admin = await FollowAsync(done);
        var card = Region(admin, "card-metadata");
        Regex.Match(card, "<p class=\"text-sm\" id=\"metadata-status\".*?</p>", RegexOptions.Singleline).Value
            .Should().MatchRegex(@"On\s*</strong>\s*since \d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC\.");
        card.Should().NotContain("Ask every device now").And.NotContain("Switch metadata encryption on").And.NotContain("name=\"masterPassword\"")
            .And.NotContain("name=\"backupTaken\"").And.NotContain("Before you switch it on").And.NotContain("This cannot be switched off");
        card.Should().Contain("Metadata encryption is on. Every device switches as it syncs.", "the result of the switch is in the card, where the button was");
        Regex.Matches(card, "<form").Should().BeEmpty("a finished switch has no form");

        Region(admin, "card-database-encryption").Should().Contain("Database encryption", "the database of this node is still a plain file");

        // The next visit of the page: the status line alone.
        var again = await GetAsync("/Admin");
        Region(again, "card-metadata").Should().Contain("<strong>On</strong>").And.NotContain("Metadata encryption is on. Every device");
        var page = await GetAsync("/VaultEncryption");
        page.Should().NotContain("Activate the new sync format first", "the sync format is active now");
    }

    // ------------------------------------------------------------------ "Ask every device now"

    [Fact]
    public async Task What_Ask_every_device_now_found_sits_next_to_its_button_and_the_page_opens_at_the_card()
    {
        var asked = await PostAdminAsync("CheckMetadataReadiness", []);

        asked.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = WebUtility.UrlDecode(asked.Headers.Location!.OriginalString);
        location.Should().EndWith("#card-metadata").And.Contain("card=card-metadata");

        var admin = await FollowAsync(asked);
        var form = Regex.Match(Region(admin, "card-metadata"), "<form[^>]*id=\"metadata-ask-form\".*?</form>", RegexOptions.Singleline).Value;
        form.Should().NotBeEmpty();
        form.Should().Contain("data-busy", "the button spins while the devices answer").And.Contain("data-busy-note").And.Contain("Asking every device");
        form.Should().Contain("id=\"metadata-ask-result\"").And.Contain("No other device to ask.").And.Contain("Asked just now.");
        form.IndexOf("Ask every device now", StringComparison.Ordinal).Should().BeLessThan(form.IndexOf("metadata-ask-result", StringComparison.Ordinal),
            "the result follows the button in the same row");
        Region(admin, "card-metadata").Should().Contain("Switch metadata encryption on", "every device was asked and none stands in the way");
    }

    [Fact]
    public async Task A_switch_the_node_refuses_leaves_the_form_and_the_reason_is_in_the_card()
    {
        await AskAsync();

        var wrong = await PostAdminAsync("ActivateMetadata", new() { ["masterPassword"] = "not the password", ["backupTaken"] = "true" });
        var admin = await FollowAsync(wrong);

        var card = Region(admin, "card-metadata");
        card.Should().Contain("Wrong master password.", "the refusal is in the card");
        card.Should().Contain("id=\"metadata-activate-form\"", "the form stays after a refusal").And.Contain("Switch metadata encryption on");
        TopOfPage(admin).Should().NotContain("Wrong master password.", "a message of a card is not repeated at the top of the page");
        (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/readiness?peers=false")).GetProperty("activated").GetBoolean().Should().BeFalse();

        // ...and again after the next refusal (no backup box), and when the page is simply opened again.
        var noBox = await PostAdminAsync("ActivateMetadata", new() { ["masterPassword"] = Password });
        var second = await FollowAsync(noBox);
        Region(second, "card-metadata").Should().Contain("Take a fresh backup of this node first").And.Contain("id=\"metadata-activate-form\"");
        Region(await GetAsync("/Admin"), "card-metadata").Should().Contain("id=\"metadata-activate-form\"");
    }

    [Fact]
    public async Task The_master_password_of_the_switch_is_a_password_field_with_autocomplete_off()
    {
        await AskAsync();

        var admin = await GetAsync("/Admin");

        var form = Regex.Match(Region(admin, "card-metadata"), "<form[^>]*id=\"metadata-activate-form\".*?</form>", RegexOptions.Singleline).Value;
        var field = Regex.Match(form, "<sl-input[^>]*name=\"masterPassword\"[^>]*>", RegexOptions.Singleline).Value;
        field.Should().Contain("type=\"password\"").And.Contain("autocomplete=\"off\"").And.NotContain("bmb-secret");
        foreach (var neighbour in new[] { "card-master-password", "card-dek-rotation" })
            Regex.Matches(Region(admin, neighbour), "<sl-input[^>]*>", RegexOptions.Singleline).Select(m => m.Value).Should().OnlyContain(
                tag => !tag.Contains("password", StringComparison.OrdinalIgnoreCase) || (tag.Contains("type=\"password\"") && tag.Contains("autocomplete=\"off\"")),
                $"the password fields of {neighbour} are password fields with autocomplete off");
    }

    // ------------------------------------------------------------------ every action ends at its card

    [Theory]
    [InlineData("ChangeMasterPassword", "card-master-password")]
    [InlineData("ResetNode", "card-reset-node")]
    [InlineData("ProposeDekRotation", "card-dek-rotation")]
    [InlineData("CancelDekRotation", "card-dek-rotation")]
    [InlineData("ActivateMetadata", "card-metadata")]
    [InlineData("DeleteAgent", "card-agents")]
    [InlineData("RevokeNode", "card-trusted-nodes")]
    [InlineData("ChangeNodeAddress", "card-trusted-nodes")]
    [InlineData("DeleteSnapshot", "card-snapshots")]
    public async Task An_action_ends_at_the_card_of_its_button_and_says_what_came_of_it_in_that_card(string handler, string card)
    {
        var fields = new Dictionary<string, string>
        {
            ["masterPassword"] = "", ["oldPassword"] = "", ["newPassword"] = "", ["commitEventId"] = Guid.NewGuid().ToString(), ["eventId"] = Guid.NewGuid().ToString(),
            ["agentId"] = "999999", ["nodeId"] = Guid.NewGuid().ToString(), ["newApiAddress"] = "http://127.0.0.1:1", ["password"] = "x", ["fileName"] = "no-such-snapshot.tar.gz",
        };

        var response = await PostAdminAsync(handler, fields);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = WebUtility.UrlDecode(response.Headers.Location!.OriginalString);
        location.Should().EndWith("#" + card).And.Contain("card=" + card);
        var message = Regex.Match(location, "[?&](?:err|msg)=([^&#]*)").Groups[1].Value;
        message.Should().NotBeNullOrWhiteSpace($"{handler} says what came of it: {location}");
        var admin = await FollowAsync(response);
        Region(admin, card).Should().Contain(message, "the reason is in the card");
        TopOfPage(admin).Should().NotContain(message, "and not at the top of the page");
    }

    [Fact]
    public async Task A_message_that_names_a_card_the_page_does_not_show_is_not_lost_it_goes_to_the_top()
    {
        // The restored-devices card exists only while a restore left devices to confirm.
        var response = await PostAdminAsync("ConfirmRestoredPeer", new() { ["nodeId"] = Guid.NewGuid().ToString() });

        var admin = await FollowAsync(response);

        admin.Should().NotContain("id=\"restored-peers\"");
        TopOfPage(admin).Should().Contain("Could not confirm the device.");
    }

    [Fact]
    public async Task The_forms_that_send_something_slow_are_marked_busy_so_that_they_spin_and_cannot_be_sent_twice()
    {
        await AskAsync();

        var admin = await GetAsync("/Admin");

        foreach (var handler in new[] { "CheckMetadataReadiness", "ActivateMetadata", "ChangeMasterPassword", "ProposeDekRotation", "RebuildGraph" })
            Regex.Match(admin, "<form[^>]*handler=" + handler + "[^>]*>", RegexOptions.Singleline).Value.Should().Contain("data-busy", $"the form of {handler}");
        // The one owner of the busy state of a form is bmb-busy.js (BMB-311); site.js had a second copy that let the form go after two minutes.
        var script = await GetAsync("/js/bmb-busy.js");
        script.Should().Contain("form[data-busy]").And.Contain("data-busy-note");
        (await GetAsync("/js/site.js")).Should().NotContain("form[data-busy]", "one owner of the state of a busy form").And.NotContain("bmbSetFormBusy");
    }

    [Fact]
    public async Task The_question_of_a_form_that_asks_first_is_asked_on_every_page_not_only_by_the_script_of_the_Admin_page()
    {
        // The encryption page carries no script of the Admin page; its "Encrypt this node" and "Erase these copies" forms ask first (data-confirm).
        var site = await GetAsync("/js/site.js");
        var admin = await GetAsync("/js/pages/admin.js");

        site.Should().Contain("form.form-confirm");
        admin.Should().NotContain("form.form-confirm", "a second question would be asked on the Admin page");
        (await GetAsync("/VaultEncryption")).Should().Contain("/js/site.js");
    }

    // ------------------------------------------------------------------ BMB-313: Invisible Mode lives here, the contact times are read as contact

    [Fact]
    public async Task Invisible_Mode_is_switched_in_Admin_and_the_header_only_shows_a_badge_while_it_is_on()
    {
        var admin = await GetAsync("/Admin");
        var card = Region(admin, "card-invisible-mode");
        card.Should().Contain("Invisible Mode").And.Contain("Turn Invisible Mode on").And.Contain("data-busy").And.Contain(">off<");
        admin.Should().Contain("id=\"syncInvisibleBadge\"", "the sync icon carries the badge");

        var on = await PostAdminAsync("SetInvisibleMode", new() { ["enabled"] = "true" });
        try
        {
            WebUtility.UrlDecode(on.Headers.Location!.OriginalString).Should().EndWith("#card-invisible-mode").And.Contain("card=card-invisible-mode");
            var after = await FollowAsync(on);
            Region(after, "card-invisible-mode").Should().Contain("Invisible Mode is on").And.Contain("Turn Invisible Mode off").And.Contain(">on<");
            (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/invisible")).GetProperty("isInvisible").GetBoolean().Should().BeTrue();
            (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/delivery-status")).GetProperty("isInvisible").GetBoolean().Should().BeTrue(
                "the header reads this to show its badge");
        }
        finally
        {
            var off = await PostAdminAsync("SetInvisibleMode", new() { ["enabled"] = "false" });
            Region(await FollowAsync(off), "card-invisible-mode").Should().Contain("Invisible Mode is off").And.Contain("Turn Invisible Mode on");
        }
    }

    [Fact]
    public async Task The_header_script_has_no_invisible_mode_switch_any_more_and_asks_a_node_on_a_double_click()
    {
        var script = await GetAsync("/js/site-sync-status.js");

        script.Should().NotContain("InvisibleModeSwitch").And.NotContain("sl-switch", "the switch left the dropdown and the dialog");
        script.Should().Contain("syncInvisibleBadge").And.Contain("Change it in Admin").And.Contain("/Admin#card-invisible-mode");
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

        var admin = await GetAsync("/Admin");

        var row = System.Text.RegularExpressions.Regex.Match(admin, "<tr>(?:(?!</tr>).)*Kitchen phone.*?</tr>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        row.Should().NotBeEmpty();
        var cell = System.Text.RegularExpressions.Regex.Match(row, "<td[^>]*data-contact-state=.*?</td>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        cell.Should().Contain("3 min ago").And.Contain("it \u2192 us").And.NotContain("Never");
        admin.Should().Contain("Last contact", "the column that used to be \"Last Sync\"");
    }

    // ------------------------------------------------------------------ the browser

    private async Task AskAsync()
    {
        var asked = await PostAdminAsync("CheckMetadataReadiness", []);
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
    public async Task A_switch_refused_for_a_condition_keeps_the_form_lists_the_condition_and_says_so_in_the_card()
    {
        await AskAsync();   // nothing stands in the way yet: the form is there
        await HoldARowAsync();   // ... and now something does

        var refused = await PostAdminAsync("ActivateMetadata", new() { ["masterPassword"] = Password, ["backupTaken"] = "true" });
        var card = Region(await FollowAsync(refused), "card-metadata");

        card.Should().Contain("Not every condition is met yet", "the refusal is in the card");
        card.Should().Contain("id=\"metadata-blockers\"").And.Contain("This node holds changes it has not applied yet", "what stands in the way is listed");
        card.Should().Contain("id=\"metadata-activate-form\"", "the form stays after a refusal: the person fixes what is said and presses it again");
        (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/readiness?peers=false")).GetProperty("activated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_switch_that_finds_metadata_encryption_already_on_is_not_a_refusal_and_the_card_shows_the_real_state()
    {
        await AskAsync();
        var fields = new Dictionary<string, string> { ["masterPassword"] = Password, ["backupTaken"] = "true" };
        (await PostAdminAsync("ActivateMetadata", fields)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var again = await PostAdminAsync("ActivateMetadata", fields);   // a second tab, a repeated click

        var location = WebUtility.UrlDecode(again.Headers.Location!.OriginalString);
        location.Should().Contain("Metadata encryption is already on.").And.NotContain("Not every condition").And.NotContain("err=");
        var card = Region(await FollowAsync(again), "card-metadata");
        card.Should().Contain("id=\"metadata-status\"", "the card says what is true: it is on").And.Contain("since");
        card.Should().NotContain("metadata-activate-form").And.NotContain("Not yet:").And.NotContain("Ask every device now");
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

        var asked = await PostAdminAsync("CheckMetadataReadiness", []);

        var cookie = asked.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("bmb_metadata_readiness=", StringComparison.Ordinal));
        cookie.Length.Should().BeLessThan(4096, "a browser drops a cookie that is bigger, and the card would lose its answer");
        var attributes = cookie.ToLowerInvariant();
        attributes.Should().Contain("max-age=600", "it expires by itself, not when a later visit happens to look at its age").And.Contain("path=/admin").And.Contain("httponly");
        asked.Headers.GetValues("Set-Cookie").Should().NotContain(c => c.StartsWith(".AspNetCore.Mvc.CookieTempDataProvider=", StringComparison.Ordinal) && c.Length > 1000,
            "the answer no longer rides in the TempData cookie");
        var card = Region(await FollowAsync(asked), "card-metadata");
        card.Should().Contain("150 of 150 devices are not ready.", "the summary counts all of them, though the list that was kept is cut to fit");
        card.Should().Contain("never seen by this node or the devices it asked", "and the ones that are not ready are the ones listed");
    }

    [Fact]
    public async Task A_damaged_or_foreign_kept_answer_is_ignored_and_the_card_starts_from_the_quick_look()
    {
        // A browser of its own, with no memory but what is handed to it: the session, and a cookie that this site did not protect.
        using var browser = _web.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var get = new HttpRequestMessage(HttpMethod.Get, "/Admin");
        get.Headers.TryAddWithoutValidation("Cookie", $"bee_session={_cookies["bee_session"]}; bmb_metadata_readiness=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        var response = await browser.SendAsync(get);
        var card = Region(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()), "card-metadata");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a damaged cookie is not an error of the page");
        card.Should().Contain("Ask every device now");
        card.Should().NotContain("metadata-activate-form", "nothing was asked that this page can vouch for").And.NotContain("Asked ");
        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("bmb_metadata_readiness=;", StringComparison.Ordinal), "and the damaged cookie is dropped");
    }

    [Theory]
    [InlineData("DismissPasswordNotice", "", "card-master-password")]
    [InlineData("PeerAcceptDekRotation", "eventId", "card-dek-rotation")]
    [InlineData("PeerRejectDekRotation", "eventId", "card-dek-rotation")]
    public async Task Every_post_goes_back_to_its_card_also_the_notice_and_the_peer_rotation_answers(string handler, string idField, string card)
    {
        var fields = idField.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { [idField] = Guid.NewGuid().ToString() };

        var response = await PostAdminAsync(handler, fields);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = WebUtility.UrlDecode(response.Headers.Location!.OriginalString);
        location.Should().EndWith("#" + card).And.Contain("card=" + card);
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

    /// <summary>The part of the page from the heading with this id up to the next heading, details block or comment: the card.</summary>
    private static string Region(string html, string id)
    {
        var at = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page has {id}");
        var ends = new[] { "<h3", "<sl-details", "<!-- " }.Select(tag => html.IndexOf(tag, at + 1, StringComparison.Ordinal)).Where(i => i >= 0).ToList();
        return ends.Count == 0 ? html[at..] : html[at..ends.Min()];
    }

    /// <summary>Everything before the first card of the page: where a message of the page itself is shown.</summary>
    private static string TopOfPage(string html)
    {
        var at = html.IndexOf("id=\"card-updates\"", StringComparison.Ordinal);
        return at < 0 ? html : html[..at];
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

    private async Task<HttpResponseMessage> PostAdminAsync(string handler, Dictionary<string, string> fields)
    {
        var page = await GetAsync("/Admin");
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = Token.Match(page).Groups[1].Value };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Admin?handler={handler}") { Content = new FormUrlEncodedContent(body) };
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
