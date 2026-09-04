using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        // Finding M8: clear any cached protected-article passphrases the instant the vault
        // locks, from ANY call site — not just the /lock handler below. SessionService.Locked
        // fires for node reset, snapshot/network restore, and the shutdown hook too, and all of
        // them equally invalidate a cached passphrase (it was only ever "trustworthy" for as
        // long as the session that verified it stayed unlocked). This runs once at startup,
        // for the lifetime of the singleton SessionService/ProtectedUnlockCache pair.
        var lockSubscriptionSession = app.Services.GetRequiredService<SessionService>();
        var unlockCacheForLockSubscription = app.Services.GetRequiredService<ProtectedUnlockCache>();
        lockSubscriptionSession.Locked += unlockCacheForLockSubscription.Clear;

        var group = app.MapGroup("/api/session").WithTags("Session").RequireInternalKey();

        group.MapPost("/unlock", async (UnlockRequest req, SessionService session) =>
        {
            var success = await session.UnlockAsync(req.Password);
            if (!success)
                return Results.Json(new ErrorResponse("Invalid password"), statusCode: 401);

            var migratedSynthetic = session.LastMigrationResult?.Migrated == true
                ? session.LastMigrationResult!.SyntheticAdminUsername
                : null;
            return Results.Ok(new UnlockResponse(true, migratedSynthetic));
        }).RequireNonAgent().WithMetadata(new SkipInternalKey());

        group.MapPost("/login", async (LoginRequest req, SessionService session, UserService userService, IUserRepository userRepo) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Json(new ErrorResponse("Username and password are required"), statusCode: 400);

            var allUsers = await userRepo.ListActiveAsync();
            if (allUsers.Count == 0)
            {
                // Legacy-node bootstrap path: nodes upgraded from before Phase A1/A2 have a legacy
                // "password" key slot but no users in tbl_user yet. Trying to Unlock with the
                // entered password triggers LegacyPasswordSlotMigrationService, which promotes
                // the legacy slot to a "user" slot bound to a freshly-created synthetic admin.
                // We then return a LoginResponse for that synthetic user.
                // Wrong password → unlock fails → generic 401 like normal failed login.
                var unlocked = await session.UnlockAsync(req.Password);
                if (!unlocked)
                    return Results.Json(new ErrorResponse("Invalid username or password"), statusCode: 401);

                var migration = session.LastMigrationResult;
                if (migration?.Migrated == true && migration.SyntheticAdminUsername != null)
                {
                    var synthUser = await userRepo.GetByUsernameAsync(migration.SyntheticAdminUsername);
                    if (synthUser != null)
                        return Results.Ok(new LoginResponse(
                            synthUser.Id, synthUser.Username, synthUser.DisplayName,
                            synthUser.Role, true, migration.SyntheticAdminUsername, synthUser.SecurityStamp));
                }
                // Defensive: unlock succeeded but no user was created — fresh post-Setup nodes
                // never reach here (Setup creates a user), so this is the bare-disk-no-init case.
                return Results.Json(new ErrorResponse("Node not initialized. Complete setup first."), statusCode: 400);
            }

            var user = await userService.AuthenticateAsync(req.Username, req.Password);
            if (user == null)
                return Results.Json(new ErrorResponse("Invalid username or password"), statusCode: 401);

            bool isUnlocked = session.IsUnlocked;

            if (user.Role == UserRoles.Superadmin)
            {
                if (!isUnlocked)
                {
                    isUnlocked = await session.UnlockAsync(req.Password);
                }

                // A user promoted to superadmin has no key slot yet — building one needs the
                // plaintext password, which the promoting admin never had. This login is the
                // first moment it is available, so provision the slot now. No-op once they
                // have one; skipped while the vault is locked (no master DEK to wrap), in
                // which case the next login retries.
                if (isUnlocked)
                    await userService.ProvisionMissingKeySlotAsync(user, req.Password);
            }
            else
            {
                if (!isUnlocked)
                    return Results.Json(
                        new ErrorResponse("Server is locked. Contact administrator.", ErrorCodes.SessionLocked),
                        statusCode: 403);
            }

            var migratedSynthetic = session.LastMigrationResult?.Migrated == true
                ? session.LastMigrationResult!.SyntheticAdminUsername
                : null;

            return Results.Ok(new LoginResponse(user.Id, user.Username, user.DisplayName, user.Role, isUnlocked, migratedSynthetic, user.SecurityStamp));
        }).WithMetadata(new SkipInternalKey());

        group.MapPost("/lock", (SessionService session) =>
        {
            session.Lock();
            return Results.Ok(new SessionStatusResponse(false));
        }).RequireSuperadmin().RequireNonAgent();

        group.MapGet("/status", (SessionService session) =>
            Results.Ok(new SessionStatusResponse(session.IsUnlocked))).WithMetadata(new SkipInternalKey());

        // GET /api/session/lock-impact — what would undo the Lock above, for the UI to state
        // before the click. Lock is advisory (SECURITY.md, "Trust Model"): it wipes the master
        // DEK, but an agent key owned by a superadmin carries its own wrapped copy, and
        // AgentAuthMiddleware unwraps it and re-unlocks the whole process on that key's NEXT
        // request — with a live MCP client attached, seconds later. Nothing in the interface said
        // so, which made Lock look stronger than it is.
        //
        // OS auto-unlock is reported alongside but is a DIFFERENT mechanism, not a second flavour
        // of the same one: OsAutoUnlockService is attempted once, at API startup, so it undoes a
        // RESTART of a locked node rather than a Lock inside a running process. The two are
        // reported separately so the UI can keep them apart too.
        //
        // RequireNonAgent as well as RequireSuperadmin, matching /lock itself and /api/keys/*: a
        // superadmin's agent inherits its owner's IsSuperadmin flag by design (it is how an agent
        // reads what its owner can read), so the superadmin gate alone would let a leaked bee_ key
        // enumerate every other key that opens this vault, and who owns it — a shopping list, and
        // one this endpoint exists precisely to help revoke. The answer would also be stale on
        // arrival: reaching this handler over a bee_ token means the middleware already used that
        // token to unlock the session it is being asked about.
        group.MapGet("/lock-impact", async (HttpContext ctx, IAgentRepository agentRepo, IUserRepository userRepo) =>
        {
            var owners = (await userRepo.ListActiveAsync()).ToDictionary(u => u.Id);

            var agents = (await agentRepo.ListActiveAsync())
                .Where(a => a.CanAutoUnlock)
                // An agent whose owner has been deactivated or deleted is refused with 401 before
                // the unlock is even attempted, so it can no longer undo a Lock and must not be
                // counted here. OwnerUserId <= 0 is the pre-migration-004 legacy shape: that path
                // skips owner resolution entirely and still auto-unlocks, so it does count.
                .Where(a => a.OwnerUserId <= 0 || owners.ContainsKey(a.OwnerUserId))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => new AutoUnlockAgentItem(
                    a.Id,
                    a.Name,
                    a.OwnerUserId,
                    owners.TryGetValue(a.OwnerUserId, out var owner)
                        ? owner.DisplayName ?? owner.Username
                        : null))
                .ToList();

            // Resolved from RequestServices rather than declared as a handler parameter for the
            // reason spelled out in AutoUnlockEndpoints: the service is only registered on
            // Windows, and minimal-API binding would silently infer [FromBody] for it elsewhere.
            var osEnabled = false;
            if (OperatingSystem.IsWindows())
            {
                var osSvc = ctx.RequestServices.GetService<OsAutoUnlockService>();
                if (osSvc != null)
                    osEnabled = await osSvc.IsEnabledAsync();
            }

            return Results.Ok(new LockImpactResponse(agents, osEnabled, OperatingSystem.IsWindows()));
        }).RequireSuperadmin().RequireNonAgent();

        // Admin-configurable web login cookie lifetime (Web project applies these to its
        // own CookieAuthenticationOptions — see BeeWebCookie config in Web's Program.cs).
        // Bearer-token (agent) access never touches this cookie at all, so it is intentionally
        // NOT part of this setting's scope — RequireNonAgent below just keeps write access to
        // human superadmins via the browser, same as /lock.
        group.MapGet("/settings", async (INodeIdentityRepository nodeRepo) =>
        {
            var (hours, sliding) = await nodeRepo.GetSessionSettingsAsync();
            return Results.Ok(new SessionSettingsResponse(hours, sliding));
        });

        group.MapPut("/settings", async (SessionSettingsRequest req, INodeIdentityRepository nodeRepo) =>
        {
            if (req.ExpireHours < 1 || req.ExpireHours > 24 * 30)
                return Results.Json(new ErrorResponse("expireHours must be between 1 and 720"), statusCode: 400);

            await nodeRepo.SetSessionSettingsAsync(req.ExpireHours, req.SlidingExpiration);
            return Results.Ok(new SessionSettingsResponse(req.ExpireHours, req.SlidingExpiration));
        }).RequireSuperadmin().RequireNonAgent();
    }
}
