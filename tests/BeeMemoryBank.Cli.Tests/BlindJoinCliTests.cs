using System.Net;
using BeeMemoryBank.Cli.Commands;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// `bmb blind join` against a scripted node (release 2.7): the command only asks the running node over its local API, so these tests script that API and look at what the command
/// asks, what it prints and which code it returns. The waiting loop is driven without waiting (an injected delay). The one thing every test of output watches: the invitation, which is a
/// bearer secret, is never printed.
/// </summary>
public class BlindJoinCliTests
{
    private const string Secret = "SECRETSECRETSECRETSECRETSECRETSECRETSECRET0";
    private const string Invitation = "bmb-invite:?v=1&a=https%3A%2F%2Fhub.example.org&x=" + Secret + "&g=SIGNATURE";

    private const string PreviewJson = """
        {"sourceName":"Alex","address":"https://hub.example.org","trust":"public-ca","expiresAt":"2026-10-21T12:00:00Z",
         "validity":"This invitation works for about 29 minutes more.","limits":"None: a server node keeps what the full node sends it, with no limit of its own."}
        """;

    private const string StartedJson = """
        {"state":"awaiting","matchCode":"K7F-2QX","sourceName":"Alex","address":"https://hub.example.org","confirmBy":"2026-10-24T11:30:00Z",
         "message":"Waiting for Alex to confirm. Read this code out to them: K7F-2QX"}
        """;

    private static string ViewJson(string state, string message) =>
        $$"""{"state":"{{state}}","matchCode":"K7F-2QX","sourceName":"Alex","address":"https://hub.example.org","message":"{{message}}"}""";

    /// <summary>The node: a script of answers per route and method, and a record of every call (body included).</summary>
    private sealed class Node : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body)> Calls { get; } = [];
        public (int, string) Preview { get; set; } = (200, PreviewJson);
        public (int, string) Start { get; set; } = (200, StartedJson);
        public Queue<(int, string)> Status { get; } = new();
        public (int, string) Cancel { get; set; } = (200, ViewJson("none", "The join was given up."));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((request.Method.Method, path, body));
            var (status, text) = (request.Method.Method, path) switch
            {
                ("POST", "/api/blind/join/preview") => Preview,
                ("POST", "/api/blind/join") => Start,
                ("DELETE", "/api/blind/join") => Cancel,
                ("GET", "/api/blind/join") => Status.Count > 1 ? Status.Dequeue() : Status.Count == 1 ? Status.Peek() : (200, ViewJson("awaiting", "Waiting for Alex to confirm. Read this code out to them: K7F-2QX")),
                _ => (404, ""),
            };
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text) };
        }
    }

    private static BlindApiOptions Options(Node node) => new() { BaseUrl = "http://node", InternalKey = "test-key", Handler = node };

    private static Task NoWait(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static async Task<(int Code, string Output)> RunAsync(Node node, string? invitation, BlindJoinCommand.JoinOptions options, string stdin = "", Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var output = new StringWriter();
        var code = await BlindJoinCommand.HandleJoinAsync("unused", invitation, options, Options(node), output, new StringReader(stdin), delay ?? NoWait);
        return (code, output.ToString());
    }

    [Fact]
    public async Task It_previews_asks_and_does_nothing_when_the_answer_is_no()
    {
        var node = new Node();

        var (code, output) = await RunAsync(node, Invitation, new(), stdin: "n\n");

        code.Should().Be(1);
        output.Should().Contain("Invitation from:  Alex").And.Contain("https://hub.example.org").And.Contain("works for about 29 minutes").And.Contain("Limits:").And.Contain("None");
        output.Should().Contain("Join this full node? [y/N]").And.Contain("Nothing was done.");
        node.Calls.Select(c => c.Method + " " + c.Path).Should().Equal("POST /api/blind/join/preview");
    }

    [Theory]
    [InlineData("y")]
    [InlineData("YES")]
    public async Task It_joins_after_a_yes_prints_the_match_code_and_waits_for_the_answer(string answer)
    {
        var node = new Node();
        node.Status.Enqueue((200, ViewJson("awaiting", "Waiting for Alex to confirm. Read this code out to them: K7F-2QX")));
        node.Status.Enqueue((200, ViewJson("confirmed", "Alex confirmed this node and sent it the data. It is attached now.")));

        var (code, output) = await RunAsync(node, Invitation, new(), stdin: answer + "\n");

        code.Should().Be(0);
        output.Should().Contain("Match code:  K7F-2QX").And.Contain("Read this code out to Alex").And.Contain("They have until 2026-10-24T11:30:00Z to answer.");
        output.Should().Contain("Waiting for the owner's answer").And.Contain("Alex confirmed this node and sent it the data. It is attached now.");
        node.Calls.Select(c => c.Method + " " + c.Path).Should().Equal(
            "POST /api/blind/join/preview", "POST /api/blind/join", "GET /api/blind/join", "GET /api/blind/join");
    }

    [Fact]
    public async Task The_invitation_goes_to_the_node_in_the_body_and_is_never_printed()
    {
        var node = new Node();
        node.Status.Enqueue((200, ViewJson("refused", "Alex refused this node. Ask for a new invitation if this was a mistake.")));

        var (code, output) = await RunAsync(node, Invitation, new(Yes: true));

        code.Should().Be(1, "a refusal is not a success");
        node.Calls[0].Body.Should().Contain(Secret, "the node is told the invitation, in the body of a local request");
        output.Should().NotContain(Secret).And.NotContain("bmb-invite").And.NotContain("SIGNATURE");
        output.Should().Contain("Alex refused this node.");
    }

    [Fact]
    public async Task With_yes_it_does_not_ask()
    {
        var node = new Node();

        var (code, output) = await RunAsync(node, Invitation, new(Yes: true, NoWait: true));

        code.Should().Be(0);
        output.Should().NotContain("[y/N]").And.Contain("Match code:  K7F-2QX").And.Contain("bmb blind join --status");
        node.Calls.Select(c => c.Method + " " + c.Path).Should().Equal("POST /api/blind/join/preview", "POST /api/blind/join");
    }

    [Fact]
    public async Task An_invitation_from_standard_input_needs_yes_because_nobody_can_be_asked()
    {
        var node = new Node();

        var (refused, output) = await RunAsync(node, "-", new(), stdin: Invitation + "\n");
        refused.Should().Be(1);
        output.Should().Contain("Run it again with --yes");
        node.Calls.Should().ContainSingle("only the preview was asked");

        var joined = new Node();
        var (code, _) = await RunAsync(joined, "-", new(Yes: true, NoWait: true), stdin: Invitation + "\n");
        code.Should().Be(0);
        joined.Calls[0].Body.Should().Contain(Secret);
    }

    [Fact]
    public async Task No_invitation_is_asked_for_and_nothing_is_called()
    {
        var node = new Node();

        var (code, output) = await RunAsync(node, null, new());

        code.Should().Be(1);
        output.Should().Contain("Give the invitation");
        node.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(400, "This invitation has expired. Ask for a new one.")]
    [InlineData(409, "This node is already attached to a full node: remove it there first.")]
    public async Task A_refusal_of_the_node_is_printed_as_its_own_sentence_not_as_a_raw_body(int status, string sentence)
    {
        var node = new Node { Preview = (status, $$"""{"error":"{{sentence}}"}""") };

        var (code, output) = await RunAsync(node, Invitation, new(Yes: true));

        code.Should().Be(1);
        output.Trim().Should().Be(sentence);
        node.Calls.Should().ContainSingle("a refused preview ends the command");
    }

    [Fact]
    public async Task A_refusal_of_the_join_itself_is_printed_as_its_sentence()
    {
        var node = new Node { Start = (502, """{"error":"The owner's Bee Memory Bank is too old to take this node's invitation: upgrade the full node first (2.7 or later), or pair with a code the old way."}""") };

        var (code, output) = await RunAsync(node, Invitation, new(Yes: true));

        code.Should().Be(1);
        output.Should().Contain("upgrade the full node first").And.NotContain("Match code");
    }

    [Fact]
    public async Task A_node_older_than_2_7_is_told_as_such()
    {
        var node = new Node { Preview = (404, "") };

        var (code, output) = await RunAsync(node, Invitation, new(Yes: true));

        code.Should().Be(1);
        output.Should().Contain("older than 2.7");
    }

    [Theory]
    [InlineData("expired", "The invitation ran out before the owner confirmed it. Ask for a new one.")]
    [InlineData("none", "No join was made on this node.")]
    public async Task A_join_that_ends_without_a_confirmation_returns_one_and_says_how(string state, string message)
    {
        var node = new Node();
        node.Status.Enqueue((200, ViewJson(state, message)));

        var (code, output) = await RunAsync(node, Invitation, new(Yes: true));

        code.Should().Be(1);
        output.Should().Contain(message);
    }

    [Fact]
    public async Task The_wait_backs_off_from_five_seconds_to_at_most_thirty()
    {
        var node = new Node();
        for (var i = 0; i < 8; i++) node.Status.Enqueue((200, ViewJson("awaiting", "Waiting for Alex to confirm. Read this code out to them: K7F-2QX")));
        node.Status.Enqueue((200, ViewJson("confirmed", "Alex confirmed this node and sent it the data. It is attached now.")));
        var waits = new List<double>();

        var (code, _) = await RunAsync(node, Invitation, new(Yes: true), delay: (span, _) => { waits.Add(span.TotalSeconds); return Task.CompletedTask; });

        code.Should().Be(0);
        waits[0].Should().Be(5);
        waits.Should().BeInAscendingOrder().And.OnlyContain(s => s <= 30);
        waits.Max().Should().Be(30);
    }

    [Fact]
    public async Task A_message_of_the_node_while_waiting_is_printed_once_not_every_time()
    {
        var node = new Node();
        var unreachable = ViewJson("awaiting", "Could not reach hub.example.org. Try again in a moment.");
        node.Status.Enqueue((200, unreachable));
        node.Status.Enqueue((200, unreachable));
        node.Status.Enqueue((200, unreachable));
        node.Status.Enqueue((200, ViewJson("confirmed", "Alex confirmed this node and sent it the data. It is attached now.")));

        var (_, output) = await RunAsync(node, Invitation, new(Yes: true));

        System.Text.RegularExpressions.Regex.Matches(output, "Could not reach hub.example.org").Count.Should().Be(1);
    }

    [Fact]
    public async Task A_local_node_that_stops_answering_ends_the_wait_but_not_the_join()
    {
        var node = new Node();
        node.Status.Enqueue((503, "busy"));

        var (code, output) = await RunAsync(node, Invitation, new(Yes: true));

        code.Should().Be(1);
        output.Should().Contain("The join goes on in the node").And.Contain("bmb blind join --status");
        node.Calls.Count(c => c.Method == "GET").Should().Be(6);
    }

    [Fact]
    public async Task Ctrl_C_stops_the_wait_and_says_the_join_goes_on()
    {
        var node = new Node();

        var (code, output) = await RunAsync(node, Invitation, new(Yes: true), delay: (_, token) => throw new OperationCanceledException(token));

        code.Should().Be(1);
        output.Should().Contain("Stopped waiting").And.Contain("goes on in the node");
        node.Calls.Select(c => c.Method).Should().NotContain("DELETE", "stopping to wait does not give the join up");
    }

    [Fact]
    public async Task Status_and_cancel_ask_the_node_and_need_no_invitation()
    {
        var node = new Node();
        node.Status.Enqueue((200, ViewJson("awaiting", "Waiting for Alex to confirm. Read this code out to them: K7F-2QX")));

        var (status, statusOut) = await RunAsync(node, null, new(StatusOnly: true));
        var (cancel, cancelOut) = await RunAsync(node, null, new(Cancel: true));

        status.Should().Be(0);
        statusOut.Should().Contain("Waiting for Alex to confirm. Read this code out to them: K7F-2QX");
        cancel.Should().Be(0);
        cancelOut.Should().Contain("The join was given up.");
        node.Calls.Select(c => c.Method + " " + c.Path).Should().Equal("GET /api/blind/join", "DELETE /api/blind/join");
    }

    [Fact]
    public async Task A_join_whose_answer_has_not_arrived_is_shown_as_such_and_is_not_an_ending()
    {
        // The node sent its request and the answer was lost: it keeps the join (state "redeeming") and says what to do. The command neither ends nor fails on it.
        var pending = "This node sent its request to hub.example.org and has not had the answer yet. Run the same command again with the same invitation, or give the join up with: bmb blind join --cancel";
        var node = new Node();
        node.Status.Enqueue((200, ViewJson("redeeming", pending)));

        var (status, statusOut) = await RunAsync(node, null, new(StatusOnly: true));

        status.Should().Be(0);
        statusOut.Should().Contain("has not had the answer yet").And.Contain("bmb blind join --cancel");

        var waiting = new Node();
        waiting.Status.Enqueue((200, ViewJson("redeeming", pending)));
        waiting.Status.Enqueue((200, ViewJson("redeeming", pending)));
        waiting.Status.Enqueue((200, ViewJson("confirmed", "Alex confirmed this node and sent it the data. It is attached now.")));

        var (code, output) = await RunAsync(waiting, Invitation, new(Yes: true));

        code.Should().Be(0);
        System.Text.RegularExpressions.Regex.Matches(output, "has not had the answer yet").Count.Should().Be(1, "said once while it lasts");
        output.Should().Contain("It is attached now.");
    }

    [Fact]
    public async Task The_status_of_a_refused_or_expired_join_returns_one()
    {
        var node = new Node();
        node.Status.Enqueue((200, ViewJson("refused", "Alex refused this node. Ask for a new invitation if this was a mistake.")));

        var (code, _) = await RunAsync(node, null, new(StatusOnly: true));

        code.Should().Be(1);
    }

    [Fact]
    public async Task Every_call_carries_the_nodes_internal_key_and_the_local_administrators_role()
    {
        var calls = new List<(string? Key, string? Role)>();
        var handler = new RecordingHandler(calls);

        await BlindJoinCommand.HandleJoinAsync("unused", null, new(StatusOnly: true), new BlindApiOptions { BaseUrl = "http://node", InternalKey = "test-key", Handler = handler }, new StringWriter());

        calls.Should().ContainSingle().Which.Should().Be(("test-key", "superadmin"));
    }

    private sealed class RecordingHandler(List<(string? Key, string? Role)> calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            calls.Add((request.Headers.GetValues("X-Internal-Key").Single(), request.Headers.GetValues("X-User-Role").Single()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ViewJson("none", "No join was made on this node.")) });
        }
    }
}
