using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Api.Endpoints;

// Acting on advice (prompt, apply, dismiss, status) and the dismiss rules (package A1). Each change writes an audit line with
// ids, kinds and scopes only: titles, paths and tag names stay in the sealed payload.
public static partial class DreamEndpoints
{
    /// <param name="target">agent (default) or chat (DreamPromptTarget). 409 expired when a precondition no longer holds,
    /// 409 not_applicable when the advice is not open or the chat cannot carry its action out.</param>
    private static async Task<IResult> GetAdvicePromptAsync(HttpContext ctx, Guid id, string? target)
    {
        var promptTarget = DreamPromptTarget.Agent;
        if (target is not null && !DreamWire.TryParse(target, out promptTarget)) return BadRequest("Unknown target.");

        var services = ctx.RequestServices;
        try
        {
            if (await services.GetRequiredService<DreamStore>().GetAdviceAsync(id) is not { } advice) return NotFound("Advice");

            // An advice that was applied or dismissed still has its text for a personal agent, for reference: the person may want
            // to see what it asked. It is not checked again and nothing is settled; the chat gets nothing. An expired advice gets no
            // prompt: it no longer fits the notes, and the reason may be that a note went under a second password or a folder was
            // excluded, which a prompt must not name.
            var forReference = promptTarget == DreamPromptTarget.Agent && advice.Status is DreamAdviceStatus.Applied or DreamAdviceStatus.Dismissed;
            if (forReference)
                return Results.Ok(new DreamPromptResponse(services.GetRequiredService<DreamPromptBuilder>().Build(advice, promptTarget), promptTarget)
                {
                    Status = advice.Status,
                    ForReference = true,
                });

            if (await OpenAndStandingAsync(services, advice, CallerIdentity.Extract(ctx).UserId) is { } refusal) return refusal;
            if (promptTarget == DreamPromptTarget.Chat && !DreamPromptBuilder.ChatCanDo(advice.Payload.Action.Type))
                return Conflict("The built-in chat has no tools for this change: use the prompt for your own agent.", DreamErrorCodes.NotApplicable);

            // A trash prompt says who links to the copy now, not what the night saw (a fresh search, no cache).
            if (advice.Payload.Action is { Type: DreamActionType.TrashArticle, ArticleId: { } copy })
            {
                var now = await services.GetRequiredService<DreamReferenceChecker>().CheckAsync(copy, ctx.RequestAborted);
                advice = advice with { Payload = advice.Payload with { References = now } };
            }

            var prompt = services.GetRequiredService<DreamPromptBuilder>().Build(advice, promptTarget);
            return Results.Ok(new DreamPromptResponse(prompt, promptTarget) { Status = advice.Status });
        }
        catch (SessionLockedException)
        {
            return Locked();
        }
    }

    /// <summary>{ status, result }; 409 expired or not_applicable; the services' own refusals (403, 409) with their text.</summary>
    private static async Task<IResult> ApplyAdviceAsync(HttpContext ctx, Guid id)
    {
        var services = ctx.RequestServices;
        var caller = CallerIdentity.Extract(ctx);
        var advice = await services.GetRequiredService<DreamStore>().GetAdviceAsync(id);

        // Every real attempt is written down (it began on a new advice), also when it throws; a press on an advice that was
        // not new changed nothing. Counts and kinds only: what was done, by title, is in the sealed payload.
        async Task AuditAsync(bool applied, string what)
        {
            if (advice?.Status != DreamAdviceStatus.New) return;
            await services.GetRequiredService<IAuditLogRepository>().LogAsync("dream", id.ToString(),
                applied ? "dream_advice_applied" : "dream_advice_apply_failed", "web",
                $"Dream advice {id} ({DreamWire.ToWire(advice.Kind)}, {DreamWire.ToWire(advice.Payload.Action.Type)}) {what} by user {caller.UserId?.ToString() ?? "unknown"}");
        }

        DreamApplyOutcome outcome;
        try
        {
            // An Apply the person confirmed runs to its end even if the page goes away: a half-done change is worse than
            // a finished one nobody watched. Its length is bounded by the advice's own items.
            outcome = await services.GetRequiredService<DreamApplyService>().ApplyAsync(id, caller.UserId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await AuditAsync(false, $"not applied (failed: {ex.GetType().Name})");
            throw;
        }

        var applied = outcome.Kind == DreamApplyOutcomeKind.Applied;
        await AuditAsync(applied, applied ? "applied" : $"not applied ({OutcomeWord(outcome.Kind)}{(outcome.Partial is null ? "" : ", after part of it was done")})");

        // Pressed on an advice that was closed meanwhile (another tab, the sweep, a check that failed just now): say what
        // happened to it and when. A refusal of the services, or one that leaves the advice open, keeps its own words.
        if (outcome.Kind is DreamApplyOutcomeKind.Expired or DreamApplyOutcomeKind.NotApplicable && outcome.Partial is null
            && await services.GetRequiredService<DreamStore>().GetAdviceAsync(id) is { } now && DreamAdviceConflicts.IsClosed(now.Status))
            return DreamAdviceConflicts.For(now);

        return outcome.Kind switch
        {
            DreamApplyOutcomeKind.Applied => Results.Ok(new DreamApplyResponse(DreamAdviceStatus.Applied, outcome.Result)),
            DreamApplyOutcomeKind.NotFound => NotFound("Advice"),
            DreamApplyOutcomeKind.Expired => Conflict(outcome.Message ?? "This advice has expired.", DreamErrorCodes.Expired),
            DreamApplyOutcomeKind.NotApplicable => Conflict(outcome.Message ?? "This advice cannot be applied.", DreamErrorCodes.NotApplicable),
            DreamApplyOutcomeKind.Forbidden => Results.Json(new ErrorResponse(outcome.Message ?? "Access denied.", DreamErrorCodes.Forbidden), statusCode: 403),
            DreamApplyOutcomeKind.Locked => outcome.Partial is null ? Locked() : Conflict(outcome.Message!, DreamErrorCodes.Locked),
            _ => Conflict(outcome.Message ?? "The change was refused.", DreamErrorCodes.Refused),
        };
    }

    private static string OutcomeWord(DreamApplyOutcomeKind kind) => kind switch
    {
        DreamApplyOutcomeKind.Expired => "expired",
        DreamApplyOutcomeKind.NotApplicable => "not applicable",
        DreamApplyOutcomeKind.Forbidden => "access denied",
        DreamApplyOutcomeKind.Locked => "locked",
        _ => "refused",
    };

    /// <summary>The advice leaves the list and a rule keeps it (or its kind, about this thing or in this folder) from coming back.</summary>
    private static async Task<IResult> DismissAdviceAsync(HttpContext ctx, Guid id, DreamDismissRequest? request)
    {
        request ??= new DreamDismissRequest();
        var services = ctx.RequestServices;
        var store = services.GetRequiredService<DreamStore>();
        var caller = CallerIdentity.Extract(ctx);

        if (await store.GetAdviceAsync(id) is not { } advice) return NotFound("Advice");
        if (advice.Status is not (DreamAdviceStatus.New or DreamAdviceStatus.InChat)) return DreamAdviceConflicts.For(advice);

        var now = Clock(services);
        var (rule, error) = DreamDismissRules.Build(advice, request, caller.UserId, now);
        if (rule is null) return BadRequest(error!);

        await store.CreateRuleAsync(rule);
        if (!await store.TrySetAdviceStatusAsync(id, [DreamAdviceStatus.New, DreamAdviceStatus.InChat], DreamAdviceStatus.Dismissed, null, caller.UserId, now))
        {
            await store.DeleteRuleAsync(rule.Id);
            return await ChangedMeanwhileAsync(store, id);
        }

        await services.GetRequiredService<IAuditLogRepository>().LogAsync("dream", id.ToString(), "dream_advice_dismissed", "web",
            $"Dream advice {id} ({DreamWire.ToWire(advice.Kind)}) dismissed by user {caller.UserId?.ToString() ?? "unknown"}: rule {rule.Id} " +
            $"(scope={DreamWire.ToWire(rule.Scope)}, {(rule.ExpiresAt is null ? "for good" : "180 days")})");
        return Results.Ok(rule.ToDto());
    }

    /// <summary>
    /// in_chat (from new, after the strict check, only for what the chat can do), applied (Mark done, from new or in_chat)
    /// or new (from in_chat, or from dismissed: then the rule that Dismiss made is removed again). Anything else is 409.
    /// </summary>
    private static async Task<IResult> SetAdviceStatusAsync(HttpContext ctx, Guid id, DreamStatusChangeRequest request)
    {
        if (request.Status is not (DreamAdviceStatus.InChat or DreamAdviceStatus.Applied or DreamAdviceStatus.New))
            return BadRequest("The status can be set to in_chat, applied or new.");

        var services = ctx.RequestServices;
        var store = services.GetRequiredService<DreamStore>();
        var caller = CallerIdentity.Extract(ctx);
        if (await store.GetAdviceAsync(id) is not { } advice) return NotFound("Advice");

        var now = Clock(services);
        var from = advice.Status;
        var changed = false;
        switch (request.Status)
        {
            case DreamAdviceStatus.InChat:
                if (from != DreamAdviceStatus.New) return NotAllowed(advice, request.Status);
                if (advice.ApplyMode == DreamApplyMode.Prompt || !DreamPromptBuilder.ChatCanDo(advice.Payload.Action.Type))
                    return Conflict("The built-in chat has no tools for this change: use the prompt for your own agent.", DreamErrorCodes.NotApplicable);
                if (await OpenAndStandingAsync(services, advice, caller.UserId) is { } refusal) return refusal;
                changed = await store.TrySetAdviceStatusAsync(id, [DreamAdviceStatus.New], DreamAdviceStatus.InChat, null, caller.UserId, now);
                break;

            case DreamAdviceStatus.Applied:
                if (from is not (DreamAdviceStatus.New or DreamAdviceStatus.InChat)) return NotAllowed(advice, request.Status);
                changed = await store.TrySetAdviceStatusAsync(id, [from], DreamAdviceStatus.Applied, DreamStatusNotes.MarkedDone, caller.UserId, now);
                if (changed)
                    await store.UpdateAdvicePayloadAsync(id, advice.Payload with
                    {
                        Applied = new DreamAppliedInfo(now, caller.UserId ?? 0, from == DreamAdviceStatus.InChat ? "Marked as done after the chat." : "Marked as done by hand."),
                        LastError = null,
                    });
                break;

            case DreamAdviceStatus.New:
                if (from is not (DreamAdviceStatus.InChat or DreamAdviceStatus.Dismissed)) return NotAllowed(advice, request.Status);
                changed = await store.TrySetAdviceStatusAsync(id, [from], DreamAdviceStatus.New, null, caller.UserId, now);
                if (changed && from == DreamAdviceStatus.Dismissed)
                    foreach (var rule in (await store.ListRulesAsync()).Where(r => r.Payload.AdviceId == id))
                        await store.DeleteRuleAsync(rule.Id);
                break;
        }
        if (!changed) return await ChangedMeanwhileAsync(store, id);

        await services.GetRequiredService<IAuditLogRepository>().LogAsync("dream", id.ToString(), "dream_advice_status", "web",
            $"Dream advice {id} ({DreamWire.ToWire(advice.Kind)}) set from {DreamWire.ToWire(from)} to {DreamWire.ToWire(request.Status)} by user {caller.UserId?.ToString() ?? "unknown"}");
        var now2 = await store.GetAdviceAsync(id);
        return now2 is null ? Results.Ok(new { }) : Results.Ok(now2.ToDto());
    }

    private static async Task<IResult> ListRulesAsync(HttpContext ctx, CancellationToken ct)
    {
        try
        {
            var rules = await ctx.RequestServices.GetRequiredService<DreamStore>().ListRulesAsync();
            return Results.Ok(new DreamRuleListResponse(rules.Select(r => r.ToDto()).ToList()));
        }
        catch (SessionLockedException)
        {
            return Locked();
        }
    }

    private static async Task<IResult> DeleteRuleAsync(HttpContext ctx, Guid id)
    {
        var services = ctx.RequestServices;
        if (!await services.GetRequiredService<DreamStore>().DeleteRuleAsync(id)) return NotFound("Rule");

        var caller = CallerIdentity.Extract(ctx);
        await services.GetRequiredService<IAuditLogRepository>().LogAsync("dream", id.ToString(), "dream_rule_deleted", "web",
            $"Dream dismiss rule {id} deleted by user {caller.UserId?.ToString() ?? "unknown"}");
        return Results.Ok(new { });
    }

    // ── Shared ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Null when the advice is open and still stands (the strict check of plan 3.4, before a prompt or a hand-off), else the
    /// 409 to answer with; an advice that no longer stands is settled on the way (expired, or done after the chat).
    /// </summary>
    private static async Task<IResult?> OpenAndStandingAsync(IServiceProvider services, DreamAdviceRecord advice, int? userId)
    {
        if (advice.Status is not (DreamAdviceStatus.New or DreamAdviceStatus.InChat)) return DreamAdviceConflicts.For(advice);

        var validator = services.GetRequiredService<DreamAdviceValidator>();
        var check = await validator.CheckStrictAsync(advice);
        if (check.Ok) return null;
        var settled = await validator.SettleAsync(advice, check, userId);
        // Settled just now (expired, or done after the chat); one that another hand changed in between is said as it is.
        return DreamAdviceConflicts.IsClosed(settled.Status) ? DreamAdviceConflicts.For(settled) : Conflict(check.Problem!, DreamErrorCodes.Expired);
    }

    /// <summary>An advice that is closed is said as such (what happened, when), except that a dismissed one may be put back;
    /// a transition that is simply not one of plan 3.4 keeps its own words.</summary>
    private static IResult NotAllowed(DreamAdviceRecord advice, DreamAdviceStatus to) =>
        DreamAdviceConflicts.IsClosed(advice.Status)
            ? DreamAdviceConflicts.For(advice)
            : Conflict($"An advice that is {DreamWire.ToWire(advice.Status)} cannot be set to {DreamWire.ToWire(to)}.", DreamErrorCodes.NotApplicable);

    /// <summary>A conditional update found the advice changed by someone else a moment ago: what it is now.</summary>
    private static async Task<IResult> ChangedMeanwhileAsync(DreamStore store, Guid id) =>
        await store.GetAdviceAsync(id) is { } now && DreamAdviceConflicts.IsClosed(now.Status)
            ? DreamAdviceConflicts.For(now)
            : Conflict("This advice changed a moment ago.", DreamErrorCodes.NotApplicable);

    private static DateTime Clock(IServiceProvider services) =>
        (services.GetService<DreamEngineOptions>() ?? DreamEngineOptions.Default).Clock.GetUtcNow().UtcDateTime;
}
