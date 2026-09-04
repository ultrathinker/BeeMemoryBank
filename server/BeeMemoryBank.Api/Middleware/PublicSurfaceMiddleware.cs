using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Answers 404 to any keyless request for a path outside <see cref="PublicSurface"/>.
///
/// <para>Runs first, before rate limiting and before agent auth: a path that is not published has
/// no business consuming a rate-limit slot, touching the session, or reaching a handler at all.
/// "Internal" is decided purely by the key — <c>InternalKeyValidator.Validate</c> no longer has a
/// trust-any-loopback-caller fallback, so this gate's answer does not depend on the caller's
/// address at all, and only the log line below reads it.</para>
///
/// <para>404, not 403: the answer for a keyless caller must be the same whether or not the endpoint
/// exists, or the node hands anyone who can reach it a map of its own version and feature set.</para>
/// </summary>
public class PublicSurfaceMiddleware(RequestDelegate next, ILogger<PublicSurfaceMiddleware> logger)
{
    /// <summary>
    /// Set <c>BMB_PUBLIC_SURFACE=off</c> to disable the gate for one release, in case a deployment
    /// depends on reaching an endpoint this list does not know about. It is an escape hatch for a
    /// bad afternoon, not a supported configuration — the startup line says so, and anyone who
    /// needs it should report which endpoint, so it can be listed properly instead.
    /// </summary>
    private static readonly bool Disabled =
        string.Equals(Environment.GetEnvironmentVariable("BMB_PUBLIC_SURFACE"), "off",
            StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (Disabled || InternalKeyValidator.Validate(context))
        {
            await next(context);
            return;
        }

        if (PublicSurface.Allows(context.Request.Method, context.Request.Path))
        {
            await next(context);
            return;
        }

        // Debug, not Warning: on a node reachable from the internet this fires for every scanner
        // probing for /wp-login.php, and a log that scrolls is a log nobody reads. The requests
        // that matter — a peer or an agent that cannot get through — are the ones an operator goes
        // looking for, and they will find them here.
        logger.LogDebug("Public surface: refusing {Method} {Path} from {RemoteIp} (no internal key)",
            context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>Startup line, so the state of the gate is visible in the logs of a node that has it off.</summary>
    public static void LogStartupState()
    {
        Console.WriteLine(Disabled
            ? "[public-surface] DISABLED via BMB_PUBLIC_SURFACE=off — every endpoint is reachable by any caller the proxy lets through."
            : "[public-surface] Enabled — keyless callers reach only the peer/agent endpoints.");
    }
}
