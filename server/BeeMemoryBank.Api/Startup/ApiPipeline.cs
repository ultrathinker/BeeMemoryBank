using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Api.Startup;

/// <summary>
/// The middleware chain and the endpoint map. Order in <see cref="UseBeeApiPipeline"/> IS the
/// behaviour — each entry says why it sits where it does.
/// </summary>
public static class ApiPipeline
{
    public static void UseBeeApiPipeline(this WebApplication app)
    {
// unpublished path should not consume a rate-limit slot or reach agent auth, and until now the
// answer to "what is visible from the internet" lived only in the reverse proxy's configuration.
BeeMemoryBank.Api.Middleware.PublicSurfaceMiddleware.LogStartupState();
app.UseMiddleware<BeeMemoryBank.Api.Middleware.PublicSurfaceMiddleware>();

// Rate limiting for sensitive endpoints (unlock, join) — brute-force protection
app.UseMiddleware<BeeMemoryBank.Api.Middleware.RateLimitMiddleware>();

// Maintenance mode — blocks all requests except snapshot restore and session unlock
app.UseMiddleware<BeeMemoryBank.Api.Middleware.MaintenanceMiddleware>();

// Agent bearer auth (non-blocking, auto-unlock)
app.UseMiddleware<BeeMemoryBank.Api.Middleware.AgentAuthMiddleware>();

// Ambient caller scope — resolves folder ACL once per request, repos filter reads automatically
app.UseMiddleware<BeeMemoryBank.Api.Middleware.CallerScopeMiddleware>();

// Validate MCP tool/call parameter names. The SDK silently drops unknown args,
// which sends weak models into guess-the-flag loops. We short-circuit with a
// schema-bearing error before the SDK sees the request.
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/mcp"),
    branch =>
    {
        // Session/identity guard runs first — "can this call even proceed" is a more
        // fundamental gate than "are the argument names spelled right."
        branch.UseMiddleware<BeeMemoryBank.Api.Middleware.McpSessionGuardMiddleware>();
        branch.UseMiddleware<BeeMemoryBank.Api.Middleware.McpParameterValidationMiddleware>();
    });

// Error handling
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        // The switch this used to inline now lives in ExceptionStatusMap so a test can assert the
        // type→status pairs directly, rather than the pairs being reachable only through a request.
        var (statusCode, message) = BeeMemoryBank.Api.Helpers.ExceptionStatusMap.Map(feature?.Error);
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(message));
    });
});

if (app.Environment.IsDevelopment())
    app.MapOpenApi();
    }

    public static void MapBeeApiEndpoints(this WebApplication app)
    {
app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }))
   .WithTags("Health");

app.MapGet("/api/version", () =>
{
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    var location = asm.Location;
    var deployedAt = File.Exists(location)
        ? File.GetLastWriteTimeUtc(location)
        : DateTime.UtcNow;
    // `version` is the compiled-in build version (source of truth for update checks).
    // `deployedAt`/`build` are kept for backward compatibility (older mobile/Maestro readers).
    return Results.Ok(new { version = BeeMemoryBank.Api.Helpers.AppVersion.Current, deployedAt, build = "2026-04-18" });
}).WithTags("Health").AllowAnonymous();

app.MapSessionEndpoints();
app.MapArticleEndpoints();
app.MapTreeEndpoints();
app.MapConceptTagEndpoints();
app.MapFolderEndpoints();
app.MapCopyEndpoints();
app.MapRemoteAuthEndpoints();
app.MapRemoteAccountEndpoints();
app.MapSearchEndpoints();
app.MapKeyEndpoints();
app.MapWhitelistEndpoints();
app.MapAgentEndpoints();
app.MapFavoriteEndpoints();
app.MapBrandingEndpoints();
app.MapUserEndpoints();
app.MapJoinEndpoints();
app.MapInitEndpoints();
app.MapSyncEndpoints();
app.MapSnapshotEndpoints();
    app.MapDekRotationEndpoints();
    app.MapUpdateEndpoints();
app.MapActivityEndpoints();
app.MapCommentEndpoints();
app.MapRestrictionEndpoints();
app.MapRoleEndpoints();
app.MapDownloadEndpoints();
    app.MapMediaEndpoints();
    app.MapVersionEndpoints();
    app.MapObsidianImportEndpoints();
    app.MapBeeImportEndpoints();
    app.MapHardDeleteEndpoints();
    app.MapCompactionEndpoints();
    app.MapAdminEndpoints();
    app.MapSearchMetricsEndpoints();
    app.MapInternetAccessEndpoints();
    app.MapAutoUnlockEndpoints();
    app.MapChatEndpoints();
    app.MapMcp("/mcp");

    }
}
