using System.Diagnostics;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class DeployEndpoints
{
    public static void MapDeployEndpoints(this WebApplication app)
    {
        var deployScript = app.Configuration["BeeMemoryBank:DeployScript"]
            ?? Environment.GetEnvironmentVariable("BMB_DEPLOY_SCRIPT");

        if (string.IsNullOrEmpty(deployScript)) return;

        app.MapGet("/api/deploy/enabled", () => Results.Ok(new { enabled = true }))
            .WithTags("Deploy");

        app.MapPost("/api/deploy", async (
            DeployRequest req,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Only superadmin can deploy"), statusCode: 403);

            if (!session.IsUnlocked)
            {
                var unlocked = await session.UnlockAsync(req.Password);
                if (!unlocked)
                    return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 403);
            }

            try
            {
                // AUDIT NOTE: sudo usage is intentional and controlled. The command is hardcoded
                // (no user input injection possible). Access requires: InternalKeyValidator (BMB_INTERNAL_KEY
                // or localhost) + X-User-Role: superadmin (set by Web proxy) + master password verification.
                // The systemd unit beememorybank-deploy is a one-shot service, not arbitrary code execution.
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = "-c \"sudo /bin/systemctl start beememorybank-deploy\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };

                Process.Start(psi);
                return Results.Accepted(null, new
                {
                    message = "Deploy started. Services will restart in ~1-2 minutes.",
                    success = true
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponse("Deploy failed. Check server logs for details."), statusCode: 500);
            }
        }).WithTags("Deploy");
    }
}
