using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>Why a request to the node's front did not produce an answer.</summary>
public enum NodeFrontFailure
{
    /// <summary>No failure: the node answered (see <see cref="NodeFrontReply.Status"/>).</summary>
    None,
    /// <summary>The address is not an http(s) address on this computer; nothing was sent, so the key never left it.</summary>
    NotLoopback,
    /// <summary>The caller's cancellation token fired.</summary>
    Cancelled,
    /// <summary>The node did not answer within the client's timeout.</summary>
    TimedOut,
    /// <summary>No connection could be made or kept.</summary>
    Unreachable,
    /// <summary>Anything else.</summary>
    Failed,
}

/// <summary>What <see cref="NodeFrontClient.SendAsync"/> found out. It never holds the internal key.</summary>
/// <param name="Failure">Why there is no answer, or <see cref="NodeFrontFailure.None"/>.</param>
/// <param name="Status">The node's HTTP status when it answered.</param>
/// <param name="Body">The response text, only when it was asked for and the node answered.</param>
/// <param name="ErrorKind">The kind of a failure only (the <see cref="HttpRequestError"/> or the exception type), safe to show.</param>
/// <param name="ErrorMessage">The exception's own text with the key replaced by <c>[key]</c>; for logs, not for the user.</param>
public sealed record NodeFrontReply(
    NodeFrontFailure Failure,
    HttpStatusCode? Status = null,
    string? Body = null,
    string? ErrorKind = null,
    string? ErrorMessage = null)
{
    public bool Answered => Failure == NodeFrontFailure.None && Status is not null;

    public bool IsSuccessStatus => Status is { } s && (int)s is >= 200 and <= 299;
}

/// <summary>
/// The one way the shell calls an endpoint of its own node's front (<c>/node/...</c>) with the node's internal key: the update-unlock
/// handoff, the vault lock on sleep, and the update guard of a profile switch.
///
/// <para><b>What it guarantees</b> (they used to be re-implemented by each caller, and only the lock request did all of them): the
/// address must be an http(s) address on this computer, or nothing is sent and the key never leaves it; the request goes over a
/// connection that does not use a proxy and does not follow redirects, so the key cannot be passed on to another address; the wait is
/// bounded; and the key is never part of anything the client returns (it is replaced by <c>[key]</c> in the exception text, and the
/// kind of a failure is reported without the text at all).</para>
///
/// <para><b>What stays with the callers:</b> where the address and the key come from (they differ: the open node's key only, the
/// environment, or the environment then the profile's key file), what the answer means, and the texts they log or show.</para>
/// </summary>
public sealed class NodeFrontClient : IDisposable
{
    public const string InternalKeyHeader = "X-Internal-Key";
    public const string UserRoleHeader = "X-User-Role";
    public const string KeyEnvironmentVariable = "BMB_INTERNAL_KEY";
    public const string KeyFileName = ".internal-key";

    /// <summary>How long a call waits for the node. The node bounds its own calls to the Api shorter, so it answers first.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _client;

    /// <param name="handler">For tests: the handler requests go through (not disposed here); null means a real connection with no proxy and no redirects.</param>
    /// <param name="timeout">How long to wait for the node; null means <see cref="DefaultTimeout"/>.</param>
    public NodeFrontClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _client = handler is not null
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient(CreateDefaultHandler());
        _client.Timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The handler of a real connection: it ignores any system proxy and follows no redirect, so the key goes to the address given and nowhere else.</summary>
    internal static SocketsHttpHandler CreateDefaultHandler() => new() { UseProxy = false, AllowAutoRedirect = false };

    /// <summary>The wait limit this client applies.</summary>
    public TimeSpan Timeout => _client.Timeout;

    /// <summary>The key in this process's environment: set only for a node this app started (<see cref="NodeLifecycleService"/> does it).</summary>
    public static string? KeyFromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>The environment key, else the key file of the node's data directory (<paramref name="dataPath"/>); null when there is none.</summary>
    public static string? ResolveInternalKey(string? dataPath)
    {
        var key = KeyFromEnvironment();
        if (key is not null) return key;
        if (string.IsNullOrEmpty(dataPath)) return null;
        try
        {
            var keyFile = Path.Combine(dataPath, KeyFileName);
            if (File.Exists(keyFile))
            {
                var fromFile = File.ReadAllText(keyFile).Trim();
                return fromFile.Length == 0 ? null : fromFile;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No key then: the caller decides what that means (the update guard fails open).
        }
        return null;
    }

    /// <summary>Replaces the key in <paramref name="text"/> by <c>[key]</c>.</summary>
    public static string Scrub(string text, string? key) =>
        string.IsNullOrEmpty(key) ? text : text.Replace(key, "[key]", StringComparison.Ordinal);

    /// <summary>
    /// Sends <paramref name="method"/> to <paramref name="path"/> (for example <c>/node/lock</c>) of the front at <paramref name="frontUrl"/>.
    /// Never throws; the outcome is the reply. A null or empty <paramref name="internalKey"/> sends the request without the header.
    /// </summary>
    /// <param name="asSuperadmin">Also send <c>X-User-Role: superadmin</c> (the update routes want it; the lock route does not).</param>
    /// <param name="readBody">Read the response text into <see cref="NodeFrontReply.Body"/>.</param>
    public async Task<NodeFrontReply> SendAsync(
        HttpMethod method, string frontUrl, string path, string? internalKey,
        bool asSuperadmin = false, bool readBody = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Uri.TryCreate($"{frontUrl.TrimEnd('/')}{path}", UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
                || !target.IsLoopback)
                return new NodeFrontReply(NodeFrontFailure.NotLoopback);

            using var request = new HttpRequestMessage(method, target);
            if (!string.IsNullOrEmpty(internalKey)) request.Headers.TryAddWithoutValidation(InternalKeyHeader, internalKey);
            if (asSuperadmin) request.Headers.TryAddWithoutValidation(UserRoleHeader, "superadmin");

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var body = readBody ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : null;
            return new NodeFrontReply(NodeFrontFailure.None, response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new NodeFrontReply(NodeFrontFailure.Cancelled);
        }
        catch (OperationCanceledException ex)
        {
            return new NodeFrontReply(NodeFrontFailure.TimedOut, ErrorKind: ex.GetType().Name, ErrorMessage: Scrub(ex.Message, internalKey));
        }
        catch (HttpRequestException ex)
        {
            // The kind of failure only for ErrorKind: an exception's own text is not trusted to be free of the request's details.
            return new NodeFrontReply(NodeFrontFailure.Unreachable, ErrorKind: ex.HttpRequestError.ToString(), ErrorMessage: Scrub(ex.Message, internalKey));
        }
        catch (Exception ex)
        {
            return new NodeFrontReply(NodeFrontFailure.Failed, ErrorKind: ex.GetType().Name, ErrorMessage: Scrub(ex.Message, internalKey));
        }
    }

    public void Dispose() => _client.Dispose();
}
