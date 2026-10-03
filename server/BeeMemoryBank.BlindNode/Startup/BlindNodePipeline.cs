using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.BlindNode.Startup;

/// <summary>
/// The blind node's middleware chain and endpoint map. Order in <see cref="UseBlindNodePipeline"/> IS the
/// behaviour. There is no agent auth (it would unlock a session with an agent's wrapped DEK - the one thing a
/// blind node must never hold), no caller scope (no users or agents to resolve) and no MCP: they are not
/// skipped by a role check, they are not in this assembly.
/// </summary>
public static class BlindNodePipeline
{
    public static void UseBlindNodePipeline(this WebApplication app)
    {
        // Error handling FIRST: an exception handler only sees exceptions thrown by middleware AFTER it, so every
        // gate below must sit inside its reach - a failure there is a mapped JSON error response, not the server
        // default page.
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                var (statusCode, message) = BeeMemoryBank.Api.Helpers.ExceptionStatusMap.Map(feature?.Error);
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsJsonAsync(new ErrorResponse(message));
            });
        });

        // An unpublished path should not consume a rate-limit slot, and the answer to "what is visible from the
        // internet" should live in the code, not only in a reverse proxy's configuration.
        BeeMemoryBank.Api.Middleware.PublicSurfaceMiddleware.LogStartupState();
        app.UseMiddleware<BeeMemoryBank.Api.Middleware.PublicSurfaceMiddleware>();

        // Rate limiting for the sensitive endpoints - brute-force protection.
        app.UseMiddleware<BeeMemoryBank.Api.Middleware.RateLimitMiddleware>();

        // Maintenance mode - blocks every request except the restore and seed flows.
        app.UseMiddleware<BeeMemoryBank.Api.Middleware.MaintenanceMiddleware>();
    }

    /// <summary>
    /// What a blind node serves from a store that cannot read: sync and its own /api/blind/* surface. Anything
    /// that is not mapped here is not served - including an endpoint added to the Api later.
    /// </summary>
    public static void MapBlindNodeSurface(this WebApplication app)
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
            // `version` is the compiled-in build version (source of truth for update checks); `deployedAt` and
            // `build` stay for older readers.
            return Results.Ok(new { version = BeeMemoryBank.Api.Helpers.AppVersion.Current, deployedAt, build = deployedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        }).WithTags("Health").AllowAnonymous();

        app.MapSyncEndpoints();
        app.MapBlindSeedEndpoints();
        app.MapBlindNodeEndpoints(); // BMB-54: blind status, backups, console, wipe
        app.MapBlindReplicaEndpoint();
        app.MapBlindRestoreEndpoints(); // BMB-53: restore codes, the restore package and events, the claim
    }
}
