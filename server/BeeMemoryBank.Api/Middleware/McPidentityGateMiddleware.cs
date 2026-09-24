using System.Text.Json;
using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Hard identity gate for /mcp. Sits in the /mcp UseWhen branch, before
/// <see cref="McpSessionGuardMiddleware"/>, so a keyless request never reaches the MCP SDK and
/// never opens an MCP session.
///
/// <para>Without this, anything left of "am I allowed to talk to MCP at all" ran only inside the
/// tool handlers, where the only signal was the deny-all <c>CallerScopeMiddleware</c> applied to an
/// unidentified caller. That is fine for most tools (they silently return nothing), but a write
/// that did not consult the caller's scope at all — <c>MediaRepository.CreateAsync</c> for an
/// unlinked upload, specifically <c>if (!articleId.HasValue) return;</c> — succeeded: the
/// anonymous caller got back a media id, the file landed in the blob store, and a media_create sync
/// event propagated the row to every peer. With this gate, every request that is not on the short
/// allow-list below answers 401 before the SDK runs; the repository's own guard is the second
/// layer (B2 fix #2).</para>
///
/// <para>What counts as a credential, and why each is allowed or rejected — the classification is
/// the contract, not just the implementation, so that any future loosening is a deliberate diff
/// against it:</para>
/// <list type="bullet">
///   <item><description><b>Internal key</b> — the Web layer, desktop tray, CLI, and tests. Always
///   allowed; this is the trusted-inside gate.</description></item>
///   <item><description><b>Resolved bee_ agent</b> — <c>AuthAgent</c> set by
///   <see cref="AgentAuthMiddleware"/>. Allowed: the caller's identity is known.</description></item>
///   <item><description><b>Unresolved bee_… token</b> — presented but <c>AuthAgent</c> was not
///   set. Passed through so <see cref="McpSessionGuardMiddleware"/> can keep firing its existing
///   JSON-RPC error (clients depend on the wording to detect a revoked key after a DEK rotation).
///   This is the ONLY narrowly-scoped exception for a presented-but-unresolved token — every
///   other shape is rejected here.</description></item>
///   <item><description><b>bmbrt_ remote token</b> — REJECTED, whether it resolved in
///   <see cref="AgentAuthMiddleware"/> or not. Remote tokens are scoped to
///   <see cref="BeeMemoryBank.Api.Endpoints.RemoteAuthEndpoints"/>; they are not general-purpose
///   credentials. Classifying them from the header value (not from the resolved marker) means
///   an expired/unknown bmbrt_ does not slip through either.</description></item>
///   <item><description><b>No Authorization header</b> — rejected with 401.</description></item>
///   <item><description><b>Any other Authorization scheme (Basic, anything not starting with
///   "Bearer ", "Bearer junk", "Bearer foo_bar")</b> — rejected with 401. The MCP surface is a
///   single credential type; allowing random strings through only to land at a 4xx elsewhere
///   turns /mcp into a probe oracle.</description></item>
/// </list>
/// </summary>
public class McpIdentityGateMiddleware(RequestDelegate next, ILogger<McpIdentityGateMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public async Task InvokeAsync(HttpContext context)
    {
        // Internal key — Web layer, tray, CLI, tests. Always allowed.
        if (InternalKeyValidator.Validate(context))
        {
            await next(context);
            return;
        }

        // No Authorization header at all → 401. The classification below is meaningless without
        // something to classify.
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader))
        {
            await RejectAsync(context, "no_credentials",
                "Authentication required for /mcp.");
            return;
        }

        // Anything that is not "Bearer <value>" is not a credential shape we recognise.
        // "Basic …", a bare token with no scheme, "Bearer junk" all land here. Reject before
        // the SDK so the SDK does not get to make the auth decision itself.
        const string bearerPrefix = "Bearer ";
        if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(context, "unsupported_scheme",
                "Only the Bearer scheme is accepted on /mcp.");
            return;
        }

        var token = authHeader[bearerPrefix.Length..].Trim();
        if (token.Length == 0)
        {
            await RejectAsync(context, "empty_bearer",
                "Bearer token is empty.");
            return;
        }

        // bmbrt_ tokens are remote-folder-mirroring credentials. They are NEVER valid for MCP,
        // regardless of whether the lookup in AgentAuthMiddleware resolved. Classifying from the
        // header value (rather than from the IsRemoteToken marker) closes the "expired bmbrt_
        // doesn't set IsRemoteToken, so it would slip through" hole.
        if (token.StartsWith("bmbrt_", StringComparison.Ordinal))
        {
            await RejectAsync(context, "remote_token_not_accepted",
                "Bearer bmbrt_ tokens are not accepted on /mcp. Use a bee_ agent key.");
            return;
        }

        // bee_…: pass through unconditionally. If AuthAgent is set, the SDK gets a real agent;
        // if not, McpSessionGuardMiddleware's "agent key was not recognized" JSON-RPC error fires
        // — clients depend on that wording to detect a key revoked by DEK rotation.
        if (token.StartsWith("bee_", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        // "Bearer <anything else>" — any other token shape is not a credential we accept.
        // Reject so the SDK cannot answer for us with whatever it would have done.
        await RejectAsync(context, "unrecognised_token",
            "Bearer token is not a recognised credential type.");
    }

    private async Task RejectAsync(HttpContext context, string reason, string message)
    {
        // Debug, not Warning: an internet-reachable node sees this for every scanner probing for
        // /wp-login.php, /phpmyadmin, etc., and a log that scrolls is a log nobody reads. The
        // requests that matter — a peer or an agent that cannot get through — are the ones an
        // operator goes looking for, and they will find them here.
        logger.LogDebug(
            "MCP request from {RemoteIp} rejected: {Reason} (TraceId={TraceId})",
            context.Connection.RemoteIpAddress, reason, context.TraceIdentifier);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new { error = message }, JsonOpts));
    }
}
