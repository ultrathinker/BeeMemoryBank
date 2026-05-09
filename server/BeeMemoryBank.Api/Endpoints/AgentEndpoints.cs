// Agents are a node-local identity. They are created, authenticated,
// and revoked per-node. They are never synchronized to other nodes.

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Endpoints;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/agents").WithTags("Agents");
        group.AddEndpointFilter<RequireNonAgentFilter>();

        // GET /api/agents — list active agents
        group.MapGet("/", async (HttpContext ctx, IAgentRepository repo, IUserRepository userRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var callerIdStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            var callerRole = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (!int.TryParse(callerIdStr, out var callerId) || callerId <= 0)
                return Results.Json(new ErrorResponse("User identification failed"), statusCode: 403);

            var agents = await repo.ListActiveAsync();

            // Filter for non-admins
            if (callerRole != UserRoles.Superadmin)
            {
                agents = agents.Where(a => a.OwnerUserId == callerId).ToList();
            }

            var users = (await userRepo.ListActiveAsync()).ToDictionary(u => u.Id);
            var items = agents.Select(a =>
            {
                users.TryGetValue(a.OwnerUserId, out var owner);
                return new AgentListItem(
                    a.Id, a.Name, a.Description, a.KeyPrefix + "****", a.CreatedAt, a.LastAccessedAt,
                    a.RequestCount, a.OwnerUserId, owner?.DisplayName ?? owner?.Username);
            }).ToList();
            return Results.Ok(items);
        });

        // POST /api/agents — create agent (requires unlocked session)
        group.MapPost("/", async (
            CreateAgentRequest req,
            IAgentRepository repo,
            IUserRepository userRepo,
            SessionService session,
            HttpContext ctx,
            IAuditLogRepository auditRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new ErrorResponse("Name is required"));

            var callerIdStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            if (!int.TryParse(callerIdStr, out var callerId) || callerId <= 0)
                return Results.BadRequest(new ErrorResponse("User identification failed"));

            var ownerUserId = callerId;

            // Check limit (20 agents)
            var count = await repo.CountByOwnerAsync(ownerUserId);
            if (count >= 20)
                return Results.BadRequest(new ErrorResponse("Agent limit reached (max 20)"));

            var owner = await userRepo.GetByIdAsync(ownerUserId);
            if (owner == null)
                return Results.BadRequest(new ErrorResponse($"User {ownerUserId} not found"));

            // Get Master DEK from session (already unlocked)
            byte[] masterDek;
            try { masterDek = session.GetMasterDek(); }
            catch { return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403); }

            try
            {
                var apiKey = AgentKeyHelper.GenerateApiKey();
                var (ciphertext, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, masterDek);

                var agent = new Agent
                {
                    Name = req.Name.Trim(),
                    Description = req.Description?.Trim(),
                    KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
                    KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                    EncryptedDek = ciphertext,
                    DekIV = iv,
                    Salt = salt,
                    KdfVersion = 1,
                    Status = "A",
                    CreatedAt = DateTime.UtcNow,
                    OwnerUserId = ownerUserId
                };

                agent.Id = await repo.CreateAsync(agent);

                // Agents are API keys with full vault access (scoped to owner's
                // ACL). Their lifecycle is security-critical — log creation +
                // deletion so a compromised superadmin can't silently provision
                // a backdoor agent.
                await auditRepo.LogAsync("agent", agent.Id.ToString(), "agent_created", "web",
                    $"Agent '{agent.Name}' (key prefix={agent.KeyPrefix}) created for user #{ownerUserId} by user {callerId}");

                return Results.Ok(new AgentCreatedResponse(agent.Id, agent.Name, apiKey));
            }
            finally
            {
                Array.Clear(masterDek);
            }
        });

        // DELETE /api/agents/{id} — soft delete
        group.MapDelete("/{id:int}", async (
            int id,
            IAgentRepository repo,
            SessionService session,
            HttpContext ctx,
            IAuditLogRepository auditRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var agent = await repo.GetByIdAsync(id);
            if (agent == null) return Results.NotFound();

            var callerIdStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            var callerRole = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (!int.TryParse(callerIdStr, out var callerId) || callerId <= 0)
                return Results.Json(new ErrorResponse("User identification failed"), statusCode: 403);

            if (callerRole != UserRoles.Superadmin && agent.OwnerUserId != callerId)
            {
                return Results.Json(new ErrorResponse("Forbidden: You do not own this agent"), statusCode: 403);
            }

            await repo.DeleteAsync(id);
            await auditRepo.LogAsync("agent", id.ToString(), "agent_deleted", "web",
                $"Agent '{agent.Name}' (owner=#{agent.OwnerUserId}) deleted by user {callerId}");

            return Results.NoContent();
        });
    }
}
