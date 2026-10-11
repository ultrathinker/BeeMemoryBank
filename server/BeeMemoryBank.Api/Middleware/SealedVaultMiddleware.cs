using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.Sealed;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Requests while a sealed node's database is not usable (docs/design/step-b-spec.md 5.6): the relay set goes to the relay (also while the inbox
/// is being imported), a short allow-list is served, everything else is answered 503 <c>vault_sealed</c> without touching the database. It sits
/// right after <see cref="AgentAuthMiddleware"/>, so an agent's request can open the database first (4.6), and in front of everything that reads
/// the vault. On a plain node, on a blind host and on an open sealed node it passes every request on untouched.
/// </summary>
public sealed class SealedVaultMiddleware(RequestDelegate next)
{
    public const string ErrorCode = "vault_sealed";

    public const string ClosedMessage = "This node's database is encrypted and closed: unlock it first.";

    private const string ProgressPath = "/api/vault/seal/progress";

    /// <summary>Method and path of the routes a closed node serves itself (none of them reads the database).</summary>
    private static readonly (string Method, string Path)[] AllowList =
    [
        ("GET", "/health"),
        ("GET", "/version"),
        ("GET", "/api/version"),
        ("GET", "/api/session/status"),
        ("POST", "/api/session/unlock"),
        ("POST", "/api/session/login"),
        ("POST", "/api/session/recover-access"),
        ("GET", "/api/vault/status"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var gate = context.RequestServices?.GetService<IVaultGate>();
        if (gate is not { IsSealed: true })
        {
            await next(context);
            return;
        }

        var state = gate.State;
        var relay = context.RequestServices?.GetService<ISealedRelay>();
        var path = context.Request.Path;

        // The relay set belongs to the boot store until the open node has imported the inbox: while Closed, Opening, Closing and Importing.
        if (state != VaultState.Open && relay is not null && relay.Handles(path, context.Request.Method))
        {
            await relay.HandleAsync(context);
            return;
        }

        if (gate.IsUsable)
        {
            await next(context);
            return;
        }

        // The migration of this very process is between its swap and the open of the new database: its own progress poll (memory only, the owner's
        // route) goes on being answered, so the page sees "done" and not a refusal. A node that is not migrating does not serve it while closed.
        if (HttpMethods.IsGet(context.Request.Method) && path.Equals(ProgressPath, StringComparison.OrdinalIgnoreCase)
            && context.RequestServices?.GetService<SealMigrationService>() is { IsRunning: true })
        {
            await next(context);
            return;
        }

        if (IsAllowed(context.Request.Method, path) || path.StartsWithSegments("/mcp"))
        {
            // /mcp is answered in JSON-RPC by McpSessionGuardMiddleware (the agent's own request has had its chance to open the database).
            await next(context);
            return;
        }

        await WriteClosedAsync(context);
    }

    private static bool IsAllowed(string method, PathString path)
    {
        foreach (var (allowedMethod, allowedPath) in AllowList)
            if (string.Equals(method, allowedMethod, StringComparison.OrdinalIgnoreCase) && path.Equals(allowedPath, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>503 <c>vault_sealed</c> with <c>Retry-After: 30</c>, the one answer of a closed node to anything it does not serve.</summary>
    public static async Task WriteClosedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsJsonAsync(new ErrorResponse(ClosedMessage, ErrorCode));
    }
}
