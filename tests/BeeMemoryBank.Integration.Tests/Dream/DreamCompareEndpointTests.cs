using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>GET /api/dream/compare</c> (plan 3.8, package C1) on a real node: run rows and sealed candidate events as the engine writes
/// them (dream_run, dream_run_event under the chat key with <see cref="DreamAad.Event"/>) are read back as one table, with the notes
/// named by their current titles. The rows are put in by the test, so the engine (package P0b) is not needed.
/// </summary>
public class DreamCompareEndpointTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private const string Password = "dreamCompareTestPw1!";
    private long _seq;

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

    // ── Putting rows in, the way the engine will ────────────────────────────────────────────────────────────────

    private Guid SeedRun(string mode = "trial", string? label = "Test model", int examined = 3, string state = "finished")
    {
        var id = Guid.NewGuid();
        using var conn = ChatDb();
        conn.Execute(
            @"INSERT INTO dream_run (id, trigger, mode, state, outcome, phase, created_at, started_at, finished_at, use_model, model_id, model_label,
                                     max_advice, max_minutes, max_model_calls, max_tokens, candidates_examined, advice_created, model_calls, prompt_tokens, completion_tokens)
              VALUES (@id, 'manual', @mode, @state, 'completed', 'pairs', @at, @at, @at, 1, 'vendor/model', @label, 50, 10, 200, 1500000, @examined, 1, @examined, 4800, 250)",
            new { id = id.ToString("D"), mode, state, label, examined, at = DateTime.UtcNow.ToString("o") });
        return id;
    }

    private async Task SealEvent(Guid runId, string kind, DreamEventPayload payload)
    {
        var id = Guid.NewGuid();
        using var key = await _factory.Services.GetRequiredService<ChatDataProtector>().AcquireAsync();
        var (cipher, iv) = key.EncryptText(DreamJson.Serialize(payload), DreamAad.Event(id));
        using var conn = ChatDb();
        conn.Execute(
            @"INSERT INTO dream_run_event (id, seq, run_id, at, kind, payload_ciphertext, payload_iv, payload_key_v)
              VALUES (@id, @seq, @run, @at, @kind, @cipher, @iv, @v)",
            new { id = id.ToString("D"), seq = ++_seq, run = runId.ToString("D"), at = DateTime.UtcNow.ToString("o"), kind, cipher, iv, v = ChatDataProtector.ChatKeyVersion });
    }

    private Task SealCandidate(Guid runId, string key, IReadOnlyList<Guid> articles, string? verdict, DreamAdviceKind? kind, int? score,
        DreamExaminedVerdict outcome = DreamExaminedVerdict.Advice, IReadOnlyList<Guid>? folders = null)
    {
        var data = new DreamCandidateEventData(key, DreamNames.PairVerdictTask, 1, articles, folders ?? [], outcome, verdict, kind, score,
            verdict == null ? null : $"The model says {verdict}.");
        return SealEvent(runId, "candidate", new DreamEventPayload("candidate examined", articles, JsonSerializer.SerializeToNode(data, DreamJson.Options) as JsonObject));
    }

    private async Task<Guid> CreateNoteAsync(string title, string path = "/Compare", string? passphrase = null)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/articles", new { title, treePath = path, content = $"Body of {title}.", passphrase });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> CompareAsync(params object[] runs)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/dream/compare" + (runs.Length == 0 ? "" : "?runs=" + string.Join(",", runs)));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text));
    }

    // ── The table ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThreeTrialRuns_BecomeOneTable_WithTheNotesNamedAndNullsWhereARunDidNotLook()
    {
        var first = await CreateNoteAsync("Backup policy");
        var second = await CreateNoteAsync("Backup policy (old)");
        var third = await CreateNoteAsync("Kids puzzles");
        var fourth = await CreateNoteAsync("Puzzles for kids");
        DreamAdviceKind? none = null;
        Guid a = SeedRun(label: "Model one"), b = SeedRun(label: "Model two"), c = SeedRun(label: "Model three", examined: 1);

        // Both runs of the first two models look at both pairs; the third, slow one only at the first.
        await SealCandidate(a, "pair-1", [first, second], "superseded", DreamAdviceKind.Superseded, 86);
        await SealCandidate(a, "pair-2", [third, fourth], "overlap", DreamAdviceKind.Overlap, 64);
        await SealCandidate(b, "pair-1", [first, second], "superseded", DreamAdviceKind.Superseded, 90);
        await SealCandidate(b, "pair-2", [third, fourth], "unrelated", none, 12, DreamExaminedVerdict.Nothing);
        await SealCandidate(c, "pair-1", [first, second], "duplicate", DreamAdviceKind.Duplicate, 95);

        var (status, body) = await CompareAsync(a, b, c);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("modelLabel").GetString()).Should().Equal("Model one", "Model two", "Model three");
        body.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("mode").GetString()).Should().OnlyContain(m => m == "trial");
        body.GetProperty("common").GetInt32().Should().Be(1);
        body.GetProperty("agreementBasis").GetInt32().Should().Be(1);
        body.GetProperty("agreement").GetDouble().Should().Be(0, "the third run called the common pair a duplicate, the first two a superseded one");
        body.GetProperty("runsWithoutLog").GetArrayLength().Should().Be(0);

        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        rows[0].GetProperty("candidateKey").GetString().Should().Be("pair-1", "the candidate every run examined comes first");
        rows[0].GetProperty("task").GetString().Should().Be("pair_verdict");
        rows[0].GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("title").GetString()).Should().Equal("Backup policy", "Backup policy (old)");
        rows[0].GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("treePath").GetString()).Should().OnlyContain(p => p == "/Compare");
        var results = rows[0].GetProperty("results");
        results.GetProperty(a.ToString("D")).GetProperty("verdict").GetString().Should().Be("superseded");
        results.GetProperty(a.ToString("D")).GetProperty("kind").GetString().Should().Be("superseded");
        results.GetProperty(b.ToString("D")).GetProperty("score").GetInt32().Should().Be(90);
        results.GetProperty(c.ToString("D")).GetProperty("verdict").GetString().Should().Be("duplicate");
        results.GetProperty(c.ToString("D")).GetProperty("outcome").GetString().Should().Be("advice");

        var second2 = rows[1].GetProperty("results");
        second2.GetProperty(c.ToString("D")).ValueKind.Should().Be(JsonValueKind.Null, "the slow run never reached the second pair");
        second2.GetProperty(b.ToString("D")).GetProperty("outcome").GetString().Should().Be("nothing");
        second2.GetProperty(b.ToString("D")).GetProperty("kind").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheRunSummaries_CarryWhatThePageShowsAboutEachRun()
    {
        var a = SeedRun(label: "Model one");
        var b = SeedRun(label: "Model two");

        var (status, body) = await CompareAsync(a, b);

        status.Should().Be(HttpStatusCode.OK);
        var run = body.GetProperty("runs")[0];
        run.GetProperty("id").GetGuid().Should().Be(a);
        run.GetProperty("state").GetString().Should().Be("finished");
        run.GetProperty("outcome").GetString().Should().Be("completed");
        run.GetProperty("useModel").GetBoolean().Should().BeTrue();
        run.GetProperty("counters").GetProperty("modelCalls").GetInt32().Should().Be(3);
        run.GetProperty("counters").GetProperty("promptTokens").GetInt64().Should().Be(4800);
        run.GetProperty("limits").GetProperty("maxMinutes").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task ANoteUnderTheSecondPassword_OrOneThatIsGone_HasItsIdOnly()
    {
        var plain = await CreateNoteAsync("A plain note");
        var secret = await CreateNoteAsync("A secret title", passphrase: "hunter2hunter2");
        var gone = Guid.NewGuid();
        Guid a = SeedRun(), b = SeedRun();
        await SealCandidate(a, "odd", [plain, secret, gone], "overlap", DreamAdviceKind.Overlap, 60);
        await SealCandidate(b, "odd", [plain, secret, gone], "overlap", DreamAdviceKind.Overlap, 61);

        var (status, body) = await CompareAsync(a, b);

        status.Should().Be(HttpStatusCode.OK);
        var objects = body.GetProperty("rows")[0].GetProperty("objects").EnumerateArray().ToList();
        objects.Select(o => o.GetProperty("id").GetGuid()).Should().Equal([plain, secret, gone]);
        objects[0].GetProperty("title").GetString().Should().Be("A plain note");
        objects[1].GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null, "Dream never reads a note under the second password, nor names it");
        objects[2].GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
        body.ToString().Should().NotContain("A secret title");
    }

    [Fact]
    public async Task AFolderCandidate_IsNamedByItsPath()
    {
        using (var client = _factory.CreateClient())
        {
            var folder = await client.PostAsJsonAsync("/api/folders", new { path = "/Cmp/Folder", name = "Folder" });
            folder.EnsureSuccessStatusCode();
            var folderId = (await folder.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Guid a = SeedRun(), b = SeedRun();
            await SealCandidate(a, "folder-1", [], "move", DreamAdviceKind.MoveArticle, 70, folders: [folderId]);
            await SealCandidate(b, "folder-1", [], "none", null, 20, DreamExaminedVerdict.Nothing, folders: [folderId]);

            var (status, body) = await CompareAsync(a, b);

            status.Should().Be(HttpStatusCode.OK);
            var objects = body.GetProperty("rows")[0].GetProperty("objects").EnumerateArray().ToList();
            objects.Should().ContainSingle();
            objects[0].GetProperty("type").GetString().Should().Be("folder");
            objects[0].GetProperty("path").GetString().Should().Be("/Cmp/Folder");
        }
    }

    [Fact]
    public async Task APayloadThatDoesNotOpenOrNotParse_AndEventsOfOtherKinds_AreLeftOut()
    {
        var note = await CreateNoteAsync("Kept");
        Guid a = SeedRun(), b = SeedRun();
        await SealCandidate(a, "good", [note], "overlap", DreamAdviceKind.Overlap, 70);
        await SealCandidate(b, "good", [note], "overlap", DreamAdviceKind.Overlap, 70);
        await SealEvent(a, "info", new DreamEventPayload("not a candidate", [], null));
        await SealEvent(a, "candidate", new DreamEventPayload("a candidate event with no data", [], null));
        await SealEvent(a, "candidate", new DreamEventPayload("a candidate event with nonsense data", [], new JsonObject { ["candidateKey"] = 5, ["outcome"] = "perhaps" }));
        using (var conn = ChatDb())
            conn.Execute(
                @"INSERT INTO dream_run_event (id, seq, run_id, at, kind, payload_ciphertext, payload_iv, payload_key_v)
                  VALUES (@id, @seq, @run, @at, 'candidate', @junk, @iv, 1)",
                new { id = Guid.NewGuid().ToString("D"), seq = ++_seq, run = a.ToString("D"), at = DateTime.UtcNow.ToString("o"), junk = RandomNumberGenerator.GetBytes(40), iv = RandomNumberGenerator.GetBytes(12) });

        var (status, body) = await CompareAsync(a, b);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("candidateKey").GetString()).Should().Equal("good");
    }

    [Fact]
    public async Task ARunThatExaminedCandidatesButHasNoJournalAnyMore_IsNamedInRunsWithoutLog()
    {
        var note = await CreateNoteAsync("Still here");
        Guid a = SeedRun(examined: 4), b = SeedRun(examined: 4), empty = SeedRun(examined: 0);
        await SealCandidate(a, "k", [note], "overlap", DreamAdviceKind.Overlap, 70);

        var (_, withGone) = await CompareAsync(a, b);
        var (_, noneExamined) = await CompareAsync(a, empty);

        withGone.GetProperty("runsWithoutLog").EnumerateArray().Select(x => x.GetGuid()).Should().Equal([b], "its journal was pruned (seven days) though it looked at four candidates");
        noneExamined.GetProperty("runsWithoutLog").GetArrayLength().Should().Be(0, "a run that examined nothing has nothing missing");
    }

    // ── What is refused ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRunsParameter_Must_NameTwoOrThreeDifferentKnownTrialRuns()
    {
        Guid a = SeedRun(), b = SeedRun(), c = SeedRun(), d = SeedRun(), normal = SeedRun(mode: "normal");

        (await CompareAsync()).Status.Should().Be(HttpStatusCode.BadRequest, "no runs");
        (await CompareAsync(a)).Status.Should().Be(HttpStatusCode.BadRequest, "one run is nothing to compare");
        (await CompareAsync(a, b, c, d)).Status.Should().Be(HttpStatusCode.BadRequest, "four columns are a wall");
        (await CompareAsync(a, a)).Status.Should().Be(HttpStatusCode.BadRequest, "the same run twice");
        (await CompareAsync(a, "not-a-guid")).Status.Should().Be(HttpStatusCode.BadRequest);
        var (unknownStatus, unknown) = await CompareAsync(a, Guid.NewGuid());
        unknownStatus.Should().Be(HttpStatusCode.NotFound);
        unknown.GetProperty("code").GetString().Should().Be(DreamErrorCodes.NotFound);
        var (normalStatus, refused) = await CompareAsync(a, normal);
        normalStatus.Should().Be(HttpStatusCode.BadRequest, "a normal run skips what the registry holds: not the same candidates");
        refused.GetProperty("code").GetString().Should().Be(DreamErrorCodes.InvalidRequest);
        (await CompareAsync(a, b, c)).Status.Should().Be(HttpStatusCode.OK, "three runs is the most");
    }

    [Fact]
    public async Task WhileTheVaultIsLocked_TheTableAnswers409Locked_BecauseTheJournalIsSealed()
    {
        var note = await CreateNoteAsync("Sealed away");
        Guid a = SeedRun(), b = SeedRun();
        await SealCandidate(a, "k", [note], "overlap", DreamAdviceKind.Overlap, 70);
        await SealCandidate(b, "k", [note], "overlap", DreamAdviceKind.Overlap, 70);
        Session.Lock();

        var (status, body) = await CompareAsync(a, b);

        status.Should().Be(HttpStatusCode.Conflict);
        body.GetProperty("code").GetString().Should().Be(DreamErrorCodes.Locked);
    }

    [Fact]
    public async Task TheTable_IsNotForAnOrdinaryUser()
    {
        Guid a = SeedRun(), b = SeedRun();
        using var user = _factory.Server.CreateClient();
        user.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        user.DefaultRequestHeaders.Add("X-User-Role", BeeMemoryBank.Core.Models.UserRoles.User);
        user.DefaultRequestHeaders.Add("X-User-Id", "999");

        (await user.GetAsync($"/api/dream/compare?runs={a},{b}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
