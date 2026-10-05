using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// The shell's own request to the node it started: <c>POST /node/lock</c>, "lock the vault now". It is what the power-events services
/// make when the computer goes to sleep (the Windows <see cref="PowerEventsService"/> in the background, the macOS monitor waiting for
/// it briefly), and it is the same call on both systems.
///
/// <para><b>Authentication.</b> The node's front answers <c>/node/lock</c> only to a caller on this machine that presents the node's
/// internal key in <c>X-Internal-Key</c>. The key is the one <see cref="NodeLifecycleService"/> generated when it started this node
/// (it also puts it in this process's <c>BMB_INTERNAL_KEY</c>, where <see cref="NodeSessionHandoff"/> reads it for the update
/// handoff). It is sent only to a loopback address, over a connection that does not use a proxy and does not follow redirects, and it
/// is never part of any text this class returns or logs. Those guards are the ones every call to the node's front gets from
/// <see cref="NodeFrontClient"/>.</para>
///
/// <para><b>A node this app did not start.</b> When the shell attached to a node that was already running (a service, the command
/// line, another app) it has no key for it, and the one in its environment, if any, belongs to a node it hosted earlier. Nothing is
/// sent then: the result says why, flagged <see cref="SleepLockResult.LogOnly"/> (one log line, no notice), and the vault of that
/// node stays as it was. Nothing unsafe happens; the vault is simply not locked by this app.</para>
///
/// <para><b>What the answers mean.</b> 2xx (the node answers 204): the Api locked the vault. 401/403: this app is not authorized (a
/// wrong or missing key). 501: an older node that does not offer lock-on-sleep (the macOS monitor keeps that case quiet). Anything
/// else, a timeout or no connection: the vault was not locked, and the result says so in a fixed text.</para>
///
/// <para><b>"Locked" is advisory.</b> After a success the node's session is locked: <c>GET /api/session/status</c> says so and the
/// master key is wiped from memory. As SECURITY.md ("Lock is advisory") says for the Lock button, an agent key owned by a superadmin
/// carries its own wrapped copy and re-unlocks the process on its next request, and OS auto-unlock only acts when the Api starts.</para>
/// </summary>
public sealed class NodeLockRequest
{
    public const string Route = "/node/lock";
    public const string InternalKeyHeader = NodeFrontClient.InternalKeyHeader;

    /// <summary>How long the shell waits for the node's answer. The node bounds its own call to the Api at 3 s, so it answers first.</summary>
    public static readonly TimeSpan DefaultTimeout = NodeFrontClient.DefaultTimeout;

    private readonly Func<string?> _frontUrl;
    private readonly Func<string?> _internalKey;
    private readonly NodeFrontClient _client;
    private readonly Action<string> _log;

    /// <param name="frontUrl">The open node's address, read at the moment of the request; null or empty when no node is open.</param>
    /// <param name="internalKey">The key of the node THIS app started, read at the moment of the request; null or empty when the app has none for the open node.</param>
    /// <param name="handler">For tests: the handler the request goes through; null means a real connection.</param>
    /// <param name="timeout">For tests: how long to wait for the node; null means <see cref="DefaultTimeout"/>.</param>
    /// <param name="log">Where the one-line reasons go; null means the standard error stream.</param>
    public NodeLockRequest(Func<string?> frontUrl, Func<string?> internalKey, HttpMessageHandler? handler = null, TimeSpan? timeout = null, Action<string>? log = null)
    {
        _frontUrl = frontUrl ?? throw new ArgumentNullException(nameof(frontUrl));
        _internalKey = internalKey ?? throw new ArgumentNullException(nameof(internalKey));
        _client = new NodeFrontClient(handler, timeout ?? DefaultTimeout);
        _log = log ?? Console.Error.WriteLine;
    }

    /// <summary>Makes the request. Never throws; the outcome is the result, and a reason that is not a success is also logged once.</summary>
    public async Task<SleepLockResult> RequestAsync(CancellationToken cancellationToken)
    {
        string? key = null;
        try
        {
            var url = _frontUrl();
            if (string.IsNullOrEmpty(url)) return new SleepLockResult(true, "No node is open, so there is nothing to lock.");

            key = _internalKey();
            if (string.IsNullOrEmpty(key))
                return Logged(new SleepLockResult(false,
                    "This app did not start the open node, so it has no key to lock it with; the vault was not locked.", LogOnly: true), key);

            var reply = await _client.SendAsync(HttpMethod.Post, url, Route, key, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Logged(reply.Failure switch
            {
                NodeFrontFailure.None => FromStatus(reply.Status!.Value),
                NodeFrontFailure.NotLoopback => new SleepLockResult(false, "The node is not on this computer, so the lock request was not sent."),
                NodeFrontFailure.Cancelled => new SleepLockResult(false, "The lock request was cancelled before the node answered."),
                NodeFrontFailure.TimedOut => new SleepLockResult(false, $"The node did not answer the lock request within {_client.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds."),
                // The kind of failure only: an exception's own text is not trusted to be free of the request's details.
                NodeFrontFailure.Unreachable => new SleepLockResult(false, $"The node could not be reached ({reply.ErrorKind})."),
                _ => new SleepLockResult(false, $"The lock request failed ({reply.ErrorKind})."),
            }, key);
        }
        catch (Exception ex)
        {
            // The client itself does not throw; this covers the address and key callbacks above.
            return Logged(new SleepLockResult(false, $"The lock request failed ({ex.GetType().Name})."), key);
        }
    }

    private static SleepLockResult FromStatus(HttpStatusCode status)
    {
        var code = (int)status;
        if (code is >= 200 and <= 299) return new SleepLockResult(true);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new SleepLockResult(false, $"The node refused the lock request: this app is not authorized to lock it (HTTP {code}).", code);
        return new SleepLockResult(false, $"The node answered {code} to the lock request.", code);
    }

    /// <summary>Logs a result that is not a success (once), and makes sure the key is not in its text.</summary>
    private SleepLockResult Logged(SleepLockResult result, string? key)
    {
        if (!string.IsNullOrEmpty(key) && result.Detail is { } detail && detail.Contains(key, StringComparison.Ordinal))
            result = result with { Detail = NodeFrontClient.Scrub(detail, key) };
        if (!result.Succeeded && result.Detail is { } reason)
        {
            try { _log($"[NodeLockRequest] {reason}"); }
            catch { /* a log that cannot be written is not worth failing a sleep for */ }
        }
        return result;
    }
}
