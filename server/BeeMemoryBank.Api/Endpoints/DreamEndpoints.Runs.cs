using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Api.Endpoints;

// /api/dream/runs: start, stop, history, one run, its live advice feed and its journal.
public static partial class DreamEndpoints
{
    private const int MaxFeedLimit = 200;
    private const int MaxEventLimit = 1000;
    private const int MaxRunPageSize = 100;

    private const string NothingToRunText =
        "Turn on the built-in rules or the AI model: a run needs at least one of them (the model also needs a model and a usable key).";

    /// <summary>202 { runId }; 409 already_running, locked or busy; 400 when a limit is out of range or the model is unusable.</summary>
    private static async Task<IResult> StartRunAsync(HttpContext ctx, DreamStartRunRequest? request)
    {
        request ??= new DreamStartRunRequest();

        if (request.MaxAdvice is { } maxAdvice && (maxAdvice < DreamSettings.MaxAdviceManualRange.Min || maxAdvice > DreamSettings.MaxAdviceManualRange.Max))
            return BadRequest($"maxAdvice must be between {DreamSettings.MaxAdviceManualRange.Min} and {DreamSettings.MaxAdviceManualRange.Max}.");
        if (request.MaxMinutes is { } maxMinutes && (maxMinutes < DreamSettings.ManualMaxMinutesRange.Min || maxMinutes > DreamSettings.ManualMaxMinutesRange.Max))
            return BadRequest($"maxMinutes must be between {DreamSettings.ManualMaxMinutesRange.Min} and {DreamSettings.ManualMaxMinutesRange.Max}.");
        if (request.Sample is not null && request.Mode != DreamRunMode.Trial)
            return BadRequest("A sample is only for a trial run.");
        if (request.Sample is { Pairs: < 0 } or { Folders: < 0 })
            return BadRequest("A sample cannot be negative.");
        if (!request.UseRules && request.UseModel == false)
            return BadRequest(NothingToRunText);

        var caller = CallerIdentity.Extract(ctx);
        var coordinator = ctx.RequestServices.GetRequiredService<DreamCoordinator>();
        var result = await coordinator.StartAsync(new DreamStartCommand(
            DreamRunTrigger.Manual, request.Mode, caller.UserId, request.UseModel, request.ModelId, request.MaxAdvice, request.MaxMinutes,
            request.Phases, request.Sample, UseRules: request.UseRules));

        if (result.RunId is not { } runId)
            return result.Error switch
            {
                DreamStartError.Locked => Locked(),
                DreamStartError.AlreadyRunning => Conflict("A Dream run is already in progress.", DreamErrorCodes.AlreadyRunning),
                DreamStartError.Busy => Conflict("Another long operation is running on this node.", DreamErrorCodes.Busy),
                DreamStartError.NothingToRun => BadRequest(NothingToRunText),
                _ => BadRequest("The model is not ready: check the model catalog and the API keys."),
            };

        await ctx.RequestServices.GetRequiredService<IAuditLogRepository>().LogAsync("dream", runId.ToString(), "dream_run_started", "web",
            $"Dream run {runId} started by user {caller.UserId?.ToString() ?? "unknown"} (mode={DreamWire.ToWire(request.Mode)}, rules={(request.UseRules ? "on" : "off")})");
        return Results.Json(new DreamStartRunResponse(runId), statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>202; stopping a run that has ended is harmless.</summary>
    private static async Task<IResult> StopRunAsync(HttpContext ctx, Guid id)
    {
        var services = ctx.RequestServices;
        var store = services.GetRequiredService<DreamStore>();
        if (await store.GetRunAsync(id) is null) return NotFound("Run");

        await services.GetRequiredService<DreamCoordinator>().StopAsync(id, store);

        var caller = CallerIdentity.Extract(ctx);
        await services.GetRequiredService<IAuditLogRepository>().LogAsync("dream", id.ToString(), "dream_run_stopped", "web",
            $"Dream run {id} stopped by user {caller.UserId?.ToString() ?? "unknown"}");
        return Results.Json(new { }, statusCode: StatusCodes.Status202Accepted);
    }

    /// <param name="mode">normal, trial or all (DreamRunListMode).</param>
    private static async Task<IResult> ListRunsAsync(HttpContext ctx, int? page, int? pageSize, string? mode)
    {
        var listMode = DreamRunListMode.All;
        if (mode is not null && !DreamWire.TryParse(mode, out listMode)) return BadRequest("Unknown mode.");
        var (safePage, size) = Paging(page, pageSize, 10, MaxRunPageSize);

        var services = ctx.RequestServices;
        var (total, runs) = await services.GetRequiredService<DreamStore>().ListRunsAsync(listMode, safePage, size);
        var names = new DreamUserNames(services.GetRequiredService<IUserRepository>());
        var items = new List<DreamRunSummary>(runs.Count);
        foreach (var run in runs) items.Add(await SummaryAsync(run, names));
        return Results.Ok(new DreamRunListResponse(total, safePage, size, items));
    }

    private static async Task<IResult> GetRunAsync(HttpContext ctx, Guid id)
    {
        var services = ctx.RequestServices;
        var store = services.GetRequiredService<DreamStore>();
        if (await store.GetRunAsync(id) is not { } run) return NotFound("Run");

        var names = new DreamUserNames(services.GetRequiredService<IUserRepository>());
        var detail = run.To<DreamRunDetail>(await names.OfAsync(run.StartedByUserId)) with
        {
            AdviceCounts = await store.CountRunAdviceAsync(id),
            LogAvailable = await store.HasEventsAsync(id),
        };
        return Results.Ok(detail);
    }

    /// <summary>
    /// The live feed: advice of the run after <paramref name="afterSeq"/>, the run and its latest journal line. While the
    /// vault is locked there is no advice to show, but the run itself is plaintext and is still answered: the page must
    /// learn that its run ended with "locked".
    /// </summary>
    private static async Task<IResult> GetRunAdviceAsync(HttpContext ctx, Guid id, long? afterSeq, int? limit)
    {
        var services = ctx.RequestServices;
        var store = services.GetRequiredService<DreamStore>();
        if (await store.GetRunAsync(id) is not { } run) return NotFound("Run");

        var after = Math.Max(0, afterSeq ?? 0);
        IReadOnlyList<DreamAdviceRecord> advice = [];
        string? activity = null;
        try
        {
            advice = await store.ListRunAdviceAsync(id, after, Math.Clamp(limit ?? 50, 1, MaxFeedLimit));
            advice = await services.GetRequiredService<DreamAdviceValidator>().CheckListAsync(advice);
            activity = await store.GetLatestEventTextAsync(id);
        }
        catch (SessionLockedException)
        {
            // The page sees the run's state; the advice waits for the next unlock.
        }

        var names = new DreamUserNames(services.GetRequiredService<IUserRepository>());
        return Results.Ok(new DreamRunAdviceResponse(
            advice.Select(a => a.ToDto()).ToList(),
            advice.Count > 0 ? advice[^1].Seq : after,
            await SummaryAsync(run, names),
            activity));
    }

    private static async Task<IResult> GetRunEventsAsync(HttpContext ctx, Guid id, long? afterSeq, int? limit)
    {
        var store = ctx.RequestServices.GetRequiredService<DreamStore>();
        if (await store.GetRunAsync(id) is null) return NotFound("Run");

        var after = Math.Max(0, afterSeq ?? 0);
        try
        {
            var items = await store.ListEventsAsync(id, after, Math.Clamp(limit ?? 200, 1, MaxEventLimit));
            return Results.Ok(new DreamRunEventsResponse(items, items.Count > 0 ? items[^1].Seq : after, await store.HasEventsAsync(id)));
        }
        catch (SessionLockedException)
        {
            return Locked();
        }
    }

    private static (int Page, int PageSize) Paging(int? page, int? pageSize, int defaultSize, int maxSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? defaultSize, 1, maxSize));
}
