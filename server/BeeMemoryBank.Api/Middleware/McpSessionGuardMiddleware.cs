using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Intercepts MCP <c>tools/call</c> JSON-RPC requests and rejects, with a clear error, the two
/// currently-unhandled ways a call can otherwise return silent empty/null results:
///
/// 1. A <c>bee_</c> agent bearer key was presented but did not resolve to a live agent row
///    (AgentAuthMiddleware left <c>ctx.Items["AuthAgent"]</c> unset) — most commonly because a
///    DEK rotation hard-deleted every row in tbl_agent (see DekRotationService's tbl_agent
///    DELETE). Without this check, CallerScopeMiddleware assigns a deny-all ACL to the
///    unidentified caller and every repository call silently filters to empty/null.
/// 2. The session is locked and the requested tool is marked
///    <see cref="RequiresUnlockedSessionAttribute"/> — i.e. it unconditionally needs to decrypt
///    content to do anything useful. Several tools already carry their own per-call
///    session.IsUnlocked check; this middleware is the centralized backstop for the tools that
///    don't (and for any new tool an author forgets to add one to).
///
/// Modeled directly on <see cref="McpParameterValidationMiddleware"/>'s body-sniffing structure
/// (same buffering, same early-outs, same JSON-RPC error shape) so the two middlewares stay easy
/// to compare.
/// </summary>
public class McpSessionGuardMiddleware(RequestDelegate next, McpToolRegistry registry, ILogger<McpSessionGuardMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public async Task InvokeAsync(HttpContext context, SessionService session)
    {
        // Only POSTs with JSON bodies carry tools/call requests. SSE GETs and other
        // verbs pass through untouched.
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !(context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
        }
        context.Request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(body))
        {
            await next(context);
            return;
        }

        JsonDocument? doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            await next(context);
            return;
        }

        using (doc)
        {
            // Only validate single requests; batches are rare in MCP and we let the SDK
            // handle them as-is rather than partially short-circuit.
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                await next(context);
                return;
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var methodEl) ||
                methodEl.GetString() != "tools/call" ||
                !root.TryGetProperty("params", out var paramsEl) ||
                paramsEl.ValueKind != JsonValueKind.Object)
            {
                await next(context);
                return;
            }

            if (!paramsEl.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            {
                await next(context);
                return;
            }

            var toolName = nameEl.GetString()!;

            object? requestId = root.TryGetProperty("id", out var idEl)
                ? JsonSerializer.Deserialize<JsonElement>(idEl.GetRawText())
                : null;

            // Check A — revoked/unresolved agent key. Runs regardless of which tool is being
            // called — it's not tool-specific. A bee_ agent key was presented but
            // AgentAuthMiddleware didn't resolve it to a live agent row — most commonly because
            // a DEK rotation deleted all agent rows (see DekRotationService.cs comment at the
            // tbl_agent DELETE). Distinguish this from "no token at all" (a legitimate
            // no-auth-configured / internal-key path elsewhere) by requiring the Bearer prefix
            // to specifically match bee_ AND AuthAgent to be absent.
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader != null &&
                authHeader.StartsWith("Bearer bee_", StringComparison.OrdinalIgnoreCase) &&
                !context.Items.ContainsKey("AuthAgent"))
            {
                var message = "Error: your agent key was not recognized. It may have been revoked " +
                    "(for example, by a DEK rotation, which deletes all agent keys) or is otherwise " +
                    "invalid. Ask the vault owner to issue you a new bee_ agent key.";
                logger.LogWarning("MCP tools/call for {Tool} rejected: unrecognized agent key (TraceId={TraceId})",
                    toolName, context.TraceIdentifier);
                await WriteErrorAsync(context, requestId, message);
                return;
            }

            // Check B — session locked for a [RequiresUnlockedSession] tool. Only reached if
            // Check A didn't already short-circuit.
            var tool = registry.Get(toolName);
            if (tool != null && tool.RequiresUnlockedSession && !session.IsUnlocked)
            {
                var message = "Bank is locked: log in as admin to unlock.";
                logger.LogWarning("MCP tools/call for {Tool} rejected: session is locked (TraceId={TraceId})",
                    toolName, context.TraceIdentifier);
                await WriteErrorAsync(context, requestId, message);
                return;
            }

            await next(context);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, object? requestId, string message)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = requestId,
            result = new
            {
                isError = true,
                content = new object[]
                {
                    new { type = "text", text = message }
                }
            }
        };

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOpts));
    }
}
