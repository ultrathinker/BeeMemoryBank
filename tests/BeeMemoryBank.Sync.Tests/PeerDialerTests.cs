using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The dialler (docs/design/lan-peer-sync.md section 7): the LAN candidates are asked at once (staggered), each with a short timeout, the first that
/// answers as THIS peer (node id and key) is taken; a dead address costs about the LAN timeout and no more; a wrong peer is never taken and is noted
/// so the planner leaves it alone for an hour.
/// </summary>
public class PeerDialerTests : SyncTestFixture
{
    private readonly WhitelistEntry _peer;
    private readonly byte[] _peerKey;

    public PeerDialerTests()
    {
        var (pub, _) = Ed25519Signer.GenerateKeyPair();
        _peerKey = pub;
        _peer = new WhitelistEntry { NodeId = Guid.NewGuid(), DisplayName = "Desktop", Ed25519PublicKey = pub, Status = "A" };
    }

    private static ChannelCandidate Lan(string url) => new(url, ChannelSources.Announced, ChannelScopes.Lan, true, null, null, null);

    private HttpClient Network(Dictionary<string, Func<Task<HttpResponseMessage>>> hosts) => new(new Handler(hosts));

    private Func<Task<HttpResponseMessage>> Identity(Guid node, byte[] key) => () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            nodeId = node, displayName = "x", ed25519PublicKeyB64 = Convert.ToBase64String(key), protocolVersion = SyncProtocolVersion.Current,
        }), Encoding.UTF8, "application/json"),
    });

    private static Func<Task<HttpResponseMessage>> Dead => async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        throw new HttpRequestException("no answer");
    };

    [Fact]
    public async Task TheLanCandidatesAreAskedAtOnce_ADeadOneCostsNoMoreThanTheShortTimeout()
    {
        var store = new PeerChannelStore(Factory);
        var dialer = new PeerDialer(store);
        var plan = new ChannelPlan([], [Lan("https://192.168.1.10:5311"), Lan("https://192.168.1.11:5311")], [], []);
        using var http = Network(new()
        {
            ["192.168.1.10"] = Dead,
            ["192.168.1.11"] = Identity(_peer.NodeId, _peerKey),
        });

        var clock = Stopwatch.StartNew();
        var result = await dialer.DialAsync(http, _peer, plan, CancellationToken.None);

        result.Chosen!.Url.Should().Be("https://192.168.1.11:5311");
        clock.Elapsed.Should().BeLessThan(PeerDialer.LanTimeout, "the second address was started a moment after the first, not after its timeout");
    }

    [Fact]
    public async Task AWrongPeerAtAnAddress_IsNeverTaken_AndIsNotedAsSuch()
    {
        var store = new PeerChannelStore(Factory);
        var dialer = new PeerDialer(store);
        var (otherKey, _) = Ed25519Signer.GenerateKeyPair();
        var plan = new ChannelPlan([], [Lan("https://192.168.1.10:5311"), Lan("https://192.168.1.12:5311")], [], []);
        using var http = Network(new()
        {
            ["192.168.1.10"] = Identity(Guid.NewGuid(), otherKey),     // another node
            ["192.168.1.12"] = Identity(_peer.NodeId, otherKey),       // this node's id with another key
        });

        var result = await dialer.DialAsync(http, _peer, plan, CancellationToken.None);

        result.Chosen.Should().BeNull();
        result.Failure.Should().Match<Exception>(e => e is PeerIdentityMismatchException || e is PeerKeyMismatchException);
        var rows = await store.GetAsync(_peer.NodeId);
        rows.Should().HaveCount(2).And.OnlyContain(r => r.LastFailKind == ChannelFailures.WrongPeer);
    }

    [Fact]
    public async Task ThePeerWithOneAddress_IsNotAskedTwice_TheSessionIsTheAttempt()
    {
        var dialer = new PeerDialer(new PeerChannelStore(Factory));
        var plan = new ChannelPlan([], [], [new ChannelCandidate("https://desktop.example.org", ChannelSources.Primary, ChannelScopes.Internet, true, null, null, null)], []);
        var asked = 0;
        using var http = Network(new() { ["desktop.example.org"] = () => { asked++; return Identity(_peer.NodeId, _peerKey)(); } });

        var result = await dialer.DialAsync(http, _peer, plan, CancellationToken.None);

        result.Chosen!.Url.Should().Be("https://desktop.example.org");
        asked.Should().Be(0);
    }

    [Fact]
    public async Task WhenNothingAnswers_TheFailureSaysWhatHappened_AndEachAddressHasItsRow()
    {
        var store = new PeerChannelStore(Factory);
        var old = PeerDialer.LanTimeout;
        PeerDialer.LanTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var dialer = new PeerDialer(store);
            var plan = new ChannelPlan([], [Lan("https://192.168.1.10:5311"), Lan("https://192.168.1.13:5311")], [], []);
            using var http = Network(new() { ["192.168.1.10"] = Dead, ["192.168.1.13"] = Dead });

            var result = await dialer.DialAsync(http, _peer, plan, CancellationToken.None);

            result.Chosen.Should().BeNull();
            result.Failure.Should().NotBeNull();
            SyncScheduler.IsUnreachable(result.Failure!, CancellationToken.None).Should().BeTrue("the loop treats it as no answer, with its growing pause");
            (await store.GetAsync(_peer.NodeId)).Should().OnlyContain(r => r.LastFailKind == ChannelFailures.NoAnswer);
        }
        finally { PeerDialer.LanTimeout = old; }
    }

    private sealed class Handler(Dictionary<string, Func<Task<HttpResponseMessage>>> hosts) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!hosts.TryGetValue(request.RequestUri!.Host, out var answer)) throw new HttpRequestException("no route");
            var task = answer();
            var done = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, ct));
            if (done != task) throw new TaskCanceledException();
            return await task;
        }
    }
}
