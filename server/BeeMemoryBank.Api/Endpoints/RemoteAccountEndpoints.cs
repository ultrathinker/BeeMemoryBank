using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// Friend-side REST surface for managing remote accounts and subscriptions.
/// All endpoints require the InternalKey and an unlocked session (the
/// service needs the master DEK to encrypt/decrypt stored tokens).
/// </summary>
public static class RemoteAccountEndpoints
{
    public static void MapRemoteAccountEndpoints(this WebApplication app)
    {
        // Remote accounts are node-wide configuration: any guest could otherwise list
        // owner-configured mirrors, delete subscriptions, or mount a foreign owner's folder under
        // their own writable tree. Restrict the whole surface to superadmin until per-user
        // ownership is added (security review 2026-05-25). Stated once on the group — it used to
        // be the same comment plus the same header compare copy-pasted into all seven handlers.
        // RequireNonAgent alongside the role gate, to keep the reach these routes had before it.
        // The old check compared X-User-Role directly, which no agent ever sends, so every agent
        // was refused; CallerIdentity gives an agent its owner's role, so the filter alone would
        // hand a superadmin's MCP agent the ability to mint and delete credentials for other
        // people's nodes. Same pairing, and the same reason, as /api/keys.
        var group = app.MapGroup("/api/remote-accounts").WithTags("RemoteAccount")
            .RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        group.MapGet("/", async (RemoteAccountService svc) =>
        {
            var list = await svc.ListAccountsAsync();
            return Results.Ok(list.Select(a => new
            {
                id = a.Id,
                displayName = a.DisplayName,
                baseUrl = a.BaseUrl,
                username = a.RemoteUsername,
                tokenExpiresAt = a.TokenExpiresAt,
                lastSyncAt = a.LastSyncAt,
                lastSyncStatus = a.LastSyncStatus,
                lastError = a.LastError,
                createdAt = a.CreatedAt
            }));
        });

        group.MapPost("/", async (CreateRemoteAccountRequest req, RemoteAccountService svc, SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.BaseUrl) || string.IsNullOrWhiteSpace(req.Username))
                return Results.BadRequest(new ErrorResponse("baseUrl and username are required"));

            try
            {
                var account = await svc.CreateAsync(req.DisplayName, req.BaseUrl, req.Username, req.Password);
                return Results.Ok(new { id = account.Id, displayName = account.DisplayName });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 401);
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, RemoteAccountService svc) =>
        {
            await svc.DeleteAccountAsync(id);
            return Results.NoContent();
        });

        group.MapGet("/{id:guid}/accessible", async (Guid id, RemoteAccountService svc, SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            try
            {
                var folders = await svc.ListAccessibleAsync(id);
                return Results.Ok(new { folders });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new ErrorResponse(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 502);
            }
        });

        group.MapGet("/{id:guid}/subscriptions", async (Guid id, RemoteAccountService svc) =>
        {
            var subs = await svc.ListSubscriptionsForAccountAsync(id);
            return Results.Ok(subs);
        });

        group.MapPost("/subscriptions", async (AddRemoteSubscriptionRequest req, RemoteAccountService svc, SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.MountPath))
                return Results.BadRequest(new ErrorResponse("mountPath is required"));

            try
            {
                var sub = await svc.AddSubscriptionAsync(req.RemoteAccountId, req.RemoteFolderId, req.RemoteFolderPath, req.MountPath);
                return Results.Ok(new { id = sub.Id, mountPath = sub.MountPath });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        });

        group.MapDelete("/subscriptions/{id:guid}", async (Guid id, RemoteAccountService svc) =>
        {
            await svc.DeleteSubscriptionAsync(id);
            return Results.NoContent();
        });
    }
}
