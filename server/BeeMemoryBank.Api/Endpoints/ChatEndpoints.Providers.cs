using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static partial class ChatEndpoints
{
    /// <summary>
    /// The provider registry (docs/ai-providers.md). Every route is superadmin and never an agent (an agent key can neither read nor change
    /// providers or keys); everything that adds a provider, changes where it points or approves it also needs an unlocked vault (the approval
    /// is sealed under the chat key). A new provider, and one whose base URL, protocol or local flag changed, is not approved: nothing is
    /// sent to it until a superadmin confirms the exact host it names. Adding, approving, re-addressing and deleting a provider write an
    /// audit line.
    /// </summary>
    private static void MapProviderEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/providers", async (ChatProviderRepository providers, ChatSettingsRepository repo, ModelClientRegistry clients) =>
            Results.Ok(await ListProviderResponsesAsync(providers, repo, clients)))
            .RequireSuperadmin().RequireNonAgent();

        // The built-in list to pick from (only protocols this node has a client for).
        group.MapGet("/providers/presets", (ModelClientRegistry clients) =>
            Results.Ok(ProviderPresets.All.Where(p => clients.IsAvailable(p.Protocol))))
            .RequireSuperadmin().RequireNonAgent();

        group.MapPost("/providers", async (CreateProviderRequest req, ChatProviderRepository providers, ChatSettingsRepository repo,
            ModelClientRegistry clients, SessionService session, IAuditLogRepository audit, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Vault is locked"), statusCode: 409);

            var preset = req.PresetId is null ? null : ProviderPresets.Find(req.PresetId);
            if (req.PresetId is not null && preset is null)
                return Results.Json(new ErrorResponse($"Unknown preset '{req.PresetId}'."), statusCode: 400);

            var isLocal = req.IsLocal ?? preset?.Local ?? false;
            var (provider, error) = BuildProvider(
                id: Guid.NewGuid(),
                name: req.Name ?? preset?.Name,
                protocol: req.Protocol ?? preset?.Protocol ?? ProviderProtocols.OpenAiChat,
                baseUrl: req.BaseUrl ?? preset?.BaseUrl,
                isLocal: isLocal,
                options: req.Options ?? preset?.Options ?? ProviderWireOptions.DefaultFor(req.Protocol ?? preset?.Protocol),
                presetId: preset?.Id,
                clients);
            if (provider is null)
                return Results.Json(new ErrorResponse(error!), statusCode: 400);

            await providers.CreateAsync(provider);
            await audit.LogAsync("chat_provider", provider.Id.ToString(), "chat_provider_added", "web",
                $"Model provider '{provider.Name}' added by user {CallerIdentity.Extract(ctx).UserId?.ToString() ?? "unknown"} " +
                $"(host {ProviderUrl.DisplayHost(provider.BaseUrl)}, protocol {provider.Protocol}, local={provider.IsLocal}); not approved yet");
            var created = (await ListProviderResponsesAsync(providers, repo, clients)).First(p => p.Id == provider.Id);
            return Results.Created($"/api/chat/providers/{provider.Id}", created);
        }).RequireSuperadmin().RequireNonAgent();

        group.MapPatch("/providers/{id:guid}", async (Guid id, UpdateProviderRequest req, ChatProviderRepository providers,
            ChatSettingsRepository repo, ModelClientRegistry clients, SessionService session, IAuditLogRepository audit, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Vault is locked"), statusCode: 409);
            var before = await providers.GetAsync(id);
            if (before is null)
                return Results.NotFound(new ErrorResponse($"Provider {id} not found"));

            if (before.IsBuiltIn
                && (req.BaseUrl is not null && ProviderUrl.Normalize(req.BaseUrl, false).Url != before.BaseUrl
                    || req.Protocol is not null && req.Protocol != before.Protocol
                    || req.IsLocal is not null && req.IsLocal != before.IsLocal))
                return Results.Json(new ErrorResponse(
                    "The address of the built-in OpenRouter provider is fixed. Add a provider of its own for another address."), statusCode: 400);

            // A new protocol without options of its own gets that protocol's defaults (the old protocol's reasoning style is not its).
            var protocol = req.Protocol ?? before.Protocol;
            var options = req.Options ?? (protocol == before.Protocol ? before.Options : ProviderWireOptions.DefaultFor(protocol));
            var (after, error) = BuildProvider(before.Id, req.Name ?? before.Name, protocol, req.BaseUrl ?? before.BaseUrl,
                req.IsLocal ?? before.IsLocal, options, before.PresetId, clients);
            if (after is null)
                return Results.Json(new ErrorResponse(error!), statusCode: 400);
            if (!after.Options.ImageGeneration && before.Options.ImageGeneration
                && (await repo.ListModelsByProviderAsync(id)).Any(m => m.IsImageGen))
                return Results.Json(new ErrorResponse(
                    "Models of this provider are marked for image generation; untick that first."), statusCode: 400);

            await providers.UpdateAsync(before, after);
            var readdressed = before.Protocol != after.Protocol || before.BaseUrl != after.BaseUrl || before.IsLocal != after.IsLocal;
            if (readdressed)
                await audit.LogAsync("chat_provider", id.ToString(), "chat_provider_readdressed", "web",
                    $"Model provider '{after.Name}' changed by user {CallerIdentity.Extract(ctx).UserId?.ToString() ?? "unknown"} " +
                    $"to host {ProviderUrl.DisplayHost(after.BaseUrl)} (protocol {after.Protocol}, local={after.IsLocal}); its approval is withdrawn");
            return Results.Ok((await ListProviderResponsesAsync(providers, repo, clients)).First(p => p.Id == id));
        }).RequireSuperadmin().RequireNonAgent();

        // The approval names the host the person was shown ("Notes and chat content will be sent to <host>. Approve?"); if the provider
        // points somewhere else by the time it arrives, nothing is approved.
        group.MapPost("/providers/{id:guid}/approve", async (Guid id, ApproveProviderRequest req, ChatProviderRepository providers,
            ChatSettingsRepository repo, ModelClientRegistry clients, SessionService session, IAuditLogRepository audit, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Vault is locked"), statusCode: 409);
            var provider = await providers.GetAsync(id);
            if (provider is null)
                return Results.NotFound(new ErrorResponse($"Provider {id} not found"));
            if (provider.IsBuiltIn)
                return Results.Ok((await ListProviderResponsesAsync(providers, repo, clients)).First(p => p.Id == id));
            if (!clients.IsAvailable(provider.Protocol))
                return Results.Json(new ErrorResponse($"This node has no client for the '{provider.Protocol}' protocol yet."), statusCode: 400);
            var (url, error) = ProviderUrl.Normalize(provider.BaseUrl, provider.IsLocal);
            if (url is null || url != provider.BaseUrl)
                return Results.Json(new ErrorResponse(error ?? "The stored address is not valid; edit it first."), statusCode: 400);
            var host = ProviderUrl.DisplayHost(provider.BaseUrl);
            if (!string.Equals(req.Host?.Trim(), host, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new ErrorResponse(
                    $"The provider's address changed: it now sends to {host}. Reload the page and confirm that host."), statusCode: 409);

            var userId = CallerIdentity.Extract(ctx).UserId;
            await providers.ApproveAsync(provider, userId);
            await audit.LogAsync("chat_provider", id.ToString(), "chat_provider_approved", "web",
                $"Model provider '{provider.Name}' approved by user {userId?.ToString() ?? "unknown"}: notes and chat content may be sent to {host}");
            return Results.Ok((await ListProviderResponsesAsync(providers, repo, clients)).First(p => p.Id == id));
        }).RequireSuperadmin().RequireNonAgent();

        group.MapPost("/providers/{id:guid}/withdraw", async (Guid id, ChatProviderRepository providers, ChatSettingsRepository repo,
            ModelClientRegistry clients, IAuditLogRepository audit, HttpContext ctx) =>
        {
            var provider = await providers.GetAsync(id);
            if (provider is null)
                return Results.NotFound(new ErrorResponse($"Provider {id} not found"));
            if (provider.IsBuiltIn)
                return Results.Json(new ErrorResponse("The built-in OpenRouter provider cannot be withdrawn; delete it, or disable its keys."), statusCode: 400);
            await providers.WithdrawApprovalAsync(id);
            await audit.LogAsync("chat_provider", id.ToString(), "chat_provider_withdrawn", "web",
                $"Approval of model provider '{provider.Name}' withdrawn by user {CallerIdentity.Extract(ctx).UserId?.ToString() ?? "unknown"}");
            return Results.Ok((await ListProviderResponsesAsync(providers, repo, clients)).First(p => p.Id == id));
        }).RequireSuperadmin().RequireNonAgent();

        group.MapDelete("/providers/{id:guid}", async (Guid id, ChatProviderRepository providers, IAuditLogRepository audit, HttpContext ctx) =>
        {
            var provider = await providers.GetAsync(id);
            if (provider is null)
                return Results.NoContent();
            await providers.DeleteAsync(id);
            await audit.LogAsync("chat_provider", id.ToString(), "chat_provider_deleted", "web",
                $"Model provider '{provider.Name}' deleted with its keys and models by user {CallerIdentity.Extract(ctx).UserId?.ToString() ?? "unknown"}");
            return Results.NoContent();
        }).RequireSuperadmin().RequireNonAgent();

        // "Fetch models": the provider's own GET {base}/models, through the same egress guard, only for an approved provider and only on a
        // click. Uses the provider's first usable key (none for a local provider that has none).
        group.MapPost("/providers/{id:guid}/fetch-models", async (Guid id, ChatProviderRepository providers, ModelRouter router,
            SessionService session, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Vault is locked"), statusCode: 409);
            var provider = await providers.GetAsync(id);
            if (provider is null)
                return Results.NotFound(new ErrorResponse($"Provider {id} not found"));

            var probe = new ChatModelRow { Id = Guid.Empty, ProviderId = id, ModelId = "-", Label = "-" };
            var (route, problem) = await router.PrepareAsync(probe);
            if (route is null)
                return Results.Json(new ErrorResponse(problem!.Message), statusCode: 409);
            try
            {
                var models = await router.RunOnRouteAsync(route, (call, token) => route.Client.ListModelsAsync(call.Target, call.ApiKey, token),
                    ctx.RequestAborted);
                return Results.Ok(models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is AllKeysExhaustedException or ProviderUnavailableException
                                       || (ex is InvalidOperationException && ex is not Core.Exceptions.SessionLockedException))
            {
                return Results.Json(new ErrorResponse("The provider did not list its models: " + ex.Message), statusCode: 502);
            }
        }).RequireSuperadmin().RequireNonAgent();

        // The ordered fallback chain of the default text model (Dream's model uses the same chain after its own).
        group.MapGet("/settings/fallbacks", async (ChatSettingsRepository repo) =>
            Results.Ok(new { fallbackModelIds = await repo.GetFallbackModelIdsAsync() }))
            .RequireSuperadmin().RequireNonAgent();

        group.MapPatch("/settings/fallbacks", async (UpdateFallbacksRequest req, ChatSettingsRepository repo) =>
        {
            var ids = req.FallbackModelIds ?? [];
            if (ids.Count > ChatSettingsRepository.MaxFallbackModels)
                return Results.Json(new ErrorResponse($"At most {ChatSettingsRepository.MaxFallbackModels} fallback models."), statusCode: 400);
            if (ids.Distinct().Count() != ids.Count)
                return Results.Json(new ErrorResponse("A model is in the fallback chain twice."), statusCode: 400);
            foreach (var modelId in ids)
            {
                var model = await repo.GetModelByIdAsync(modelId);
                if (model is null)
                    return Results.Json(new ErrorResponse($"Model {modelId} not found."), statusCode: 400);
                if (!model.IsText)
                    return Results.Json(new ErrorResponse($"Model '{model.Label}' is not a text model."), statusCode: 400);
            }
            await repo.SetFallbackModelIdsAsync(ids);
            return Results.Ok(new { fallbackModelIds = ids });
        }).RequireSuperadmin().RequireNonAgent();
    }

    /// <summary>Checks and normalizes what a provider is made of; the provider, or the first problem as an English sentence.</summary>
    private static (ChatProvider? Provider, string? Error) BuildProvider(Guid id, string? name, string protocol, string? baseUrl, bool isLocal,
        ProviderWireOptions options, string? presetId, ModelClientRegistry clients)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return (null, "The provider's name is required.");
        if (name.Length > 100 || name.Any(char.IsControl)) return (null, "The provider's name must be at most 100 characters, with no control characters.");
        if (!ProviderProtocols.IsKnown(protocol))
            return (null, $"Unknown protocol '{protocol}'. Known: {string.Join(", ", ProviderProtocols.All)}.");
        if (!clients.IsAvailable(protocol))
            return (null, $"This node has no client for the '{protocol}' protocol yet.");
        if (options.Validate(protocol) is { } bad) return (null, bad);
        var (url, error) = ProviderUrl.Normalize(baseUrl, isLocal);
        if (url is null) return (null, error);
        var now = DateTime.UtcNow;
        return (new ChatProvider
        {
            Id = id, Name = name, PresetId = presetId, Protocol = protocol, BaseUrl = url, IsLocal = isLocal, OptionsJson = options.ToJson(),
            CreatedAt = now, UpdatedAt = now,
        }, null);
    }

    /// <summary>Every provider with its approval (checked against the sealed stamp), its key and model counts and the most recent error of
    /// its keys. Never a key.</summary>
    private static async Task<List<ProviderResponse>> ListProviderResponsesAsync(ChatProviderRepository providers, ChatSettingsRepository repo,
        ModelClientRegistry clients)
    {
        var list = await providers.ListAsync();
        var approvals = await providers.CheckApprovalsAsync(list);
        var keys = await repo.ListAsync();
        var models = await repo.ListAllModelsAsync();
        var now = DateTime.UtcNow;
        return list.Select((p, i) =>
        {
            var own = keys.Where(k => k.ProviderId == p.Id).ToList();
            var lastError = own.Where(k => k.LastError != null).OrderByDescending(k => k.LastUsedAt).Select(k => k.LastError).FirstOrDefault();
            var available = own.Count(k => k.Enabled && (k.DisabledUntil is null || k.DisabledUntil <= now));
            return new ProviderResponse(p.Id, p.Name, p.PresetId, p.Protocol, p.BaseUrl, ProviderUrl.DisplayHost(p.BaseUrl), p.IsLocal, p.IsBuiltIn,
                approvals[i] == true, approvals[i] is not null, p.ApprovedHost, p.ApprovedAt, p.Options, own.Count, available,
                models.Count(m => m.ProviderId == p.Id), lastError, ProviderPresets.Find(p.PresetId)?.Note, clients.IsAvailable(p.Protocol));
        }).ToList();
    }
}
