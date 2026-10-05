using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Node;

/// <summary>
/// <c>POST /node/lock</c>: the desktop shell tells its own node to lock the vault, which is what it does when the computer goes to
/// sleep (<c>PowerEventsService</c> on Windows, <c>MacOsSleepMonitor</c> on macOS).
///
/// <para><b>Who may ask.</b> Only a caller on this machine (anything else gets a plain 404, the answer an off-machine caller gets from
/// <c>/node/update/*</c> too: it learns nothing about the route) that also presents the node's own internal key in
/// <c>X-Internal-Key</c> (403 without saying whether it was missing or wrong). The key check is the front's one comparer,
/// <see cref="NodeInternalKey"/>. A web page open in the user's browser can reach 127.0.0.1 as well, but it cannot send the header
/// cross-origin and does not know the key.</para>
///
/// <para><b>What the front does with it.</b> It calls the Api's <c>POST /api/session/lock</c> over loopback, authenticating as the node
/// itself: its own <c>X-Internal-Key</c> and <c>X-User-Role: superadmin</c>. That is the identity the Api route needs
/// (<c>RequireInternalKey().RequireSuperadmin().RequireNonAgent()</c>) and the one the tray already uses for
/// <c>/node/update/unlock-handoff</c>; the shell is the node's owner, so a call made with the internal key is the intended identity and
/// no user session is involved. The caller's own headers are never forwarded, and the front's key never leaves the machine (the Api
/// is on 127.0.0.1). The call is bounded by <see cref="DefaultApiTimeout"/>, so a hung Api cannot hang a sleep.</para>
///
/// <para><b>Answers.</b> 204: the Api locked the vault (locking an already locked vault is fine and answers the same). 502: the Api
/// answered with a refusal. 503: the Api could not be reached or did not answer in time. The bodies are fixed plain
/// <c>{"error": "..."}</c> texts: no exception text, no status line of the Api, never the key.</para>
///
/// <para><b>What "locked" means.</b> After a 204 the Api's <c>SessionService.IsUnlocked</c> is false and <c>GET /api/session/status</c>
/// says so. This is advisory, exactly as SECURITY.md ("Lock is advisory") says for the Lock button: the master key is wiped from
/// memory, but an agent key owned by a superadmin carries its own wrapped copy and re-unlocks the whole process on its next request,
/// with a live MCP client attached usually seconds later. OS auto-unlock (Windows, opt-in) does not undo a lock inside a running
/// process: it only acts when the Api starts. Revoke the superadmin-owned agent keys if the vault must stay locked.</para>
/// </summary>
public sealed class NodeLockControl
{
    /// <summary>How long the front waits for the Api's answer before it gives up with 503.</summary>
    public static readonly TimeSpan DefaultApiTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The route on the front.</summary>
    public const string Route = "/node/lock";

    /// <summary>The route on the Api the front calls.</summary>
    public const string ApiLockRoute = "/api/session/lock";

    private readonly Uri _apiLockUri;
    private readonly NodeInternalKey _internalKey;
    private readonly HttpClient _api;
    private readonly TimeSpan _apiTimeout;

    /// <param name="apiUrl">The Api child's loopback address (<c>http://127.0.0.1:port</c>).</param>
    /// <param name="internalKey">The node's own internal key (<c>BMB_INTERNAL_KEY</c>), the one the Api validates.</param>
    /// <param name="apiHandler">For tests: the handler the call to the Api goes through; null means a real connection.</param>
    /// <param name="apiTimeout">For tests: the bound of the call to the Api; null means <see cref="DefaultApiTimeout"/>.</param>
    public NodeLockControl(string apiUrl, string internalKey, HttpMessageHandler? apiHandler = null, TimeSpan? apiTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) throw new ArgumentException("The Api address is required.", nameof(apiUrl));
        _internalKey = new NodeInternalKey(internalKey);
        _apiLockUri = new Uri(new Uri(apiUrl.TrimEnd('/') + "/", UriKind.Absolute), ApiLockRoute.TrimStart('/'));
        _apiTimeout = apiTimeout ?? DefaultApiTimeout;
        if (_apiTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(apiTimeout), "The bound must be positive.");

        // One client for the life of the node. The bound is applied per call (below), so the client's own timeout stays off.
        // No proxy and no redirects: the Api is on this machine and the request carries the key.
        _api = apiHandler is not null
            ? new HttpClient(apiHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Maps <c>POST /node/lock</c>. Outside the front's <c>/node</c> group on purpose: that group answers an off-machine caller with 403, this route with 404.</summary>
    public void Map(IEndpointRouteBuilder endpoints)
    {
        // The explicit delegate type matters: a bare method group taking an HttpContext binds as a RequestDelegate, which throws the IResult away.
        endpoints.MapPost(Route, (Func<HttpContext, Task<IResult>>)LockAsync)
            .AddEndpointFilter(async (context, next) =>
                LoopbackIpMatcher.IsLoopback(context.HttpContext.Connection.RemoteIpAddress)
                    && !ForwardingHeaders.IsPresentOn(context.HttpContext.Request)
                    ? await next(context)
                    : Results.StatusCode(StatusCodes.Status404NotFound))
            .AddEndpointFilter(async (context, next) =>
                _internalKey.IsPresentedBy(context.HttpContext.Request)
                    ? await next(context)
                    : Results.Json(new { error = "Unauthorized" }, statusCode: StatusCodes.Status403Forbidden));
    }

    private async Task<IResult> LockAsync(HttpContext context)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        bound.CancelAfter(_apiTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _apiLockUri);
            _internalKey.AddTo(request);
            request.Headers.TryAddWithoutValidation("X-User-Role", UserRoles.Superadmin);

            using var response = await _api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token);
            if (response.IsSuccessStatusCode) return Results.NoContent();

            Log(context).LogWarning("The Api refused the lock request with HTTP {Status}.", (int)response.StatusCode);
            return Results.Json(new { error = "The vault service refused the lock request." }, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            Log(context).LogWarning("The Api did not answer the lock request within {Seconds} seconds.", _apiTimeout.TotalSeconds);
            return Results.Json(new { error = "The vault service did not answer the lock request in time." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException ex)
        {
            // The type and the socket error only: the message of an exception is not trusted to be free of the request's details.
            Log(context).LogWarning("The Api could not be reached for the lock request ({Reason}).", ex.HttpRequestError);
            return Results.Json(new { error = "The vault service could not be reached." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever else goes wrong, the answer is a fixed text, not the host's error page.
            Log(context).LogWarning("The lock request to the Api failed ({ExceptionType}).", ex.GetType().Name);
            return Results.Json(new { error = "The lock request could not be sent to the vault service." }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static ILogger Log(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BeeMemoryBank.Node.NodeLockControl");
}
