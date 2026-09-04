// Node-local product name shown in the web header and the browser tab title.

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Api.Endpoints;

public static class BrandingEndpoints
{
    public static void MapBrandingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/branding").WithTags("Branding").RequireInternalKey();
        // Renaming the product is an operator action, and an agent bearer token must not reach it.
        group.AddEndpointFilter<RequireNonAgentFilter>();

        // GET /api/branding — readable by any internal caller, including the Web layer rendering
        // the header for a not-yet-signed-in visitor on the login page.
        group.MapGet("/", async (INodeIdentityRepository repo) =>
        {
            var stored = await repo.GetBrandNameAsync();
            return Results.Ok(new BrandingResponse(
                Branding.Resolve(stored),
                !string.IsNullOrWhiteSpace(stored),
                Branding.DefaultName));
        });

        // PUT /api/branding — superadmin only; null/blank restores the built-in name.
        group.MapPut("/", async (
            BrandingRequest req,
            INodeIdentityRepository repo,
            IAuditLogRepository auditRepo,
            HttpContext ctx) =>
        {
            var name = req.Name?.Trim();
            if (name is { Length: 0 }) name = null;

            if (name is not null)
            {
                if (name.Length > Branding.MaxNameLength)
                    return Results.BadRequest(new ErrorResponse(
                        $"Name must be at most {Branding.MaxNameLength} characters"));

                // The name goes into the page header and the <title>. Razor escapes it, but control
                // characters (newlines, tabs) would still mangle the tab title and the audit line,
                // and they can only ever be a copy-paste accident.
                if (name.Any(char.IsControl))
                    return Results.BadRequest(new ErrorResponse("Name must not contain control characters"));
            }

            await repo.SetBrandNameAsync(name);

            var callerId = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            await auditRepo.LogAsync("branding", "node", "branding_changed", "web",
                name is null
                    ? $"Product name reset to default by user {callerId}"
                    : $"Product name set to '{name}' by user {callerId}");

            return Results.Ok(new BrandingResponse(Branding.Resolve(name), name is not null, Branding.DefaultName));
        }).RequireSuperadmin();
    }
}
