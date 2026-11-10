using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The sources of Admin -> Dream (package U2, plan 3.9) against the settings contract of plan 3.8: the script names every field of
/// <see cref="DreamSettingsUpdateRequest"/> and no field it does not have, the ranges it checks before sending and the defaults it shows
/// while the endpoints are not there yet equal <see cref="DreamSettings"/>' own, and every element the script touches exists on the page.
/// The real save and its out-of-range refusal arrive with the Dream service (P0b) on these same names and ranges.
/// </summary>
public sealed class DreamSettingsPageSourceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static readonly string PageSource = File.ReadAllText(Path.Combine(RepoRoot(), "server", "BeeMemoryBank.Web", "Pages", "Admin", "Dream.cshtml"));
    private static readonly string Js = File.ReadAllText(Path.Combine(RepoRoot(), "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "dream-admin.js"));

    /// <summary>The text between the balanced braces that follow <paramref name="marker"/>'s opening one, one property per line.</summary>
    private static Dictionary<string, string> JsMap(string marker)
    {
        var at = Js.IndexOf(marker, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the script keeps a {marker} block");
        var open = Js.IndexOf('{', at);
        var depth = 0;
        var end = -1;
        for (var i = open; i < Js.Length && end < 0; i++)
        {
            if (Js[i] == '{') depth++;
            else if (Js[i] == '}' && --depth == 0) end = i;
        }
        end.Should().BeGreaterThan(0, marker);
        var rows = new Dictionary<string, string>();
        foreach (var line in Js[(open + 1)..end].Split('\n'))
        {
            var row = Regex.Match(line, @"^\s+(\w+):\s*(.+?),?\s*$");
            if (row.Success) rows[row.Groups[1].Value] = row.Groups[2].Value.Trim();
        }
        return rows;
    }

    /// <summary>The (min, max) of one of DreamSettings' range tuples: named tuple elements are compile-time only, the fields are Item1/Item2.</summary>
    private static (long Min, long Max) RangeOf(string csField)
    {
        var tuple = typeof(DreamSettings).GetField(csField, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var type = tuple.GetType();
        return (Convert.ToInt64(type.GetField("Item1")!.GetValue(tuple), CultureInfo.InvariantCulture),
                Convert.ToInt64(type.GetField("Item2")!.GetValue(tuple), CultureInfo.InvariantCulture));
    }

    private static string Unquote(string value) => value.Trim('\'', '"');

    /// <summary>The camelCase wire names of the settings contract (what PUT /api/dream/settings takes).</summary>
    private static HashSet<string> ContractFields() =>
        typeof(DreamSettingsUpdateRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void TheFormScript_SendsEveryFieldOfTheSettingsContract_AndNoFieldItDoesNotHave()
    {
        var fields = ContractFields();
        foreach (var field in fields)
            Js.Should().MatchRegex(@"\b" + Regex.Escape(field) + @"\b", $"the contract field {field} has its control and its place in the payload");

        var at = Js.IndexOf("var payload = {", StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, "the script builds the whole settings object it saves");
        var sent = JsMap("var payload = {").Keys;
        sent.Should().BeEquivalentTo(fields, "the save sends exactly what the contract holds, under the same names");
    }

    [Fact]
    public void TheRangesTheScriptChecks_BeforeSending_AreTheRangesTheSettingsAccept()
    {
        var ranges = JsMap("var RANGES = {");
        ranges.Keys.Should().BeEquivalentTo(new[]
        {
            "scheduleWindowMinutes", "maxAdviceScheduled", "maxAdviceManual", "maxOpenAdvice", "minScore", "manualMaxMinutes", "maxModelCalls", "maxTokens",
        }, "every bounded field of the contract is bounded in the form as well");

        foreach (var (jsName, csField) in new Dictionary<string, string>
                 {
                     ["scheduleWindowMinutes"] = "ScheduleWindowRange",
                     ["maxAdviceScheduled"] = "MaxAdviceScheduledRange",
                     ["maxAdviceManual"] = "MaxAdviceManualRange",
                     ["maxOpenAdvice"] = "MaxOpenAdviceRange",
                     ["minScore"] = "MinScoreRange",
                     ["manualMaxMinutes"] = "ManualMaxMinutesRange",
                     ["maxModelCalls"] = "MaxModelCallsRange",
                     ["maxTokens"] = "MaxTokensRange",
                 })
        {
            var (min, max) = RangeOf(csField);
            var bounds = Regex.Match(ranges[jsName], @"^\[\s*(\d+)\s*,\s*(\d+)\s*\]$");
            bounds.Success.Should().BeTrue($"{jsName} in the script's RANGES is [min, max]");
            long.Parse(bounds.Groups[1].Value).Should().Be(min, $"{jsName}: the lower bound of the form is DreamSettings' own");
            long.Parse(bounds.Groups[2].Value).Should().Be(max, $"{jsName}: the upper bound of the form is DreamSettings' own");
        }
    }

    [Fact]
    public void TheDraftShownWhileTheEndpointsAreMissing_IsTheSettingsRow()
    {
        var draft = JsMap("var DEFAULTS = {");
        var d = DreamSettings.Default;
        var expected = new Dictionary<string, string>
        {
            ["scheduleEnabled"] = d.ScheduleEnabled.ToString().ToLowerInvariant(),
            ["scheduleStart"] = d.ScheduleStart,
            ["scheduleWindowMinutes"] = d.ScheduleWindowMinutes.ToString(),
            ["maxAdviceScheduled"] = d.MaxAdviceScheduled.ToString(),
            ["maxAdviceManual"] = d.MaxAdviceManual.ToString(),
            ["maxOpenAdvice"] = d.MaxOpenAdvice.ToString(),
            ["minScore"] = d.MinScore.ToString(),
            ["manualMaxMinutes"] = d.ManualMaxMinutes.ToString(),
            ["maxModelCalls"] = d.MaxModelCalls.ToString(),
            ["maxTokens"] = d.MaxTokens.ToString(),
            ["useModel"] = d.UseModel.ToString().ToLowerInvariant(),
        };
        draft.Keys.Should().BeEquivalentTo(expected.Keys, "the draft shows every plain field of the settings");
        foreach (var (name, value) in expected)
            Unquote(draft[name]).Should().Be(value, $"{name}: the draft of the form is DreamSettings.Default");
    }

    [Fact]
    public void EveryElementTheScriptTouches_IsOnThePage_WithItsBounds()
    {
        var ids = Regex.Matches(Js, @"byId\('([a-z-]+)'\)").Select(m => m.Groups[1].Value).Distinct().ToList();
        ids.Should().NotBeEmpty();
        foreach (var id in ids)
            PageSource.Should().Contain($"id=\"{id}\"", $"the script fills '{id}'");

        foreach (Match m in Regex.Matches(PageSource, @"<sl-input id=""(dream-[\w-]+)""([^>]*)>"))
        {
            var tag = m.Groups[2].Value;
            if (!tag.Contains("type=\"number\"", StringComparison.Ordinal)) continue;
            tag.Should().Contain("min=\"", m.Groups[1].Value);
            tag.Should().Contain("max=\"", m.Groups[1].Value + ": the browser's own bound sits next to the script's check");
        }

        // The page says what it does while the endpoints are stubs, and what the model switch sends out.
        PageSource.Should().Contain("id=\"dream-endpoint-note\"").And.Contain("hidden");
        PageSource.Should().Contain("note content leaves this node");
        PageSource.Should().Contain("/Admin/AiChat", "the keys and the models of the catalogue live on AI / Chat");
    }
}

/// <summary>
/// Admin -> Dream through the real Web host in front of a real Api (plan 3.9): the page is the superadmin's and carries its script, and the
/// settings paths it uses behave as the contract says while the real endpoints are not there yet - GET answers "not implemented" (501), a
/// change without the page's anti-forgery token is refused locally, and a save with the token reaches the Api.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class DreamSettingsPageTests : IAsyncLifetime
{
    private const string AdminPassword = "DreamAdmin12345";
    private const string BobPassword = "BobAdmin123456";

    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "DreamAdminNode", password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/users", new { username = "bob", password = BobPassword, displayName = "Bob", role = "user", passwordHint = "A test hint" }))
            .EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _admin = await WebLogin.LoginAsync(_web, "admin", AdminPassword);
        _bob = await WebLogin.LoginAsync(_web, "bob", BobPassword);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _bob.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ThePage_IsTheSuperadmins_AndCarriesTheFormAndItsScript()
    {
        using var asAdmin = await _admin.GetAsync("/Admin/Dream");
        asAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await asAdmin.Content.ReadAsStringAsync());
        html.Should().Contain("id=\"dream-settings\"");
        // The section titles sit behind an <sl-icon> in the heading: compare without whitespace.
        var compact = Regex.Replace(html, @"\s+", string.Empty);
        foreach (var section in new[] { ">Schedule</h3>", ">Limits</h3>", ">Model</h3>", ">Excludedfolders</h3>", ">Dismissrules</h3>" })
            compact.Should().Contain(section, $"the section {section.Trim('<', '/', 'h', '3', '>')} of plan 3.9");
        html.Should().Contain("/js/pages/dream-admin.js?v=", "the page loads its own script (PageScripts), not only admin.js");
        html.Should().Contain("id=\"btn-save-dream-settings\"");
        html.Should().Contain("id=\"dream-rules-body\"");

        using var asBob = await _bob.GetAsync("/Admin/Dream");
        asBob.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await asBob.Content.ReadAsStringAsync()).Should().NotContain("id=\"dream-settings\"");
    }

    [Fact]
    public async Task TheSettingsSave_WithThePageToken_AndFiftyAdvicePerNightIsKept()
    {
        var settings = await _admin.GetAsync("/api-proxy/dream/settings");
        settings.StatusCode.Should().Be(HttpStatusCode.OK, "the Dream service (P0b) is merged");
        var current = JsonSerializer.Deserialize<JsonElement>(await settings.Content.ReadAsStringAsync());

        (await _admin.GetAsync("/api-proxy/dream/rules")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _bob.GetAsync("/api-proxy/dream/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var page = await _admin.GetStringAsync("/Admin/Dream");
        var token = Token.Match(page).Groups[1].Value;
        token.Should().NotBeEmpty("the page carries the token its script saves with");

        var body = new DreamSettingsUpdateRequest
        {
            ScheduleEnabled = current.GetProperty("scheduleEnabled").GetBoolean(),
            ScheduleStart = current.GetProperty("scheduleStart").GetString()!,
            ScheduleWindowMinutes = current.GetProperty("scheduleWindowMinutes").GetInt32(),
            MaxAdviceScheduled = 50,
            MaxAdviceManual = current.GetProperty("maxAdviceManual").GetInt32(),
            MaxOpenAdvice = current.GetProperty("maxOpenAdvice").GetInt32(),
            MinScore = current.GetProperty("minScore").GetInt32(),
            ManualMaxMinutes = current.GetProperty("manualMaxMinutes").GetInt32(),
            MaxModelCalls = current.GetProperty("maxModelCalls").GetInt32(),
            MaxTokens = current.GetProperty("maxTokens").GetInt64(),
            UseModel = current.GetProperty("useModel").GetBoolean(),
        };

        using var noToken = await _admin.PutAsJsonAsync("/api-proxy/dream/settings", body);
        noToken.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a change made by a script carries the page's RequestVerificationToken");

        using var save = new HttpRequestMessage(HttpMethod.Put, "/api-proxy/dream/settings") { Content = JsonContent.Create(body) };
        save.Headers.Add("RequestVerificationToken", token);
        using var saved = await _admin.SendAsync(save);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());

        var after = JsonSerializer.Deserialize<JsonElement>(await _admin.GetStringAsync("/api-proxy/dream/settings"));
        after.GetProperty("maxAdviceScheduled").GetInt32().Should().Be(50, "the owner asked that 50 advice a night be possible");
    }
}
