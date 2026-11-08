using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>One network this computer is on, as the node reports it (<c>lanSync.networks[]</c> of <c>GET /node/lan</c>).</summary>
/// <param name="Fingerprint">The key of the person's answer; null while the node does not know it yet.</param>
/// <param name="Reason">Why the network is or is not trusted: <c>unknown</c>, <c>trusted</c>, <c>refused</c>, <c>pending</c>, <c>vpn-off</c>,
/// <c>virtual</c>, or <c>windows-private</c>, <c>windows-domain</c>, <c>windows-public</c> (Windows decides those by its network category).</param>
/// <param name="CanAsk">The node says the person may be asked about it.</param>
/// <param name="Kind"><c>lan</c>, <c>vpn</c> or <c>virtual</c>.</param>
public sealed record LanNetworkEntry(
    string Interface, string Address, string Label, string? Fingerprint, bool Trusted, string Reason, bool CanAsk, string Kind);

/// <summary>The sync part of the node's LAN status (the Api's <c>LanSyncStatus</c>), only what the shell uses.</summary>
/// <param name="Setting"><c>on</c>, <c>off</c> or <c>environment</c>.</param>
/// <param name="NoticePending">The one-time notice "Direct sync with your devices on this network is on" has not been dismissed yet.</param>
public sealed record LanSyncAnswer(string Setting, bool NoticePending, IReadOnlyList<LanNetworkEntry> Networks)
{
    public const string On = "on";
    public const string Off = "off";
}

/// <summary>What one poll of <c>GET /node/lan</c> brought.</summary>
/// <param name="Answer">The sync status, when the node answered with one.</param>
/// <param name="Detail">Why there is none; null when there is one.</param>
public sealed record NodeLanPoll(LanSyncAnswer? Answer, string? Detail = null)
{
    public bool Answered => Answer is not null;
}

/// <summary>
/// What <see cref="LanNetworkPrompt"/> needs of its node: the sync status, the person's answer about a network, and the dismissal of the
/// one-time notice. <see cref="NodeLanRequest"/> is the real one; the tests have their own.
/// </summary>
public interface ILanNode
{
    Task<NodeLanPoll> PollAsync(CancellationToken cancellationToken);

    /// <summary>Posts "trust this network" (<paramref name="trusted"/> true) or "not this network" (false); true when the node took it.</summary>
    Task<bool> AnswerNetworkAsync(string fingerprint, bool trusted, CancellationToken cancellationToken);

    /// <summary>Posts that the one-time notice was read; true when the node took it.</summary>
    Task<bool> DismissNoticeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The shell's calls to its own node about LAN sync: <c>GET /node/lan</c> (which networks this computer is on and whether the node syncs
/// there), <c>POST /node/lan/networks</c> (the person's answer, the node rebinds at once) and <c>POST /node/lan/notice/dismiss</c>. The
/// same request as <see cref="NodeAlarmsRequest"/> in every respect that matters for the key: the node's internal key in
/// <c>X-Internal-Key</c>, sent only to a loopback address over a connection with no proxy and no redirects (<see cref="NodeFrontClient"/>),
/// never in a returned or logged text. A node this app did not start has no key here, and then nothing is sent.
///
/// <para>Polled every minute, so a reason is logged only when it differs from the last one logged.</para>
/// </summary>
public sealed class NodeLanRequest : ILanNode, IDisposable
{
    public const string Route = "/node/lan";
    public const string NetworksRoute = "/node/lan/networks";
    public const string DismissRoute = "/node/lan/notice/dismiss";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Func<string?> _frontUrl;
    private readonly Func<string?> _internalKey;
    private readonly NodeFrontClient _client;
    private readonly Action<string> _log;
    private string? _lastLogged;

    /// <param name="frontUrl">The open node's address, read at each call; null or empty when no node is open.</param>
    /// <param name="internalKey">The key of the node THIS app started, read at each call; null or empty when it has none for the open node.</param>
    /// <param name="handler">For tests: the handler the request goes through; null means a real connection.</param>
    /// <param name="timeout">For tests: how long to wait for the node; null means <see cref="NodeFrontClient.DefaultTimeout"/>.</param>
    /// <param name="log">Where the one-line reasons go; null means the standard error stream.</param>
    public NodeLanRequest(Func<string?> frontUrl, Func<string?> internalKey, HttpMessageHandler? handler = null, TimeSpan? timeout = null, Action<string>? log = null)
    {
        _frontUrl = frontUrl ?? throw new ArgumentNullException(nameof(frontUrl));
        _internalKey = internalKey ?? throw new ArgumentNullException(nameof(internalKey));
        _client = new NodeFrontClient(handler, timeout);
        _log = log ?? Console.Error.WriteLine;
    }

    /// <summary>Polls once. Never throws; a poll without a status says why.</summary>
    public async Task<NodeLanPoll> PollAsync(CancellationToken cancellationToken)
    {
        var (reply, reason) = await SendAsync(HttpMethod.Get, Route, null, "the LAN status poll", cancellationToken).ConfigureAwait(false);
        if (reply is null) return new NodeLanPoll(null, reason);
        var poll = FromAnswer(reply.Status!.Value, reply.Body);
        Remember(poll.Detail, poll.Answered);
        return poll;
    }

    /// <summary>Posts the person's answer about the network with this fingerprint. Never throws.</summary>
    public async Task<bool> AnswerNetworkAsync(string fingerprint, bool trusted, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { fingerprint, trusted }, Json);
        return await PostAsync(NetworksRoute, body, "the answer about a network", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Posts that the one-time notice was read. Never throws.</summary>
    public Task<bool> DismissNoticeAsync(CancellationToken cancellationToken) =>
        PostAsync(DismissRoute, "{}", "the dismissal of the LAN notice", cancellationToken);

    private async Task<bool> PostAsync(string route, string body, string what, CancellationToken cancellationToken)
    {
        var (reply, reason) = await SendAsync(HttpMethod.Post, route, body, what, cancellationToken).ConfigureAwait(false);
        if (reply is null) return false;
        if (reply.IsSuccessStatus) return true;
        // The node says why in {"error": "..."}; its text is its own words and holds no key.
        Remember(StatusDetail(reply.Status!.Value, what), answered: false);
        return false;
    }

    /// <summary>One call to the node. A reply is null when there is none to read; <c>Reason</c> then says why (and is logged once).</summary>
    private async Task<(NodeFrontReply? Reply, string? Reason)> SendAsync(HttpMethod method, string route, string? jsonBody, string what, CancellationToken cancellationToken)
    {
        string? key = null;
        try
        {
            var url = _frontUrl();
            if (string.IsNullOrEmpty(url)) return (null, "No node is open.");

            key = _internalKey();
            if (string.IsNullOrEmpty(key))
                return (null, Remember($"This app did not start the open node, so it has no key to ask it about LAN sync ({what}).", false, key));

            var reply = await _client.SendAsync(method, url, route, key, readBody: method == HttpMethod.Get, cancellationToken: cancellationToken, jsonBody: jsonBody).ConfigureAwait(false);
            var reason = reply.Failure switch
            {
                NodeFrontFailure.None => null,
                NodeFrontFailure.NotLoopback => $"The node is not on this computer, so it was not asked ({what}).",
                NodeFrontFailure.Cancelled => $"The request was cancelled ({what}).",
                NodeFrontFailure.TimedOut => $"The node did not answer within {_client.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds ({what}).",
                NodeFrontFailure.Unreachable => $"The node could not be reached ({what}: {reply.ErrorKind}).",
                _ => $"The request failed ({what}: {reply.ErrorKind}).",
            };
            return reason is null ? (reply, null) : (null, Remember(reason, false, key));
        }
        catch (Exception ex)
        {
            return (null, Remember($"The request failed ({what}: {ex.GetType().Name}).", false, key));
        }
    }

    private static NodeLanPoll FromAnswer(HttpStatusCode status, string? body)
    {
        var code = (int)status;
        if (status is HttpStatusCode.NotImplemented or HttpStatusCode.NotFound)
            return new NodeLanPoll(null, "The node does not report LAN sync (an older version).");
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new NodeLanPoll(null, $"The node refused the LAN status poll: this app is not authorized (HTTP {code}).");
        if (code is < 200 or > 299) return new NodeLanPoll(null, $"The node answered {code} to the LAN status poll.");
        try
        {
            var status2 = JsonSerializer.Deserialize<LanStatusWire>(body ?? "", Json);
            // An older node has no lanSync member at all: nothing to ask about.
            if (status2?.LanSync is not { Setting: not null } sync) return new NodeLanPoll(null, "The node's LAN status has no sync part (an older version).");
            return new NodeLanPoll(new LanSyncAnswer(sync.Setting, sync.NoticePending, sync.Networks ?? []));
        }
        catch (JsonException)
        {
            return new NodeLanPoll(null, "The node's LAN status could not be read.");
        }
    }

    private static string StatusDetail(HttpStatusCode status, string what) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? $"The node refused {what}: this app is not authorized (HTTP {(int)status})."
            : $"The node answered {(int)status} to {what}.";

    /// <summary>Logs a reason once until it changes (or an answer came), with the key taken out. Returns the (scrubbed) reason.</summary>
    private string? Remember(string? reason, bool answered, string? key = null)
    {
        if (reason is not null && !string.IsNullOrEmpty(key)) reason = NodeFrontClient.Scrub(reason, key);
        if (reason is not null && reason != _lastLogged)
        {
            _lastLogged = reason;
            try { _log($"[NodeLanRequest] {reason}"); }
            catch { /* a log that cannot be written is not worth failing a poll for */ }
        }
        else if (answered)
        {
            _lastLogged = null;
        }
        return reason;
    }

    private sealed record LanStatusWire(LanSyncWire? LanSync);

    private sealed record LanSyncWire(string? Setting, bool NoticePending, List<LanNetworkEntry>? Networks);

    public void Dispose() => _client.Dispose();
}
