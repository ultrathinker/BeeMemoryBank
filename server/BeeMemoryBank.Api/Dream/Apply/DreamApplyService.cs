using System.Globalization;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>How an Apply ended, for the endpoint to answer with.</summary>
public enum DreamApplyOutcomeKind
{
    Applied,
    NotFound,
    /// <summary>409 expired: a precondition no longer holds; the advice is expired now.</summary>
    Expired,
    /// <summary>409 not_applicable: not something Apply does (another apply mode, not new, being applied), other notes
    /// now name the copy to trash (the advice is handed to the chat instead), or this attempt was given up on.</summary>
    NotApplicable,
    /// <summary>403: the service refused for the caller's access (a denied or read-only folder, a remote copy).</summary>
    Forbidden,
    /// <summary>409: the service refused for the state of the vault; the advice is new again with lastError.</summary>
    Refused,
    /// <summary>409 locked: the vault locked under the Apply; the advice is new again.</summary>
    Locked,
}

/// <param name="Message">What to tell the person: the service's own words for a refusal.</param>
public sealed record DreamApplyOutcome(DreamApplyOutcomeKind Kind, DreamAdviceStatus? Status, string? Result, string? Message)
{
    /// <summary>What a many-item Apply had already done when it stopped (done, skipped, failed), or null when it did not
    /// stop part-way. It is in <see cref="Message"/> too, and in the advice's lastError.</summary>
    public string? Partial { get; init; }
}

/// <summary>
/// Apply by code (plan 3.10): in the request of the person who pressed it, under their access scope, through the
/// ordinary article, tag and folder services; never a direct repository write. Every change therefore leaves what any
/// change by that person leaves: the version of the note, the trash, the sync event, the /Activity line (marked "via
/// agent: dream", the way the chat marks its writes "chat").
///
/// <para>Order: the advice must be new and of apply mode code; it is taken into <c>applying</c> for an attempt token
/// (DreamStore.NewApplyAttempt), so a second press finds it taken; the strict check runs right before the action (an
/// advice that no longer stands is expired: 409 expired); the action; <c>applied</c> with <c>applied.result</c> saying what
/// was and what became, so a person can undo it by hand. A refusal of a service puts the advice back to <c>new</c> with
/// <c>lastError</c>.</para>
///
/// <para><b>One attempt at a time.</b> The ten-minute sweep gives up on an attempt that is still <c>applying</c> and
/// clears its token. Every change asks first whether the attempt still holds the advice
/// (DreamStore.StillApplyingAsync), and every write that ends it names the token (DreamStore.TryEndApplyingAsync), so an
/// attempt given up on stops at its next step and can never settle a newer one.</para>
///
/// <para><b>Many-item actions</b> (move_articles, merge_tags, delete_tags) succeed when one item does and list the rest
/// in the result. One stopped part-way (the vault locked, an unexpected failure, the attempt given up on) keeps what it
/// had done (done, skipped, failed) in the advice's lastError and the answer; the advice goes back to <c>new</c>, and
/// pressing Apply again does the rest: the items already done no longer meet their preconditions and are skipped.</para>
///
/// <para><b>The last look before a delete.</b> A tag is deleted only if it is still unused, and a folder only if it is
/// still the same empty folder, asked the moment before the service is called (the services offer no lock or transaction
/// a caller could join). A duplicate is trashed only after a fresh reference search (DreamReferenceChecker) right before.</para>
/// </summary>
public sealed class DreamApplyService(
    DreamStore store,
    DreamAdviceValidator validator,
    DreamReferenceChecker references,
    ArticleService articles,
    ConceptTagService tags,
    FolderService folders,
    IHttpContextAccessor http,
    ILogger<DreamApplyService> logger,
    DreamEngineOptions? options = null,
    DreamApplyHooks? hooks = null)
{
    private const string TakenOver = "This Apply ran for more than ten minutes and was given up on, so it stopped.";

    private readonly DreamEngineOptions _options = options ?? DreamEngineOptions.Default;

    private DateTime Now => _options.Clock.GetUtcNow().UtcDateTime;

    /// <summary>True for an action Apply carries out (apply_mode code).</summary>
    public static bool CanApply(DreamActionType type) => type is DreamActionType.MoveArticle or DreamActionType.MoveArticles
        or DreamActionType.TrashArticle or DreamActionType.MergeTags or DreamActionType.DeleteTags or DreamActionType.DeleteFolder;

    public async Task<DreamApplyOutcome> ApplyAsync(Guid adviceId, int? userId, CancellationToken ct)
    {
        var advice = await store.GetAdviceAsync(adviceId);
        if (advice is null) return new(DreamApplyOutcomeKind.NotFound, null, null, "Advice not found.");
        if (advice.Status == DreamAdviceStatus.Expired)
            return new(DreamApplyOutcomeKind.Expired, advice.Status, null, advice.StatusNote ?? "This advice has expired.");
        if (advice.ApplyMode != DreamApplyMode.Code || !CanApply(advice.Payload.Action.Type))
            return new(DreamApplyOutcomeKind.NotApplicable, advice.Status, null, "This advice is not applied by code: use the chat or the prompt.");
        if (advice.Status != DreamAdviceStatus.New)
            return new(DreamApplyOutcomeKind.NotApplicable, advice.Status, null, advice.Status == DreamAdviceStatus.Applying
                ? "This advice is being applied right now."
                : "Only a new advice can be applied.");

        // The double-press guard: of two presses only one takes the advice out of new, with its own token.
        var attempt = DreamStore.NewApplyAttempt();
        if (!await store.TryClaimApplyingAsync(adviceId, attempt, userId, Now))
        {
            var now = await store.GetAdviceAsync(adviceId);
            return new(DreamApplyOutcomeKind.NotApplicable, now?.Status, null, "This advice is being applied or was changed a moment ago.");
        }
        advice = advice with { Status = DreamAdviceStatus.Applying, StatusNote = attempt };
        var run = new Attempt(advice, attempt, userId);

        try
        {
            var check = await validator.CheckStrictAsync(advice);
            if (!check.Ok)
            {
                await EndAsync(run, DreamAdviceStatus.Expired, check.Problem);
                return new(DreamApplyOutcomeKind.Expired, DreamAdviceStatus.Expired, null, check.Problem);
            }

            var result = await RunMarkedAsync(() => ActAsync(run, check, ct));
            if (await EndAsync(run, DreamAdviceStatus.Applied, null, advice.Payload with { Applied = new DreamAppliedInfo(Now, userId ?? 0, result), LastError = null }))
                return new(DreamApplyOutcomeKind.Applied, DreamAdviceStatus.Applied, result, null);

            // The change is made, but the advice is no longer this attempt's to mark: say both.
            logger.LogWarning("Dream Apply of advice {AdviceId} finished after its attempt was given up on", adviceId);
            return new(DreamApplyOutcomeKind.Applied, (await store.GetAdviceAsync(adviceId))?.Status,
                $"{result} This Apply ran for more than ten minutes and was given up on, so the advice was not marked applied.", null);
        }
        catch (DreamHandedToChatException ex)
        {
            await EndAsync(run, DreamAdviceStatus.New, null, advice.Payload with { References = ex.References, LastError = ex.Message }, DreamApplyMode.Chat);
            return new(DreamApplyOutcomeKind.NotApplicable, DreamAdviceStatus.New, null, ex.Message);
        }
        catch (DreamStaleException ex)
        {
            // What the strict check saw changed in the moment before the delete: the advice no longer stands.
            await EndAsync(run, DreamAdviceStatus.Expired, ex.Message);
            return new(DreamApplyOutcomeKind.Expired, DreamAdviceStatus.Expired, null, ex.Message);
        }
        catch (DreamAttemptLostException)
        {
            // Given up on by the sweep, perhaps taken by a newer attempt: this one touches the advice no more.
            var partial = run.Progress.Describe();
            return new(DreamApplyOutcomeKind.NotApplicable, (await store.GetAdviceAsync(adviceId))?.Status, null, WithPartial(TakenOver, partial)) { Partial = partial };
        }
        catch (Exception ex) when (Refusal(ex) is { } refusal)
        {
            var partial = run.Progress.Describe();
            var message = WithPartial(refusal.Message, partial);
            await ReturnToNewAsync(run, message);
            return new(refusal.Kind, DreamAdviceStatus.New, null, message) { Partial = partial };
        }
        catch (DreamItemsRefusedException ex)
        {
            var partial = run.Progress.Describe();
            var message = WithPartial(ex.Message, partial);
            await ReturnToNewAsync(run, message);
            return new(ex.Forbidden ? DreamApplyOutcomeKind.Forbidden : DreamApplyOutcomeKind.Refused, DreamAdviceStatus.New, null, message) { Partial = partial };
        }
        catch (Exception ex)
        {
            // Anything else is not the person's to fix; the advice must not stay taken until the ten-minute sweep, and what
            // was already done must not be lost with the exception.
            logger.LogError(ex, "Dream Apply of advice {AdviceId} failed", adviceId);
            await ReturnToNewAsync(run, WithPartial($"The change failed ({ex.GetType().Name}).", run.Progress.Describe()));
            throw;
        }
    }

    // ── One attempt ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The advice as this attempt took it, its token, who pressed, and what it has done so far.</summary>
    private sealed record Attempt(DreamAdviceRecord Advice, string Token, int? UserId)
    {
        public DreamApplyProgress Progress { get; } = new();
    }

    /// <summary>Asked before every change: throws when the attempt no longer holds the advice. <paramref name="what"/> names
    /// the change for the test seam (DreamApplyHooks), which runs first.</summary>
    private async Task OwnAsync(Attempt run, string what)
    {
        if (hooks?.BeforeChange is { } before) await before(what);
        if (!await store.StillApplyingAsync(run.Advice.Id, run.Token)) throw new DreamAttemptLostException();
    }

    /// <summary>Ends the attempt (fenced by its token). Without the chat key the payload cannot be sealed: the status
    /// still changes, the words are lost. False when the attempt no longer held the advice.</summary>
    private async Task<bool> EndAsync(Attempt run, DreamAdviceStatus to, string? note, DreamAdvicePayload? payload = null, DreamApplyMode? mode = null)
    {
        try
        {
            return await store.TryEndApplyingAsync(run.Advice.Id, run.Token, to, note, run.UserId, Now, payload, mode);
        }
        catch (Exception ex) when (payload is not null && ex is SessionLockedException or VaultSealedException)
        {
            return await store.TryEndApplyingAsync(run.Advice.Id, run.Token, to, note, run.UserId, Now, null, mode);
        }
    }

    private Task<bool> ReturnToNewAsync(Attempt run, string error) =>
        EndAsync(run, DreamAdviceStatus.New, null, run.Advice.Payload with { LastError = error });

    private static string WithPartial(string message, string? partial) =>
        partial is null ? message : $"{message} It had already done part of the change. {partial}";

    // ── The action ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<string> ActAsync(Attempt run, DreamCheckResult check, CancellationToken ct)
    {
        var advice = run.Advice;
        var action = advice.Payload.Action;
        switch (action.Type)
        {
            case DreamActionType.MoveArticle:
            {
                var id = action.ArticleId!.Value;
                var from = (await articles.GetMetadataAsync(id))?.TreePath ?? ObjectOf(advice, id)?.TreePath;
                var to = TreePathCanonicalizer.Canonicalize(action.ToPath!);
                await OwnAsync(run, DreamAdviceValidator.Item(id));
                await articles.MoveAsync(id, to);
                return $"Moved {TitleOf(advice, id)} from {from ?? "its folder"} to {to}.";
            }
            case DreamActionType.TrashArticle:
            {
                var id = action.ArticleId!.Value;
                await RefuseIfLinkedNowAsync(advice, ct);
                var from = (await articles.GetMetadataAsync(id))?.TreePath;
                await OwnAsync(run, DreamAdviceValidator.Item(id));
                await articles.DeleteAsync(id);
                return $"Moved {TitleOf(advice, id)}{(from is null ? "" : " (from " + from + ")")} to the trash, where it can be restored for 30 days. Kept {TitleOf(advice, action.KeepId!.Value)}.";
            }
            case DreamActionType.MoveArticles:
            {
                var to = TreePathCanonicalizer.Canonicalize(action.ToPath!);
                var from = new SortedSet<string>(StringComparer.Ordinal);
                var ids = action.ArticleIds ?? [];
                await EachAsync(run, ids.Select(id => new Item(DreamAdviceValidator.Item(id), TitleOf(advice, id), "", id)).ToList(), check,
                    "changed or gone since the advice",
                    async item =>
                    {
                        if ((await articles.GetMetadataAsync(item.Id))?.TreePath is { } path) from.Add(path);
                        await articles.MoveAsync(item.Id, to);
                        return null;
                    }, ct);
                var p = run.Progress;
                var result = $"Moved {p.Done.Count.ToString(CultureInfo.InvariantCulture)} note(s){(from.Count > 0 ? " from " + string.Join(", ", from) : "")} to {to}: {string.Join(", ", p.Done)}.";
                return p.NotDone.Count == 0 ? result : $"{result} Not moved: {string.Join("; ", p.NotDone)}.";
            }
            case DreamActionType.MergeTags:
            {
                await EachAsync(run, (action.Sources ?? []).Select(TagOf).ToList(), check,
                    "already gone",
                    async item =>
                    {
                        await tags.MergeAsync(item.Name, action.Target!);
                        return null;
                    }, ct);
                var p = run.Progress;
                var result = $"Merged the tag(s) {string.Join(", ", p.Done)} into \"{action.Target}\".";
                return p.NotDone.Count == 0 ? result : $"{result} Left alone: {string.Join("; ", p.NotDone)}.";
            }
            case DreamActionType.DeleteTags:
            {
                await EachAsync(run, (action.Names ?? []).Select(TagOf).ToList(), check,
                    "in use or gone now",
                    async item =>
                    {
                        // The last look: a tag given to a note since the strict check is left alone.
                        var uses = await validator.TagUsesNowAsync(item.Name);
                        if (uses is null) return "gone now";
                        if (uses > 0) return "given to a note now";
                        await tags.DeleteAsync(item.Name);
                        return null;
                    }, ct);
                var p = run.Progress;
                var result = $"Deleted the unused tag(s) {string.Join(", ", p.Done)}.";
                return p.NotDone.Count == 0 ? result : $"{result} Left alone: {string.Join("; ", p.NotDone)}.";
            }
            case DreamActionType.DeleteFolder:
            {
                await OwnAsync(run, action.Path!);

                // The last look: still the same folder, still empty, the moment before the service deletes its subtree.
                if (await validator.FolderProblemNowAsync(action.FolderId!.Value, action.Path!) is { } problem) throw new DreamStaleException(problem);
                await folders.DeleteAsync(action.FolderId!.Value);
                return $"Deleted the empty folder {action.Path}; it can be restored from the trash.";
            }
            default:
                throw new InvalidOperationException("This advice is not applied by code.");
        }
    }

    /// <summary>
    /// The reference check right before a copy goes to the trash, fresh (DreamReferenceChecker bypasses the search
    /// cache): a note that links to it by id since the night, or a check that cannot run now, hands the advice to the chat,
    /// which re-points the links first (plan 3.10).
    /// </summary>
    private async Task RefuseIfLinkedNowAsync(DreamAdviceRecord advice, CancellationToken ct)
    {
        var found = await references.CheckAsync(advice.Payload.Action.ArticleId!.Value, ct);
        if (found is { Checked: true, Count: 0 }) return;
        throw new DreamHandedToChatException(found, found.Checked
            ? $"{found.Count.ToString(CultureInfo.InvariantCulture)} other note(s) now link to this note by its id, so it is not trashed by code: use Open in AI chat or the prompt, which point those links to the note that stays first."
            : "Whether other notes link to this note cannot be checked now, so it is not trashed by code: use Open in AI chat or the prompt.");
    }

    /// <summary>One item of a many-item action: its key in DreamCheckResult.SkippedItems, how the result names it, and the
    /// tag name or note id it is.</summary>
    private sealed record Item(string Key, string Label, string Name, Guid Id);

    private static Item TagOf(string name) => new(DreamAdviceValidator.TagItem(name), $"\"{name}\"", name, Guid.Empty);

    /// <summary>
    /// Runs <paramref name="one"/> per item, asking before each whether the attempt still holds the advice. An item the
    /// strict check already let go is skipped with <paramref name="skippedWhy"/>; <paramref name="one"/> returns null when
    /// it did its item, or why it left it alone; a refusal of the services is listed as failed. Every outcome goes into the
    /// attempt's progress as it happens, so an exception that escapes leaves an exact account behind. When nothing at all
    /// was done the whole Apply fails: refused if a service refused, expired if every item had stopped standing.
    /// </summary>
    private async Task EachAsync(Attempt run, IReadOnlyList<Item> items, DreamCheckResult check, string skippedWhy,
        Func<Item, Task<string?>> one, CancellationToken ct)
    {
        var progress = run.Progress;
        DreamItemsRefusedException? firstRefusal = null;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (check.SkippedItems.Contains(item.Key))
            {
                progress.Skipped.Add($"{item.Label} ({skippedWhy})");
                continue;
            }
            await OwnAsync(run, item.Key);
            try
            {
                if (await one(item) is { } why) progress.Skipped.Add($"{item.Label} ({why})");
                else progress.Done.Add(item.Label);
            }
            catch (Exception ex) when (Refusal(ex) is { Kind: not DreamApplyOutcomeKind.Locked } refusal)
            {
                progress.Failed.Add($"{item.Label} ({refusal.Message})");
                firstRefusal ??= new DreamItemsRefusedException(refusal.Message, refusal.Kind == DreamApplyOutcomeKind.Forbidden);
            }
        }
        if (progress.Done.Count > 0) return;
        if (firstRefusal is not null) throw firstRefusal;
        throw new DreamStaleException(DreamAdviceValidator.Reasons.NothingLeft);
    }

    /// <summary>
    /// Runs the action with the request marked the way the chat marks its writes, so that /Activity and the version of a
    /// note say "via agent: dream" next to the person's name. The mark is removed afterwards; one set by an outer caller
    /// is left alone.
    /// </summary>
    private async Task<string> RunMarkedAsync(Func<Task<string>> act)
    {
        var ctx = http.HttpContext;
        if (ctx is null || ctx.Items.ContainsKey(ChatToolDispatcher.ChatActorItemsKey)) return await act();
        ctx.Items[ChatToolDispatcher.ChatActorItemsKey] = DreamNames.AgentName;
        try
        {
            return await act();
        }
        finally
        {
            ctx.Items.Remove(ChatToolDispatcher.ChatActorItemsKey);
        }
    }

    /// <summary>A refusal of the services that the person can act on, or null for anything else.</summary>
    private static (DreamApplyOutcomeKind Kind, string Message)? Refusal(Exception ex) => ex switch
    {
        SessionLockedException or VaultSealedException => (DreamApplyOutcomeKind.Locked, "The vault is locked."),
        UnauthorizedAccessException => (DreamApplyOutcomeKind.Forbidden, ex.Message),
        KeyNotFoundException => (DreamApplyOutcomeKind.Refused, "Something it is about no longer exists."),
        MetadataKeyPublishPendingException => null,
        InvalidOperationException or ArgumentException => (DreamApplyOutcomeKind.Refused, ex.Message),
        _ => null,
    };

    private static DreamAdviceObject? ObjectOf(DreamAdviceRecord advice, Guid id) =>
        advice.Payload.Objects.FirstOrDefault(o => o.Type == DreamObjectType.Article && o.Id == id);

    private static string TitleOf(DreamAdviceRecord advice, Guid id) =>
        ObjectOf(advice, id)?.Title is { } title ? $"\"{title}\"" : $"the note {id:D}";
}

/// <summary>What a many-item Apply has done so far, item by item: done, skipped (with why), failed (with the refusal).</summary>
internal sealed class DreamApplyProgress
{
    public List<string> Done { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<string> Failed { get; } = [];

    /// <summary>Skipped and failed together, in the words of the result.</summary>
    public List<string> NotDone => Skipped.Concat(Failed).ToList();

    /// <summary>The account of a run that stopped part-way, or null when nothing was done yet.</summary>
    public string? Describe()
    {
        if (Done.Count == 0) return null;
        var parts = new List<string> { $"Done: {string.Join(", ", Done)}." };
        if (Skipped.Count > 0) parts.Add($"Skipped: {string.Join("; ", Skipped)}.");
        if (Failed.Count > 0) parts.Add($"Failed: {string.Join("; ", Failed)}.");
        parts.Add("Apply again to do the rest.");
        return string.Join(" ", parts);
    }
}

/// <summary>Every item of a many-item Apply was refused; the message is the first refusal's.</summary>
internal sealed class DreamItemsRefusedException(string message, bool forbidden) : Exception(message)
{
    public bool Forbidden { get; } = forbidden;
}

/// <summary>The attempt no longer holds the advice (the ten-minute sweep gave up on it): it must change nothing more.</summary>
internal sealed class DreamAttemptLostException() : Exception("The Apply attempt no longer holds the advice.");

/// <summary>What the strict check saw changed the moment before a delete; the message is the expiry reason.</summary>
internal sealed class DreamStaleException(string reason) : Exception(reason);

/// <summary>Other notes link to the copy by id now (or the check cannot run): the advice goes to the chat.</summary>
internal sealed class DreamHandedToChatException(DreamReferences references, string message) : Exception(message)
{
    public DreamReferences References { get; } = references;
}

/// <summary>
/// A test seam, never registered by the product: lets an integration test act at the moment before each change an Apply
/// makes (give a tag to a note, put a note into the folder, let the sweep give the attempt up, lock the vault), to show
/// what the last look and the attempt token do. <c>what</c> is the item's key: a note id ("D"), a normalized tag name, or
/// the folder path.
/// </summary>
public sealed class DreamApplyHooks
{
    public Func<string, Task>? BeforeChange { get; init; }
}
