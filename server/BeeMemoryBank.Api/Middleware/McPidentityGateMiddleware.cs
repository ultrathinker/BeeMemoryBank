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
/// event propagated the row to every peer. With this gate, "no credential" answers 401 before the
/// SDK runs; the repository's own guard is the second layer (B2 fix #2).</para>
///
/// <para>What counts as a credential, and why each is allowed or rejected:</para>
/// <list type="bullet">
///   <item><description><b>Internal key</b> — the Web layer, desktop tray, CLI, and tests. Always
///   allowed; this is the trusted-inside gate.</description></item>
///   <item><description><b>Resolved bee_ agent</b> — <c>AuthAgent</c> set by
///   <see cref="AgentAuthMiddleware"/>. Allowed: the caller's identity is known.</description></item>
///   <item><description><b>bmbrt_ remote token</b> — sets <c>CallerIdentity</c> too, but the
///   <see cref="RemoteAuthEndpoints"/> are its only intended destination (cross-instance folder
///   mirroring). Rejected here so the answer is distinct from a missing credential.</description></item>
///   <item><description><b>No Authorization header at all</b> — rejected with 401.</description></item>
///   <item><description><b>Bearer bee_… that did not resolve</b> — passed through, so
///   <see cref="McpSessionGuardMiddleware"/>'s existing JSON-RPC error keeps firing. Clients
///   rely on that wording to detect "your key was revoked" (DEK rotation wipes tbl_agent;
///   see its comment). Converting it to a flat 401 would break that signal.</description></item>
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

        // Resolved bee_ agent — AgentAuthMiddleware built CallerIdentity + AuthAgent.
        if (context.Items.ContainsKey("AuthAgent"))
        {
            await next(context);
            return;
        }

        // bmbrt_ tokens set CallerIdentity (for the remote-folder endpoints) but are not
        // intended for MCP. The marker makes the distinction without re-parsing the header.
        if (context.Items.ContainsKey("IsRemoteToken"))
        {
            logger.LogWarning(
                "MCP request from {RemoteIp} rejected: bmbrt_ remote tokens are not accepted on /mcp (TraceId={TraceId})",
                context.Connection.RemoteIpAddress, context.TraceIdentifier);
            await WriteUnauthorizedAsync(context,
                "Bearer bmbrt_ tokens are not accepted on /mcp. Use a bee_ agent key.");
            return;
        }

        // No credential at all → 401. The "presented-but-unresolved bee_ key" case below is
        // passed through to McpSessionGuardMiddleware so the existing JSON-RPC error keeps
        // answering (clients depend on its wording for key-rotation detection).
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "MCP request from {RemoteIp} rejected: no credentials presented (TraceId={TraceId})",
                context.Connection.RemoteIpAddress, context.TraceIdentifier);
            await WriteUnauthorizedAsync(context, "Authentication required for /mcp.");
            return;
        }

        await next(context);
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new { error = message }, JsonOpts));
    }
}
