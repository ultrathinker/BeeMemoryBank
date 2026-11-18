using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// <c>bmb user remove-superadmin</c> and <c>bmb offboarding status|continue</c> (release 2.12.0, plan WS-E): what the CLI sends to the running node, that
/// the password comes from stdin or a prompt and never from an argument, that nothing is sent when the command is incomplete, and that a refusal is
/// the node's own sentence. The node is a fake handler here; the API behind these routes is proven by the Integration suites of the removal.
/// </summary>
[Collection("OffboardingCli")]
public sealed class OffboardingCommandTests : IDisposable
{
    private static readonly Guid ProcessId = Guid.Parse("6f1b7a52-3d43-4b0e-9a8e-2b1d0c1e5a77");
    private readonly FakeNode _node = new();

    public OffboardingCommandTests() => OffboardingCommand.ClientOverride = () => new HttpClient(_node) { BaseAddress = new Uri("http://node.test") };

    public void Dispose() => OffboardingCommand.ClientOverride = null;

    private static string View(bool open = true, bool needsPassword = false, string status = "being removed, step 2 of 6", int undecided = 0) =>
        JsonSerializer.Serialize(new
        {
            processId = ProcessId, person = "Lee Leaver (leaver)", status, open, needsPassword,
            steps = new object[]
            {
                new { number = 0, title = "Lock out", state = "done", sentence = "Signed out everywhere.", waits = Array.Empty<object>() },
                new
                {
                    number = 1, title = "New identity keys", state = "waiting", sentence = "Waiting for the nodes.",
                    waits = new object[] { new { nodeName = "Shared server", state = "waiting", why = "It may be offline. Wait for it, or revoke it." } },
                },
            },
            review = Enumerable.Range(0, undecided).Select(i => new { id = $"device:{i}", decision = (string?)null }).ToArray(),
            bridges = Array.Empty<string>(),
            reminders = Array.Empty<string>(),
        });

    private const string Users = """[{"id":1,"username":"admin"},{"id":7,"username":"leaver"}]""";

    private async Task<(int Exit, string Out, string Err)> RemoveAsync(string name, string? demoteTo, bool delete, string? actingUser, string stdin,
        bool openRole = false, string[]? folders = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await OffboardingCommand.RemoveSuperadminAsync("unused", name, demoteTo, delete, actingUser, openRole, folders, passwordStdin: true,
            new StringReader(stdin), inputRedirected: true, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task RemoveSuperadmin_Delete_SendsTheTargetAndTheAdministratorsPasswordFromStdin()
    {
        _node.On("GET /api/users", HttpStatusCode.OK, Users);
        _node.On("POST /api/offboarding", HttpStatusCode.Accepted, View());

        var (exit, output, _) = await RemoveAsync("Leaver", null, delete: true, "admin", "Admin-Password-1\n");

        exit.Should().Be(0);
        var body = _node.BodyOf("POST /api/offboarding");
        body.GetProperty("userId").GetInt32().Should().Be(7, "the name is resolved to the account");
        body.GetProperty("target").GetString().Should().Be("delete");
        body.GetProperty("password").GetString().Should().Be("Admin-Password-1");
        body.GetProperty("actingUser").GetString().Should().Be("admin");
        output.Should().Contain("The removal has started").And.Contain("being removed, step 2 of 6").And.Contain($"bmb offboarding status {ProcessId}");
        output.Should().NotContain("Admin-Password-1");
    }

    [Fact]
    public async Task RemoveSuperadmin_Demote_SendsTheRoleTheFoldersAndTheOpenRoleChoice()
    {
        _node.On("GET /api/users", HttpStatusCode.OK, Users);
        _node.On("POST /api/offboarding", HttpStatusCode.Accepted, View());
        var folder = Guid.NewGuid();

        var (exit, _, _) = await RemoveAsync("leaver", "user", delete: false, "admin", "pw\n", openRole: false, folders: [$"{folder}:ro"]);

        exit.Should().Be(0);
        var body = _node.BodyOf("POST /api/offboarding");
        body.GetProperty("target").GetString().Should().Be("demote");
        body.GetProperty("role").GetString().Should().Be("user");
        body.GetProperty("allowOpenRole").GetBoolean().Should().BeFalse();
        var rule = body.GetProperty("folderRules")[0];
        rule.GetProperty("folderId").GetGuid().Should().Be(folder);
        rule.GetProperty("effect").GetString().Should().Be("allow");
        rule.GetProperty("isReadOnly").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(null, false, "admin", "--delete")]          // neither end
    [InlineData("user", true, "admin", "--delete")]         // both ends
    [InlineData(null, true, null, "--as")]                  // nobody acts
    public async Task RemoveSuperadmin_AnIncompleteCommand_SendsNothing(string? demoteTo, bool delete, string? actingUser, string named)
    {
        var (exit, _, error) = await RemoveAsync("leaver", demoteTo, delete, actingUser, "pw\n");

        exit.Should().Be(1);
        error.Should().Contain(named);
        _node.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveSuperadmin_ARefusal_IsTheNodesOwnSentence()
    {
        _node.On("GET /api/users", HttpStatusCode.OK, Users);
        _node.On("POST /api/offboarding", HttpStatusCode.Conflict, """{"error":"Cannot remove the last superadmin."}""");

        var (exit, _, error) = await RemoveAsync("leaver", null, delete: true, "admin", "pw\n");

        exit.Should().Be(1);
        error.Trim().Should().Be("Cannot remove the last superadmin.");
    }

    [Fact]
    public async Task RemoveSuperadmin_AnUnknownName_IsRefusedBeforeThePasswordIsAskedFor()
    {
        _node.On("GET /api/users", HttpStatusCode.OK, Users);

        var (exit, _, error) = await RemoveAsync("nobody", null, delete: true, "admin", "");

        exit.Should().Be(1);
        error.Should().Contain("no user named 'nobody'");
        _node.Requests.Should().ContainSingle("only the list of users was read");
    }

    [Fact]
    public async Task Status_ShowsTheStepsAndWhatTheyWaitFor()
    {
        _node.On($"GET /api/offboarding/{ProcessId:D}", HttpStatusCode.OK, View(undecided: 2));
        var output = new StringWriter();

        (await OffboardingCommand.StatusAsync("unused", ProcessId.ToString(), output, new StringWriter())).Should().Be(0);

        var text = output.ToString();
        text.Should().Contain("Lee Leaver (leaver): being removed, step 2 of 6");
        text.Should().Contain("[waiting] 2. New identity keys").And.Contain("Shared server: waiting - It may be offline. Wait for it, or revoke it.");
        text.Should().Contain("Review: 2 item(s), 2 undecided").And.Contain("--accept-review");
    }

    [Fact]
    public async Task Status_WithNoRemoval_SaysSo()
    {
        _node.On("GET /api/offboarding", HttpStatusCode.OK, "[]");
        var output = new StringWriter();

        (await OffboardingCommand.StatusAsync("unused", null, output, new StringWriter())).Should().Be(0);

        output.ToString().Should().Contain("Nobody is being removed");
    }

    [Fact]
    public async Task Continue_GivesThePasswordOnlyWhenTheMasterKeyChangeWaitsForIt()
    {
        _node.On($"GET /api/offboarding/{ProcessId:D}", HttpStatusCode.OK, View(needsPassword: true, status: "being removed, step 5 of 6"));
        _node.On($"POST /api/offboarding/{ProcessId:D}/continue", HttpStatusCode.OK, View(open: false, status: "deleted"));
        var output = new StringWriter();

        var exit = await OffboardingCommand.ContinueAsync("unused", ProcessId.ToString(), "admin", acceptReview: false, passwordStdin: true,
            new StringReader("Admin-Password-1\n"), inputRedirected: true, output, new StringWriter());

        exit.Should().Be(0);
        var body = _node.BodyOf($"POST /api/offboarding/{ProcessId:D}/continue");
        body.GetProperty("password").GetString().Should().Be("Admin-Password-1");
        body.GetProperty("actingUser").GetString().Should().Be("admin");
        output.ToString().Should().Contain("deleted");
    }

    [Fact]
    public async Task Continue_WithoutAPasswordNeeded_SendsNone()
    {
        _node.On($"GET /api/offboarding/{ProcessId:D}", HttpStatusCode.OK, View());
        _node.On($"POST /api/offboarding/{ProcessId:D}/continue", HttpStatusCode.OK, View());

        var exit = await OffboardingCommand.ContinueAsync("unused", ProcessId.ToString(), null, acceptReview: false, passwordStdin: true,
            new StringReader(""), inputRedirected: true, new StringWriter(), new StringWriter());

        exit.Should().Be(0);
        _node.BodyOf($"POST /api/offboarding/{ProcessId:D}/continue").TryGetProperty("password", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Continue_AcceptReview_TakesThePreselectedChoicesFirst()
    {
        _node.On($"GET /api/offboarding/{ProcessId:D}", HttpStatusCode.OK, View(undecided: 3));
        _node.On($"POST /api/offboarding/{ProcessId:D}/review", HttpStatusCode.OK, View());
        _node.On($"POST /api/offboarding/{ProcessId:D}/continue", HttpStatusCode.OK, View());

        var exit = await OffboardingCommand.ContinueAsync("unused", ProcessId.ToString(), "admin", acceptReview: true, passwordStdin: true,
            new StringReader(""), inputRedirected: true, new StringWriter(), new StringWriter());

        exit.Should().Be(0);
        _node.Requests.Select(r => r.Key).Should().Equal(
            $"GET /api/offboarding/{ProcessId:D}", $"POST /api/offboarding/{ProcessId:D}/review", $"POST /api/offboarding/{ProcessId:D}/continue");
        var review = _node.BodyOf($"POST /api/offboarding/{ProcessId:D}/review");
        review.GetProperty("acceptDefaults").GetBoolean().Should().BeTrue();
        review.GetProperty("actingUser").GetString().Should().Be("admin");
    }

    // --accept-review never decides a superadmin; --keep names one, and the status says which wait for that.
    [Fact]
    public async Task Continue_Keep_DecidesNamedItems_AndStatusNamesTheSuperadminsLeftOpen()
    {
        var withSuperadmin = JsonSerializer.Serialize(new
        {
            processId = ProcessId, person = "Lee Leaver (leaver)", status = "being removed, step 4 of 6", open = true, needsPassword = false,
            steps = Array.Empty<object>(),
            review = new object[] { new { id = "superadmin:12", kind = "superadmin", label = "Old Puppet (puppet)", defaultRemove = false, decision = (string?)null } },
            bridges = Array.Empty<string>(), reminders = Array.Empty<string>(),
        });
        _node.On($"GET /api/offboarding/{ProcessId:D}", HttpStatusCode.OK, withSuperadmin);
        _node.On($"POST /api/offboarding/{ProcessId:D}/review", HttpStatusCode.OK, View());
        _node.On($"POST /api/offboarding/{ProcessId:D}/continue", HttpStatusCode.OK, View());

        var status = new StringWriter();
        (await OffboardingCommand.StatusAsync("unused", ProcessId.ToString(), status, new StringWriter())).Should().Be(0);
        status.ToString().Should().Contain("Old Puppet (puppet): decide it yourself - keep it with --keep superadmin:12");

        var exit = await OffboardingCommand.ContinueAsync("unused", ProcessId.ToString(), "admin", acceptReview: true, passwordStdin: true,
            new StringReader(""), inputRedirected: true, new StringWriter(), new StringWriter(), keep: ["superadmin:12"]);
        exit.Should().Be(0);
        var review = _node.BodyOf($"POST /api/offboarding/{ProcessId:D}/review");
        review.GetProperty("acceptDefaults").GetBoolean().Should().BeTrue();
        var decision = review.GetProperty("decisions").EnumerateArray().Single();
        (decision.GetProperty("itemId").GetString(), decision.GetProperty("decision").GetString()).Should().Be(("superadmin:12", "keep"));
    }

    /// <summary>A node that answers canned bodies per "METHOD /path" and records what it was sent.</summary>
    private sealed class FakeNode : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new();
        public List<KeyValuePair<string, string?>> Requests { get; } = [];

        public void On(string key, HttpStatusCode status, string body) => _answers[key] = (status, body);

        public JsonElement BodyOf(string key) => JsonDocument.Parse(Requests.Last(r => r.Key == key).Value!).RootElement;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            Requests.Add(new(key, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (!_answers.TryGetValue(key, out var answer)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") };
        }
    }
}
