using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>One alarm as the node reports it (the Api's <c>BlindAlarm</c>): kinds, ids, times and protocol numbers; no free text.</summary>
/// <param name="Name">The blind node's display name; null while the vault is locked.</param>
/// <param name="Since">For <c>silent</c>: the last contact (UTC). Null for the protocol kinds.</param>
/// <param name="Notify">The node says a notification is due.</param>
public sealed record BlindAlarmEntry(string Kind, Guid NodeId, string? Name, DateTime? Since, int? Protocol, bool Notify, bool Banner);

/// <summary>The node's alarm report (the Api's <c>BlindAlarmReport</c>).</summary>
/// <param name="State"><c>ok</c> (judged), <c>invisible</c> or <c>warming_up</c> (nothing judged).</param>
public sealed record BlindAlarmsAnswer(string State, bool Locked, IReadOnlyList<BlindAlarmEntry> Alarms)
{
    public const string Judged = "ok";
}

/// <summary>What one poll of <c>GET /node/alarms</c> brought.</summary>
/// <param name="Answer">The report, when the node answered with one.</param>
/// <param name="Detail">Why there is no report; null when there is one.</param>
public sealed record NodeAlarmsPoll(BlindAlarmsAnswer? Answer, string? Detail = null)
{
    public bool Answered => Answer is not null;
}

/// <summary>
/// The shell's poll of its own node: <c>GET /node/alarms</c>, "which blind nodes need attention" (BMB-77). The same request as
/// <see cref="NodeLockRequest"/> in every respect that matters for the key: the node's internal key in <c>X-Internal-Key</c>, sent only
/// to a loopback address over a connection with no proxy and no redirects (<see cref="NodeFrontClient"/>), never in a returned or
/// logged text. A node this app did not start has no key here, and then nothing is sent.
///
/// <para>Polled every minute, so a reason is logged only when it differs from the last one logged.</para>
/// </summary>
public sealed class NodeAlarmsRequest : IDisposable
{
    public const string Route = "/node/alarms";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Func<string?> _frontUrl;
    private readonly Func<string?> _internalKey;
    private readonly NodeFrontClient _client;
    private readonly Action<string> _log;
    private string? _lastLogged;

    /// <param name="frontUrl">The open node's address, read at each poll; null or empty when no node is open.</param>
    /// <param name="internalKey">The key of the node THIS app started, read at each poll; null or empty when it has none for the open node.</param>
    /// <param name="handler">For tests: the handler the request goes through; null means a real connection.</param>
    /// <param name="timeout">For tests: how long to wait for the node; null means <see cref="NodeFrontClient.DefaultTimeout"/>.</param>
    /// <param name="log">Where the one-line reasons go; null means the standard error stream.</param>
    public NodeAlarmsRequest(Func<string?> frontUrl, Func<string?> internalKey, HttpMessageHandler? handler = null, TimeSpan? timeout = null, Action<string>? log = null)
    {
        _frontUrl = frontUrl ?? throw new ArgumentNullException(nameof(frontUrl));
        _internalKey = internalKey ?? throw new ArgumentNullException(nameof(internalKey));
        _client = new NodeFrontClient(handler, timeout);
        _log = log ?? Console.Error.WriteLine;
    }

    /// <summary>Polls once. Never throws; a poll without a report says why.</summary>
    public async Task<NodeAlarmsPoll> PollAsync(CancellationToken cancellationToken)
    {
        string? key = null;
        try
        {
            var url = _frontUrl();
            if (string.IsNullOrEmpty(url)) return new NodeAlarmsPoll(null, "No node is open.");

            key = _internalKey();
            if (string.IsNullOrEmpty(key))
                return Logged(new NodeAlarmsPoll(null, "This app did not start the open node, so it has no key to ask it for blind-node alarms."), key);

            var reply = await _client.SendAsync(HttpMethod.Get, url, Route, key, readBody: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Logged(reply.Failure switch
            {
                NodeFrontFailure.None => FromAnswer(reply.Status!.Value, reply.Body),
                NodeFrontFailure.NotLoopback => new NodeAlarmsPoll(null, "The node is not on this computer, so it was not asked for alarms."),
                NodeFrontFailure.Cancelled => new NodeAlarmsPoll(null, "The alarms poll was cancelled."),
                NodeFrontFailure.TimedOut => new NodeAlarmsPoll(null, $"The node did not answer the alarms poll within {_client.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds."),
                NodeFrontFailure.Unreachable => new NodeAlarmsPoll(null, $"The node could not be reached for the alarms poll ({reply.ErrorKind})."),
                _ => new NodeAlarmsPoll(null, $"The alarms poll failed ({reply.ErrorKind})."),
            }, key);
        }
        catch (Exception ex)
        {
            return Logged(new NodeAlarmsPoll(null, $"The alarms poll failed ({ex.GetType().Name})."), key);
        }
    }

    private static NodeAlarmsPoll FromAnswer(HttpStatusCode status, string? body)
    {
        var code = (int)status;
        if (status == HttpStatusCode.NotImplemented) return new NodeAlarmsPoll(null, "The node does not report blind-node alarms (an older version).");
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new NodeAlarmsPoll(null, $"The node refused the alarms poll: this app is not authorized (HTTP {code}).");
        if (code is < 200 or > 299) return new NodeAlarmsPoll(null, $"The node answered {code} to the alarms poll.");
        try
        {
            var answer = JsonSerializer.Deserialize<BlindAlarmsAnswer>(body ?? "", Json);
            return answer is { State: not null, Alarms: not null }
                ? new NodeAlarmsPoll(answer)
                : new NodeAlarmsPoll(null, "The node's alarms answer was incomplete.");
        }
        catch (JsonException)
        {
            return new NodeAlarmsPoll(null, "The node's alarms answer could not be read.");
        }
    }

    /// <summary>Logs a reason once until it changes, and makes sure the key is not in its text.</summary>
    private NodeAlarmsPoll Logged(NodeAlarmsPoll poll, string? key)
    {
        if (!string.IsNullOrEmpty(key) && poll.Detail is { } detail && detail.Contains(key, StringComparison.Ordinal))
            poll = poll with { Detail = NodeFrontClient.Scrub(detail, key) };
        if (poll.Detail is { } reason && reason != _lastLogged)
        {
            _lastLogged = reason;
            try { _log($"[NodeAlarmsRequest] {reason}"); }
            catch { /* a log that cannot be written is not worth failing a poll for */ }
        }
        else if (poll.Answered)
        {
            _lastLogged = null;
        }
        return poll;
    }

    public void Dispose() => _client.Dispose();
}
