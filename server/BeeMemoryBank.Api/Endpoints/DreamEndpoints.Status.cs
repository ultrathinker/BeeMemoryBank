using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

// GET /api/dream/status, GET/PUT /api/dream/settings.
public static partial class DreamEndpoints
{
    /// <summary>The page's first call: whether Dream can run now, what it is doing, and what is waiting.</summary>
    private static async Task<IResult> GetStatusAsync(HttpContext ctx, CancellationToken ct)
    {
        var services = ctx.RequestServices;
        var session = services.GetRequiredService<SessionService>();
        if (!session.IsUnlocked)
        {
            var defaults = DreamSettings.Default;
            return Results.Ok(new DreamStatusResponse
            {
                Available = false,
                Reason = DreamErrorCodes.Locked,
                Settings = new DreamStatusSettings(defaults.ScheduleEnabled, defaults.UseModel, defaults.MaxAdviceScheduled, defaults.MaxAdviceManual),
            });
        }

        var store = services.GetRequiredService<DreamStore>();
        var coordinator = services.GetRequiredService<DreamCoordinator>();
        var chat = services.GetRequiredService<ChatSettingsRepository>();
        var corpus = services.GetRequiredService<DreamCorpusBuilder>();
        var settings = await store.GetSettingsAsync();

        var names = new DreamUserNames(services.GetRequiredService<IUserRepository>());
        DreamRunSummary? active = null;
        if (coordinator.ActiveRunId is { } activeId && await store.GetRunAsync(activeId) is { } activeRun)
            active = await SummaryAsync(activeRun, names);
        var lastRun = await store.GetLastFinishedRunAsync() is { } last ? await SummaryAsync(last, names) : null;

        var hasKey = await DreamModelResolver.HasUsableKeyAsync(chat);
        var model = await DreamModelResolver.ResolveAsync(chat, settings.AnalysisModelId);
        var chatDefaults = await chat.GetDefaultModelIdsAsync();
        var chatModel = await chat.ResolveEffectiveModelAsync("is_text", chatDefaults.TextId);
        var coverage = await corpus.GetEmbeddingCoverageAsync(settings);

        var busy = coordinator.IsHeavyOperationRunning;
        return Results.Ok(new DreamStatusResponse
        {
            Available = !busy,
            Reason = busy ? DreamErrorCodes.Busy : null,
            ActiveRun = active,
            LastRun = lastRun,
            OpenAdvice = await store.CountOpenAdviceAsync(),
            Settings = new DreamStatusSettings(settings.ScheduleEnabled, settings.UseModel, settings.MaxAdviceScheduled, settings.MaxAdviceManual),
            ModelReady = model is not null && hasKey,
            ModelLabel = model?.Label,
            ModelProblem = model is null ? DreamErrorCodes.NoModel : !hasKey ? DreamErrorCodes.NoKey : null,
            ChatAvailable = chatModel is not null && hasKey,
            Embeddings = new DreamEmbeddingsStatus(coverage.Total, coverage.Pending),
            NextScheduledAt = await NextScheduledAtAsync(store, settings),
        });
    }

    private static async Task<DateTime?> NextScheduledAtAsync(DreamStore store, DreamSettings settings)
    {
        if (!settings.ScheduleEnabled) return null;
        var now = DateTime.UtcNow;
        var zone = TimeZoneInfo.Local;
        // Inside a window that has not had its run, the run is due at the next step.
        if (DreamScheduleWindow.Latest(now, settings, zone) is { } window && now < window.EndUtc
            && !await store.HasScheduledRunInWindowAsync(window.StartUtc, window.EndUtc))
            return now;
        return DreamScheduleWindow.NextStart(now, settings, zone);
    }

    private static async Task<IResult> GetSettingsAsync(HttpContext ctx, CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<DreamStore>();
        var settings = await store.GetSettingsAsync();
        return Results.Ok(DreamSettingsResponse.From(settings, await ExcludedFoldersAsync(ctx.RequestServices, settings)));
    }

    /// <summary>The excluded folders that still exist, as id and path.</summary>
    private static async Task<IReadOnlyList<DreamFolderRef>> ExcludedFoldersAsync(IServiceProvider services, DreamSettings settings)
    {
        var folders = services.GetRequiredService<IFolderRepository>();
        var refs = new List<DreamFolderRef>();
        foreach (var id in settings.ExcludedFolderIds)
            if (await folders.GetByIdAsync(id) is { } folder)
                refs.Add(new DreamFolderRef(folder.Id, folder.Path));
        return refs;
    }

    private static async Task<IResult> PutSettingsAsync(HttpContext ctx, DreamSettingsUpdateRequest request)
    {
        var services = ctx.RequestServices;
        var folders = services.GetRequiredService<IFolderRepository>();
        var chat = services.GetRequiredService<ChatSettingsRepository>();

        var excludedPaths = request.ExcludedFolderPaths ?? [];
        if (excludedPaths.Count > DreamSettings.MaxExcludedFolders)
            return BadRequest($"At most {DreamSettings.MaxExcludedFolders} excluded folders.");

        var excluded = new List<Guid>();
        foreach (var raw in excludedPaths)
        {
            // Untrusted input naming a tree path: canonicalized before any lookup.
            var path = TreePathCanonicalizer.TryCanonicalize(raw);
            if (path is null || await folders.GetByPathAsync(path) is not { } folder)
                return BadRequest($"Unknown folder: {raw}");
            excluded.Add(folder.Id);
        }

        if (request.AnalysisModelId is { } modelId && await chat.GetModelByIdAsync(modelId) is not { IsText: true })
            return BadRequest("Unknown analysis model.");

        var settings = new DreamSettings
        {
            ScheduleEnabled = request.ScheduleEnabled,
            ScheduleStart = request.ScheduleStart,
            ScheduleWindowMinutes = request.ScheduleWindowMinutes,
            MaxAdviceScheduled = request.MaxAdviceScheduled,
            MaxAdviceManual = request.MaxAdviceManual,
            MaxOpenAdvice = request.MaxOpenAdvice,
            MinScore = request.MinScore,
            ManualMaxMinutes = request.ManualMaxMinutes,
            MaxModelCalls = request.MaxModelCalls,
            MaxTokens = request.MaxTokens,
            UseModel = request.UseModel,
            AnalysisModelId = request.AnalysisModelId,
            ExcludedFolderIds = excluded.Distinct().ToList(),
        };
        if (settings.Validate() is { } problem) return BadRequest(problem);

        var caller = CallerIdentity.Extract(ctx);
        var store = services.GetRequiredService<DreamStore>();
        await store.SaveSettingsAsync(settings, caller.UserId);
        await services.GetRequiredService<IAuditLogRepository>().LogAsync("dream", "settings", "dream_settings_changed", "web",
            $"Dream settings changed by user {caller.UserId?.ToString() ?? "unknown"} (schedule={settings.ScheduleEnabled}, useModel={settings.UseModel}, " +
            $"excludedFolders={settings.ExcludedFolderIds.Count})");

        var saved = await store.GetSettingsAsync();
        return Results.Ok(DreamSettingsResponse.From(saved, await ExcludedFoldersAsync(services, saved)));
    }

    /// <summary>Looks each user up once per request.</summary>
    private sealed class DreamUserNames(IUserRepository users)
    {
        private readonly Dictionary<int, DreamUserRef?> _known = [];

        public async Task<DreamUserRef?> OfAsync(int? id)
        {
            if (id is not { } userId) return null;
            if (_known.TryGetValue(userId, out var known)) return known;
            var user = await users.GetByIdAsync(userId);
            var result = user is null ? null : new DreamUserRef(userId, string.IsNullOrEmpty(user.DisplayName) ? user.Username : user.DisplayName);
            _known[userId] = result;
            return result;
        }
    }

    private static async Task<DreamRunSummary> SummaryAsync(DreamRunRecord run, DreamUserNames names) =>
        run.To<DreamRunSummary>(await names.OfAsync(run.StartedByUserId));
}
