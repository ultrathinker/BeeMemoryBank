extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey;
using Dapper;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AdminPageCatalog = WebApp::BeeMemoryBank.Web.Pages.Admin.AdminPageCatalog;
using ProxyRouteFlags = WebApp::BeeMemoryBank.Web.Services.ProxyRouteFlags;
using ProxyRouteTable = WebApp::BeeMemoryBank.Web.Services.ProxyRouteTable;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The skeleton of Dream (package P0a): the six tables in chat.db with their re-key fates, the node reset, every route of plan 3.8 behind the
/// superadmin and non-agent gates with 409 locked on changes while locked, the services every package fills, and the Web side (proxy entry, Admin
/// catalog row).
/// </summary>
public class DreamSkeletonTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private const string Password = "dreamSkeletonPw1!";

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);
        (await Session.UnlockAsync(Password)).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private SessionService Session => _factory.Services.GetRequiredService<SessionService>();

    private SqliteConnection ChatDb() =>
        (SqliteConnection)_factory.Services.GetRequiredService<ChatDbConnectionFactory>().CreateConnection();

    private static readonly string[] SealedTables = ["dream_advice", "dream_rule", "dream_run_event"];

    // ── chat.db ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChatDb_HoldsTheSixDreamTables_EachWithItsFate()
    {
        using var conn = ChatDb();
        var tables = conn.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'dream_%'").ToList();
        tables.Should().BeEquivalentTo(DreamSchema.Tables);

        foreach (var t in SealedTables)
        {
            RekeyTables.Chat[t].Should().Be(TableFate.Resealed, t);
            var columns = conn.Query<(string Name, string Type, long Pk)>($"SELECT name, type, pk FROM pragma_table_info('{t}')").ToList();
            columns.Should().Contain(("id", "TEXT", 1L), $"{t}: the re-key walks SELECT id, ... and builds the AAD from it");
            columns.Select(c => c.Name).Should().Contain(["payload_ciphertext", "payload_iv", "payload_key_v"], t);
        }
        foreach (var t in new[] { "dream_run", "dream_settings", "dream_examined" })
            RekeyTables.Chat[t].Should().Be(TableFate.Plaintext, t);
    }

    [Fact]
    public void TheSettingsRow_HoldsThePlansDefaults()
    {
        using var conn = ChatDb();
        var row = conn.QuerySingle(
            "SELECT schedule_enabled, schedule_start, schedule_window_minutes, max_advice_scheduled, max_advice_manual, max_open_advice, min_score, "
            + "manual_max_minutes, max_model_calls, max_tokens, use_model, analysis_model_id, excluded_folder_ids FROM dream_settings WHERE id = 1");
        var d = DreamSettings.Default;
        ((long)row.schedule_enabled == 1).Should().Be(d.ScheduleEnabled);
        ((string)row.schedule_start).Should().Be(d.ScheduleStart);
        ((long)row.schedule_window_minutes).Should().Be(d.ScheduleWindowMinutes);
        ((long)row.max_advice_scheduled).Should().Be(d.MaxAdviceScheduled);
        ((long)row.max_advice_manual).Should().Be(d.MaxAdviceManual);
        ((long)row.max_open_advice).Should().Be(d.MaxOpenAdvice);
        ((long)row.min_score).Should().Be(d.MinScore);
        ((long)row.manual_max_minutes).Should().Be(d.ManualMaxMinutes);
        ((long)row.max_model_calls).Should().Be(d.MaxModelCalls);
        ((long)row.max_tokens).Should().Be(d.MaxTokens);
        ((long)row.use_model == 1).Should().Be(d.UseModel);
        ((object?)row.analysis_model_id).Should().BeNull();
        ((string)row.excluded_folder_ids).Should().Be("[]");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM dream_settings").Should().Be(1);
    }

    [Fact]
    public async Task TheSchema_IsIdempotent_AndTheRunTableTakesItsTriggerColumn()
    {
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ChatDbInitializer>().InitializeAsync();

        using var conn = ChatDb();
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM dream_settings").Should().Be(1, "a second start keeps the one settings row");
        var id = Guid.NewGuid().ToString("D");
        conn.Execute("INSERT INTO dream_run (id, trigger, mode, state, created_at) VALUES (@id, 'schedule', 'normal', 'queued', @at)",
            new { id, at = DateTime.UtcNow.ToString("o") });
        conn.ExecuteScalar<string>("SELECT trigger FROM dream_run WHERE id = @id", new { id }).Should().Be("schedule");
        conn.Execute("INSERT INTO dream_examined (candidate_key, task, content_marker, prompt_version, verdict, examined_at) VALUES ('k', 't', 'm', 'pair-1', 'nothing', 'x')");
        FluentActions.Invoking(() => conn.Execute("INSERT INTO dream_settings (id) VALUES (2)")).Should().Throw<SqliteException>("one settings row");
    }

    [Fact]
    public async Task ANodeReset_EmptiesEveryDreamTable_AndTurnsTheScheduleOff()
    {
        using (var conn = ChatDb())
        {
            var at = DateTime.UtcNow.ToString("o");
            var blob = new byte[] { 1, 2, 3 };
            conn.Execute("UPDATE dream_settings SET schedule_enabled = 1, max_advice_manual = 77");
            conn.Execute("INSERT INTO dream_run (id, trigger, mode, state, created_at) VALUES (@id, 'manual', 'normal', 'finished', @at)", new { id = Guid.NewGuid().ToString("D"), at });
            conn.Execute("INSERT INTO dream_advice (id, run_id, seq, kind, source, apply_mode, score, created_at, payload_ciphertext, payload_iv, payload_key_v) "
                         + "VALUES (@id, 'r', 1, 'merge_tags', 'rule', 'code', 85, @at, @blob, @blob, 1)", new { id = Guid.NewGuid().ToString("D"), at, blob });
            conn.Execute("INSERT INTO dream_rule (id, scope, created_at, payload_ciphertext, payload_iv, payload_key_v) VALUES (@id, 'kind', @at, @blob, @blob, 1)",
                new { id = Guid.NewGuid().ToString("D"), at, blob });
            conn.Execute("INSERT INTO dream_run_event (id, seq, run_id, at, kind, payload_ciphertext, payload_iv, payload_key_v) VALUES (@id, 1, 'r', @at, 'info', @blob, @blob, 1)",
                new { id = Guid.NewGuid().ToString("D"), at, blob });
            conn.Execute("INSERT INTO dream_examined (candidate_key, task, content_marker, prompt_version, verdict, examined_at) VALUES ('k', 't', 'm', 'pair-1', 'nothing', @at)", new { at });
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var hook = scope.ServiceProvider.GetServices<INodeResetHook>().OfType<ApiStateResetHook>().Single();
            await hook.AfterVaultWipedAsync(CancellationToken.None);
        }

        using (var conn = ChatDb())
        {
            foreach (var t in DreamSchema.ResetTables)
                conn.ExecuteScalar<long>($"SELECT COUNT(*) FROM {t}").Should().Be(0, $"{t} names the old vault's notes");
            var s = conn.QuerySingle<(long Schedule, long MaxManual)>("SELECT schedule_enabled, max_advice_manual FROM dream_settings WHERE id = 1");
            s.Schedule.Should().Be(0, "the nightly run is turned on per node, on purpose");
            s.MaxManual.Should().Be(77, "the limits are the node's own settings, kept");
        }
    }

    // ── Routes ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] PlanRoutes =
    [
        "GET /api/dream/status", "GET /api/dream/settings", "PUT /api/dream/settings",
        "POST /api/dream/runs", "POST /api/dream/runs/{id:guid}/stop", "GET /api/dream/runs", "GET /api/dream/runs/{id:guid}",
        "GET /api/dream/runs/{id:guid}/advice", "GET /api/dream/runs/{id:guid}/events",
        "GET /api/dream/advice", "GET /api/dream/advice/{id:guid}", "GET /api/dream/advice/{id:guid}/prompt",
        "POST /api/dream/advice/{id:guid}/apply", "POST /api/dream/advice/{id:guid}/dismiss", "POST /api/dream/advice/{id:guid}/status",
        "GET /api/dream/rules", "DELETE /api/dream/rules/{id:guid}", "GET /api/dream/compare",
        // The descriptions of the "View all rules" dialog (Dream UX round 2, package A).
        "GET /api/dream/rulebook",
    ];

    private List<RouteEndpoint> DreamRoutes() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/dream", StringComparison.Ordinal) == true)
            .ToList();

    [Fact]
    public void EveryRouteOfPlan38_IsMapped_AndCarriesTheSuperadminGate()
    {
        var routes = DreamRoutes();
        routes.Select(e => $"{e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Should().BeEquivalentTo(PlanRoutes);
        routes.Should().OnlyContain(e => e.Metadata.GetMetadata<BeeMemoryBank.Api.Helpers.RequiresSuperadmin>() != null);
        // A handler taking HttpContext alone would bind as a RequestDelegate and drop its IResult (an empty 200): every Dream handler is a
        // delegate whose parameters are bound.
        routes.Should().OnlyContain(e => e.Metadata.GetMetadata<IParameterBindingMetadata>() != null);
    }

    [Fact]
    public async Task Status_AnswersTheSuperadmin()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/dream/status");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Should().NotBeEmpty($"headers: {response.Headers} {response.Content.Headers}");
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        body.GetProperty("available").GetBoolean().Should().BeTrue();
        body.GetProperty("activeRun").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("settings").GetProperty("maxAdviceScheduled").GetInt32().Should().Be(10);
        body.GetProperty("openAdvice").GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task EveryDreamRoute_RefusesAnOrdinaryUser_AndAnAgentOfTheSuperadmin()
    {
        int userId;
        string apiKey = AgentKeyHelper.GenerateApiKey();
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            userId = await users.CreateAsync(new User
            {
                Username = "dream-regular-user", DisplayName = "Regular", Role = UserRoles.User, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            var owner = await users.GetByUsernameAsync("admin");
            await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
            {
                Name = "dream-agent",
                KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
                KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                Status = "A",
                CreatedAt = DateTime.UtcNow,
                OwnerUserId = owner!.Id,
            });
        }

        using var user = _factory.Server.CreateClient();
        user.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        user.DefaultRequestHeaders.Add("X-User-Role", UserRoles.User);
        user.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());

        using var agent = _factory.Server.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        foreach (var route in DreamRoutes())
        {
            var method = route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single();
            var path = route.RoutePattern.RawText!.Replace("{id:guid}", Guid.NewGuid().ToString("D"));

            using var asUser = new HttpRequestMessage(new HttpMethod(method), path) { Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { }) };
            (await user.SendAsync(asUser)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path} as an ordinary user");

            using var asAgent = new HttpRequestMessage(new HttpMethod(method), path) { Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { }) };
            var response = await agent.SendAsync(asAgent);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path} as an agent: {body}");
            body.Should().Contain("Agents cannot perform this operation", $"{method} {path} stops at the agent gate; there is no Dream over MCP");
        }
    }

    [Fact]
    public async Task WhileLocked_TheStatusSaysSo_AndEveryChangeAnswers409Locked()
    {
        Session.Lock();
        using var client = _factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/dream/status");
        status.GetProperty("available").GetBoolean().Should().BeFalse();
        status.GetProperty("reason").GetString().Should().Be(DreamErrorCodes.Locked);

        var id = Guid.NewGuid();
        foreach (var (method, path) in new[]
                 {
                     ("PUT", "/api/dream/settings"), ("POST", "/api/dream/runs"), ("POST", $"/api/dream/advice/{id}/apply"),
                     ("POST", $"/api/dream/advice/{id}/dismiss"), ("POST", $"/api/dream/advice/{id}/status"), ("DELETE", $"/api/dream/rules/{id}"),
                 })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (path.EndsWith("/status", StringComparison.Ordinal)) request.Content = JsonContent.Create(new { status = "applied" });
            else if (method is "PUT") request.Content = JsonContent.Create(new DreamSettingsUpdateRequest());
            else if (method is "POST") request.Content = JsonContent.Create(new { });
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, $"{method} {path}");
            (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Code.Should().Be(DreamErrorCodes.Locked, $"{method} {path}");
        }
    }

    // ── Services and the Web side ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDreamClass_IsRegistered_OnAFullNode()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetServices<IDreamDetector>().Select(d => d.GetType()).Should().BeEquivalentTo(
        [
            typeof(TagVariantsDetector), typeof(UnusedTagsDetector), typeof(EmptyFolderDetector), typeof(TestLeftoversDetector),
            typeof(ExactDuplicateDetector), typeof(NearDuplicateDetector), typeof(MisplacedArticleDetector),
        ]);
        sp.GetServices<IDreamModelTask>().Select(t => (t.Name, t.PromptVersion)).Should().BeEquivalentTo(
        [
            (DreamNames.PairVerdictTask, DreamNames.PairPromptVersion),
            (DreamNames.FolderReviewTask, DreamNames.FolderPromptVersion),
            (DreamNames.TreeReviewTask, DreamNames.TreePromptVersion),
        ]);
        sp.GetRequiredService<IDreamModelClient>().Should().BeOfType<OpenRouterDreamModelClient>();
        foreach (var type in new[]
                 {
                     typeof(DreamStore), typeof(DreamRunner), typeof(DreamCorpusBuilder), typeof(PairCandidateSource), typeof(DreamAdviceValidator),
                     typeof(DreamApplyService), typeof(DreamReferenceChecker), typeof(DreamPromptBuilder),
                 })
            sp.GetRequiredService(type).Should().NotBeNull(type.Name);
        _factory.Services.GetRequiredService<DreamCoordinator>().Should().BeSameAs(_factory.Services.GetRequiredService<DreamCoordinator>());
        _factory.Services.GetServices<IHostedService>().OfType<DreamScheduler>().Should().ContainSingle();
    }

    [Fact]
    public void TheWebProxiesDream_ToTheSuperadmin_WithTheAntiforgeryToken_AndTheAdminAreaListsIt()
    {
        var entry = ProxyRouteTable.Entries["dream"];
        entry.UpstreamPrefix.Should().Be("/api/dream");
        entry.Methods.Select(m => (m.Method, m.RequiredRole)).Should().BeEquivalentTo(
            [("GET", UserRoles.Superadmin), ("POST", UserRoles.Superadmin), ("PUT", UserRoles.Superadmin), ("DELETE", UserRoles.Superadmin)]);
        entry.Flags.HasFlag(ProxyRouteFlags.RequireAntiforgery).Should().BeTrue("the page's changes carry its RequestVerificationToken");

        var page = AdminPageCatalog.Find("/Admin/Dream");
        page.Should().NotBeNull();
        page!.Section.Should().Be(AdminPageCatalog.SearchAndAi);
        page.Icon.Should().Be("moon-stars");
        page.Title.Should().Be("Dream");
    }
}
