using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The shell's calls to its node about LAN sync: <c>GET /node/lan</c>, <c>POST /node/lan/networks</c>, <c>POST /node/lan/notice/dismiss</c>.
/// The key goes in <c>X-Internal-Key</c> to a loopback address only and is in no text that comes back or is logged; a node this app did not
/// start gets nothing; an answer is read as the node writes it (camelCase) and an older node is not an error.
/// </summary>
public sealed class NodeLanRequestTests
{
    private const string Key = "shell-lan-test-key-0123456789abcdef";
    private const string Front = "http://127.0.0.1:5310";

    private const string StatusJson = """
        {"mode":"permanent","active":true,"token":null,"expiresAt":null,"spkiPin":"abc","port":5311,"firewallRule":false,
         "setting":"on","platform":"macos","firewallManaged":false,
         "lanSync":{"setting":"on","serving":["192.168.1.20"],"port":5311,"portSource":"setting","bindError":null,"noticePending":true,
           "networks":[
             {"interface":"en0","address":"192.168.1.20","label":"en0 (192.168.1.20)","fingerprint":"fp-home","trusted":false,"reason":"unknown","canAsk":true,"kind":"lan"},
             {"interface":"utun3","address":"100.64.0.2","label":"utun3 (100.64.0.2)","fingerprint":null,"trusted":false,"reason":"vpn-off","canAsk":false,"kind":"vpn"}],
           "answers":[],"firewallLegacyRule":false}}
        """;

    private sealed record Seen(string Method, Uri? Uri, string? Key, string? Body, string? ContentType);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<Seen> Requests { get; } = new();

        public Handler(HttpStatusCode status, string body = "") : this(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new Seen(request.Method.Method, request.RequestUri,
                request.Headers.TryGetValues("X-Internal-Key", out var k) ? string.Join(",", k) : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.MediaType));
            return answer(request);
        }
    }

    private readonly ConcurrentQueue<string> _log = new();

    private NodeLanRequest Make(Handler handler, string? front = Front, string? key = Key) => new(() => front, () => key, handler, log: _log.Enqueue);

    [Fact]
    public async Task ThePoll_AsksForTheLanStatus_WithTheKey_AndReadsTheNetworks()
    {
        var handler = new Handler(HttpStatusCode.OK, StatusJson);
        using var request = Make(handler);

        var poll = await request.PollAsync(CancellationToken.None);

        var seen = handler.Requests.Should().ContainSingle().Subject;
        seen.Method.Should().Be("GET");
        seen.Uri.Should().Be(new Uri("http://127.0.0.1:5310/node/lan"));
        seen.Key.Should().Be(Key);
        var answer = poll.Answer!;
        answer.Setting.Should().Be("on");
        answer.NoticePending.Should().BeTrue();
        answer.Networks.Should().HaveCount(2);
        answer.Networks[0].Should().Be(new LanNetworkEntry("en0", "192.168.1.20", "en0 (192.168.1.20)", "fp-home", false, "unknown", true, "lan"));
        answer.Networks[1].Fingerprint.Should().BeNull();
        LanNetworkPrompt.CandidatesOf(answer).Should().ContainSingle().Which.Fingerprint.Should().Be("fp-home");
    }

    [Fact]
    public async Task TheAnswerAboutANetwork_IsPostedAsJson_ToTheNetworksRoute()
    {
        var handler = new Handler(HttpStatusCode.OK, "{}");
        using var request = Make(handler);

        (await request.AnswerNetworkAsync("fp-home", true, CancellationToken.None)).Should().BeTrue();
        (await request.AnswerNetworkAsync("fp-home", false, CancellationToken.None)).Should().BeTrue();

        var seen = handler.Requests.ToArray();
        seen.Select(r => r.Method).Should().OnlyContain(m => m == "POST");
        seen.Select(r => r.Uri!.AbsolutePath).Should().OnlyContain(p => p == "/node/lan/networks");
        seen.Select(r => r.Body).Should().Equal("""{"fingerprint":"fp-home","trusted":true}""", """{"fingerprint":"fp-home","trusted":false}""");
        seen.Select(r => r.ContentType).Should().OnlyContain(t => t == "application/json");
        seen.Select(r => r.Key).Should().OnlyContain(k => k == Key);
    }

    [Fact]
    public async Task TheDismissal_IsPostedToTheNoticeRoute()
    {
        var handler = new Handler(HttpStatusCode.OK, "{}");
        using var request = Make(handler);

        (await request.DismissNoticeAsync(CancellationToken.None)).Should().BeTrue();

        var seen = handler.Requests.Should().ContainSingle().Subject;
        seen.Method.Should().Be("POST");
        seen.Uri!.AbsolutePath.Should().Be("/node/lan/notice/dismiss");
        seen.Key.Should().Be(Key);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnAnswerTheNodeRefuses_IsFalse_AndLoggedWithoutTheKey(HttpStatusCode status)
    {
        var handler = new Handler(status, "{\"error\":\"This computer is not on that network now.\"}");
        using var request = Make(handler);

        (await request.AnswerNetworkAsync("fp-home", true, CancellationToken.None)).Should().BeFalse();

        _log.Should().ContainSingle().Which.Should().Contain(((int)status).ToString()).And.NotContain(Key);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "older version")]
    [InlineData(HttpStatusCode.NotImplemented, "older version")]
    [InlineData(HttpStatusCode.Forbidden, "not authorized")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task APollThatBringsNoStatus_SaysWhy(HttpStatusCode status, string expected)
    {
        using var request = Make(new Handler(status));

        var poll = await request.PollAsync(CancellationToken.None);

        poll.Answered.Should().BeFalse();
        poll.Detail.Should().Contain(expected);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"lanSync":null}""")]
    public async Task AStatusWithoutTheSyncPart_IsAnOlderNode_NotACrash(string body)
    {
        using var request = Make(new Handler(HttpStatusCode.OK, body));

        var poll = await request.PollAsync(CancellationToken.None);

        poll.Answered.Should().BeFalse();
        poll.Detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ANodeThisAppDidNotStart_GetsNothing()
    {
        var handler = new Handler(HttpStatusCode.OK, StatusJson);
        using var request = Make(handler, key: null);

        (await request.PollAsync(CancellationToken.None)).Answered.Should().BeFalse();
        (await request.AnswerNetworkAsync("fp", true, CancellationToken.None)).Should().BeFalse();
        (await request.DismissNoticeAsync(CancellationToken.None)).Should().BeFalse();

        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithNoNodeOpen_NothingIsSent(string? front)
    {
        var handler = new Handler(HttpStatusCode.OK, StatusJson);
        using var request = Make(handler, front);

        (await request.PollAsync(CancellationToken.None)).Answered.Should().BeFalse();

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddressThatIsNotOnThisComputer_GetsNeitherTheRequestNorTheKey()
    {
        var handler = new Handler(HttpStatusCode.OK, StatusJson);
        using var request = Make(handler, "http://192.168.1.50:5310");

        (await request.PollAsync(CancellationToken.None)).Answered.Should().BeFalse();
        (await request.AnswerNetworkAsync("fp", true, CancellationToken.None)).Should().BeFalse();

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailure_IsLoggedOnce_UntilAnAnswerComes_AndNeverHoldsTheKey()
    {
        var failing = true;
        var handler = new Handler(_ => failing
            ? throw new HttpRequestException($"connection to {Key} refused")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StatusJson, Encoding.UTF8) });
        using var request = Make(handler);

        for (var i = 0; i < 3; i++) (await request.PollAsync(CancellationToken.None)).Answered.Should().BeFalse();
        _log.Should().ContainSingle().Which.Should().NotContain(Key);

        failing = false;
        (await request.PollAsync(CancellationToken.None)).Answered.Should().BeTrue();
        failing = true;
        await request.PollAsync(CancellationToken.None);

        _log.Should().HaveCount(2, "after an answer, a failure is news again");
    }
}
