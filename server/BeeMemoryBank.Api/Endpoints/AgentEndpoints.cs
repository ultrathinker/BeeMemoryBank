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

        // GET /api/agents — list active agents
        group.MapGet("/", async (HttpContext ctx, IAgentRepository repo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var agents = await repo.ListActiveAsync();
            return Results.Ok(agents.Select(a => new AgentListItem(
                a.Id, a.Name, a.Description, a.KeyPrefix + "****", a.CreatedAt, a.LastAccessedAt, a.RequestCount)));
        });

        // POST /api/agents — create agent (requires unlocked session)
        group.MapPost("/", async (
            CreateAgentRequest req,
            IAgentRepository repo,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new ErrorResponse("Name is required"));

            // Get Master DEK from session (already unlocked)
            byte[] masterDek;
            try { masterDek = session.GetMasterDek(); }
            catch { return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403); }

            var apiKey = AgentKeyHelper.GenerateApiKey();
            var (ciphertext, iv) = AgentKeyHelper.EncryptDek(apiKey, masterDek);
            Array.Clear(masterDek);

            var agent = new Agent
            {
                Name = req.Name.Trim(),
                Description = req.Description?.Trim(),
                KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
                KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                EncryptedDek = ciphertext,
                DekIV = iv,
                Status = "A",
                CreatedAt = DateTime.UtcNow
            };

            agent.Id = await repo.CreateAsync(agent);

            return Results.Ok(new AgentCreatedResponse(agent.Id, agent.Name, apiKey));
        });

        // DELETE /api/agents/{id} — soft delete
        group.MapDelete("/{id:int}", async (
            int id,
            IAgentRepository repo,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            await repo.DeleteAsync(id);
            return Results.NoContent();
        });
    }
}
