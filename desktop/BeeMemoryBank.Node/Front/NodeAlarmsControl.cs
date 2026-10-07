using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
/// <c>GET /node/alarms</c>: the desktop shell asks its own node which blind nodes need attention (BMB-77), to turn them into
/// notifications. The sibling of <see cref="NodeLockControl"/>, with the same gates and the same identity towards the Api.
///
/// <para><b>Who may ask.</b> Only a caller on this machine (anything else, a relayed request included, gets a plain 404) that also
/// presents the node's own internal key in <c>X-Internal-Key</c> (403 without saying whether it was missing or wrong).</para>
///
/// <para><b>What the front does with it.</b> It calls the Api's <c>GET /api/blind-nodes/alarms</c> over loopback as the node itself
/// (its own <c>X-Internal-Key</c> and <c>X-User-Role: superadmin</c>, the identity that route's group requires), bounded by
/// <see cref="DefaultApiTimeout"/>. The caller's own headers are never forwarded.</para>
///
/// <para><b>Answers.</b> 200: the Api's list, passed on as it came once it parsed as JSON (kinds, node ids, times, protocol numbers, and
/// names only while the vault is unlocked: nothing secret). 501: the Api has no such route (an older Api), the same answer the stub
/// gives when the front has no key. 502: the Api refused or answered something that is not a list. 503: the Api could not be reached
/// or did not answer in time. The error bodies are fixed <c>{"error": "..."}</c> texts that never hold the key.</para>
/// </summary>
public sealed class NodeAlarmsControl
{
    /// <summary>How long the front waits for the Api's answer before it gives up with 503.</summary>
    public static readonly TimeSpan DefaultApiTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The largest answer the front passes on: a list of a few alarms is a few kilobytes.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>The route on the front.</summary>
    public const string Route = "/node/alarms";

    /// <summary>The route on the Api the front calls.</summary>
    public const string ApiAlarmsRoute = "/api/blind-nodes/alarms";

    private readonly Uri _apiAlarmsUri;
    private readonly NodeInternalKey _internalKey;
    private readonly HttpClient _api;
    private readonly TimeSpan _apiTimeout;

    /// <param name="apiUrl">The Api child's loopback address (<c>http://127.0.0.1:port</c>).</param>
    /// <param name="internalKey">The node's own internal key (<c>BMB_INTERNAL_KEY</c>), the one the Api validates.</param>
    /// <param name="apiHandler">For tests: the handler the call to the Api goes through; null means a real connection.</param>
    /// <param name="apiTimeout">For tests: the bound of the call to the Api; null means <see cref="DefaultApiTimeout"/>.</param>
    public NodeAlarmsControl(string apiUrl, string internalKey, HttpMessageHandler? apiHandler = null, TimeSpan? apiTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) throw new ArgumentException("The Api address is required.", nameof(apiUrl));
        _internalKey = new NodeInternalKey(internalKey);
        _apiAlarmsUri = new Uri(new Uri(apiUrl.TrimEnd('/') + "/", UriKind.Absolute), ApiAlarmsRoute.TrimStart('/'));
        _apiTimeout = apiTimeout ?? DefaultApiTimeout;
        if (_apiTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(apiTimeout), "The bound must be positive.");

        // One client for the life of the node; the bound is applied per call. No proxy and no redirects: the Api is on this machine
        // and the request carries the key.
        _api = apiHandler is not null
            ? new HttpClient(apiHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Maps <c>GET /node/alarms</c>, outside the front's <c>/node</c> group like <c>/node/lock</c>: an off-machine caller gets 404.</summary>
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, (Func<HttpContext, Task<IResult>>)GetAsync)
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

    private async Task<IResult> GetAsync(HttpContext context)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        bound.CancelAfter(_apiTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _apiAlarmsUri);
            _internalKey.AddTo(request);
            request.Headers.TryAddWithoutValidation("X-User-Role", UserRoles.Superadmin);

            using var response = await _api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Results.Json(new { error = "This node's vault service does not report blind-node alarms." }, statusCode: StatusCodes.Status501NotImplemented);
            if (!response.IsSuccessStatusCode)
            {
                Log(context).LogWarning("The Api refused the alarms request with HTTP {Status}.", (int)response.StatusCode);
                return Results.Json(new { error = "The vault service refused the alarms request." }, statusCode: StatusCodes.Status502BadGateway);
            }

            var body = await ReadBoundedAsync(response.Content, bound.Token)
                ?? throw new InvalidDataException($"the answer is larger than {MaxBodyBytes} bytes");
            using (var parsed = JsonDocument.Parse(body))
            {
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("not an object");
            }
            return Results.Content(body, "application/json");
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            Log(context).LogWarning("The Api did not answer the alarms request within {Seconds} seconds.", _apiTimeout.TotalSeconds);
            return Results.Json(new { error = "The vault service did not answer the alarms request in time." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException ex)
        {
            // The type and the socket error only: the message of an exception is not trusted to be free of the request's details.
            Log(context).LogWarning("The Api could not be reached for the alarms request ({Reason}).", ex.HttpRequestError);
            return Results.Json(new { error = "The vault service could not be reached." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An answer that is too large or is not a JSON object, or anything else: a fixed text, not the host's error page.
            Log(context).LogWarning("The alarms request to the Api failed ({ExceptionType}).", ex.GetType().Name);
            return Results.Json(new { error = "The vault service gave no usable alarms answer." }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>The answer's text, or null when it is larger than <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0) total += read;
        return total > MaxBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static ILogger Log(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BeeMemoryBank.Node.NodeAlarmsControl");
}
