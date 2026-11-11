using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Embeddings;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// One run, in its own DI scope under the system access scope: phases, limits, registry, rules, repeats, events and
/// counters (plan 3.5). It only reads the vault and writes its own dream_* rows: the articles, tags and folders change
/// only when a person presses a button. Scoped: one instance serves one run.
/// </summary>
public sealed class DreamRunner(
    DreamStore store,
    DreamCorpusBuilder corpusBuilder,
    IEnumerable<IDreamDetector> detectors,
    IEnumerable<IDreamModelTask> modelTasks,
    IDreamModelClient modelClient,
    CallerScopeHolder scopeHolder,
    SessionService session,
    LongOperationGate operations,
    MaintenanceModeService maintenance,
    IServiceProvider services,
    ILogger<DreamRunner> logger,
    DreamEngineOptions? options = null)
{
    private readonly DreamEngineOptions _options = options ?? DreamEngineOptions.Default;
    private readonly HashSet<string> _knownFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DreamOpenAdviceEntry>> _openNearDuplicates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenCandidates = new(StringComparer.Ordinal);

    private DreamRunPlan _plan = null!;
    private DreamRunControl _control = null!;
    private DreamRunProgress _progress = null!;
    private DreamRunContext _ctx = null!;
    private DreamCorpus _corpus = null!;
    private DreamRuleSet _rules = new([]);
    private IDreamRunLog _journal = null!;

    private TimeProvider Clock => _options.Clock;
    private DreamThresholds Thresholds => _options.Thresholds;
    private DateTime Now => Clock.GetUtcNow().UtcDateTime;
    private CancellationToken Token => _control.Token;

    // Started outside any request, the system scope is what the run would have anyway; said out loud because the whole run
    // depends on it, and through RunAsSystemAsync so the scope the runner found is back when the run ends.
    public Task<DreamRunResult> RunAsync(DreamRunPlan plan, DreamRunControl control, DreamRunProgress progress) =>
        scopeHolder.RunAsSystemAsync(() => RunAsSystemCoreAsync(plan, control, progress));

    private async Task<DreamRunResult> RunAsSystemCoreAsync(DreamRunPlan plan, DreamRunControl control, DreamRunProgress progress)
    {
        _plan = plan;
        _control = control;
        _progress = progress;
        _journal = new DreamStoreRunLog(store, plan.RunId, Clock);

        _ctx = new DreamRunContext
        {
            RunId = plan.RunId,
            Trigger = plan.Trigger,
            Mode = plan.Mode,
            Settings = plan.Settings,
            Thresholds = Thresholds,
            UseModel = plan.UseModel,
            UseRules = plan.UseRules,
            ModelId = plan.ModelId,
            ModelLabel = plan.ModelLabel,
            ModelRowId = plan.ModelRowId,
            MaxAdvice = plan.MaxAdvice,
            MaxMinutes = plan.MaxMinutes,
            Phases = plan.Phases,
            Sample = plan.Sample,
            Budget = progress.Budget,
            Log = new RevalidatingLog(this),
            Clock = Clock,
            StartedAtUtc = plan.StartedAtUtc,
        };

        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatLoopAsync(heartbeatStop.Token);
        try
        {
            await store.MarkRunStartedAsync(plan.RunId, Now);
            await ExecuteAsync();
            return new DreamRunResult(_control.Reason ?? DreamRunOutcome.Completed);
        }
        catch (DreamRunEndException end)
        {
            return new DreamRunResult(end.Outcome);
        }
        catch (OperationCanceledException)
        {
            return new DreamRunResult(_control.Reason ?? DreamRunOutcome.Interrupted);
        }
        catch (DreamBudgetExceededException)
        {
            return new DreamRunResult(DreamRunOutcome.BudgetLimit);
        }
        catch (DreamProviderException ex)
        {
            // Told as a kind of failure: the exception may wrap what the provider answered, and that never reaches the page.
            logger.LogWarning(ex, "A Dream run ended: the model provider failed");
            return new DreamRunResult(DreamRunOutcome.ProviderError, "provider_error", "The model provider kept failing after the retries.");
        }
        catch (Exception ex) when (ex is VaultSealedException or SessionLockedException)
        {
            return new DreamRunResult(DreamRunOutcome.Locked, DreamErrorCodes.Locked);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A Dream run failed");
            return new DreamRunResult(DreamRunOutcome.Failed, "internal_error", ex.GetType().Name);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }
    }

    private async Task ExecuteAsync()
    {
        Interrupt();
        if (_plan.Notice is { } notice) await WriteAsync(DreamEventKind.Info, notice);

        if (_ctx.Includes(DreamPhase.Embeddings)) await EmbeddingsPhaseAsync();

        Interrupt();
        _corpus = await corpusBuilder.BuildAsync(_plan.Settings, Token);
        _progress.SetArticlesInScope(_corpus.Articles.Count);
        _rules = new DreamRuleSet(await store.LoadActiveRulesAsync(Now));
        if (!_ctx.IsTrial)
        {
            // An open advice that no longer stands must not hide a fresh one as its "repeat": settle those first.
            await services.GetRequiredService<DreamAdviceValidator>().SweepAsync(Token);
            await LoadOpenAdviceAsync();
        }

        var withoutVector = _corpus.Articles.Count(a => !a.HasEmbedding);
        if (_corpus.Articles.Count > 0 && (double)withoutVector / _corpus.Articles.Count > Thresholds.MaxShareWithoutVector)
        {
            _corpus.DisableSimilarity();
            await WriteAsync(DreamEventKind.Skip, $"{withoutVector} of {_corpus.Articles.Count} notes have no vector: the steps based on similarity are skipped.");
        }

        // With the rules off the detectors do not run at all: no rule advice is made, and nothing the model tasks use comes from
        // them (folder_review orders folders by its own count of stray notes, pairs come from the vectors).
        if (!_plan.UseRules) await WriteAsync(DreamEventKind.Info, "The built-in rules are off for this run: only the AI model looks at the notes.");
        else if (_ctx.Includes(DreamPhase.Detectors)) await DetectorsPhaseAsync();
        if (_plan.UseModel) await ModelPhasesAsync();
    }

    private async Task EnterPhaseAsync(DreamPhase phase, string text)
    {
        Interrupt();
        _progress.Phase = phase;
        await store.UpdateRunProgressAsync(_plan.RunId, phase, _progress.Snapshot(), Now);
        await WriteAsync(DreamEventKind.Phase, text);
    }

    // ── embeddings ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task EmbeddingsPhaseAsync()
    {
        var coverage = await corpusBuilder.GetEmbeddingCoverageAsync(_plan.Settings);
        if (coverage.Pending == 0) return;

        await EnterPhaseAsync(DreamPhase.Embeddings, $"Preparing vectors: {coverage.Pending} notes are waiting.");

        var processor = services.GetService<PendingEmbeddingProcessor>();
        if (processor is null)
        {
            await WriteAsync(DreamEventKind.Skip, "This node does not generate vectors.");
            return;
        }

        try
        {
            while (true)
            {
                Interrupt();
                var processed = await processor.DrainAllPendingAsync(Token);
                Interrupt();
                var remaining = (await corpusBuilder.GetEmbeddingCoverageAsync(_plan.Settings)).Pending;
                await WriteAsync(DreamEventKind.Info, $"Vectors: {processed} notes done, {remaining} waiting.");
                if (remaining == 0 || processed == 0) break;
            }
        }
        catch (ModelUnavailableException)
        {
            await WriteAsync(DreamEventKind.Skip, "The embedding model is not available on this node.");
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex))
        {
            // Vectors are a help, not a condition: without them the steps on similarity are skipped below.
            logger.LogWarning(ex, "A Dream run could not prepare vectors");
            _progress.AddError();
            await WriteAsync(DreamEventKind.Error, $"Preparing vectors failed ({ex.GetType().Name}).");
        }
    }

    // ── detectors ───────────────────────────────────────────────────────────────────────────────────────────────

    private async Task DetectorsPhaseAsync()
    {
        await EnterPhaseAsync(DreamPhase.Detectors, "Looking through the notes with the rules.");

        var drafts = new List<DreamAdviceDraft>();
        foreach (var detector in detectors)
        {
            Interrupt();
            try
            {
                await foreach (var draft in detector.DetectAsync(_corpus, _ctx, Token).WithCancellation(Token))
                {
                    drafts.Add(draft);
                    Interrupt();
                }
            }
            catch (Exception ex) when (IsOrdinaryFailure(ex))
            {
                logger.LogWarning(ex, "A Dream detector failed");
                _progress.AddError();
                await WriteAsync(DreamEventKind.Error, $"The check {detector.Name} failed ({ex.GetType().Name}).");
            }
        }

        // With a limit on the advice of a run the best ones must win, so nothing is written until every detector has spoken.
        var cap = _plan.MaxAdvice <= 0
            ? int.MaxValue
            : _plan.UseModel
                ? Math.Max(1, (int)(_plan.MaxAdvice * Thresholds.DetectorShareOfAdviceWithModel))
                : _plan.MaxAdvice;
        var created = 0;
        foreach (var draft in drafts.OrderByDescending(d => d.Score))
        {
            Interrupt();
            if (created >= cap)
            {
                await WriteAsync(DreamEventKind.Info, "The rules have used their share of this run's advice.");
                break;
            }
            if (await ProcessDraftAsync(draft)) created++;
            if (_options.EmitPause > TimeSpan.Zero) await Task.Delay(_options.EmitPause, Token);
        }
    }

    // ── the model ───────────────────────────────────────────────────────────────────────────────────────────────

    private async Task ModelPhasesAsync()
    {
        var pairs = TaskNamed(DreamNames.PairVerdictTask);
        var folders = TaskNamed(DreamNames.FolderReviewTask);
        var tree = TaskNamed(DreamNames.TreeReviewTask);
        var maxTier = _plan.Trigger == DreamRunTrigger.Schedule ? Thresholds.ScheduledMaxTier : Thresholds.PairTierCount;

        if (_ctx.Sample is { } sample)
        {
            await PairsPhaseAsync(pairs, 1, maxTier, sample.Pairs);
            await FoldersPhaseAsync(folders, sample.Folders);
            await TreePhaseAsync(tree);
            return;
        }

        // The night goes through its tiers once. A manual run takes the first tier, the folders and the tree, then comes
        // back for the remaining tiers until the candidates or a limit run out.
        var firstPass = _plan.Trigger == DreamRunTrigger.Schedule ? maxTier : 1;
        await PairsPhaseAsync(pairs, 1, firstPass, null);
        await FoldersPhaseAsync(folders, null);
        await TreePhaseAsync(tree);
        if (firstPass < maxTier) await PairsPhaseAsync(pairs, firstPass + 1, maxTier, null);
    }

    private IDreamModelTask? TaskNamed(string name) => modelTasks.FirstOrDefault(t => t.Name == name);

    private async Task PairsPhaseAsync(IDreamModelTask? task, int fromTier, int toTier, int? sample)
    {
        if (task is null || !_ctx.Includes(DreamPhase.Pairs)) return;
        if (!_corpus.SimilarityEnabled)
        {
            await WriteAsync(DreamEventKind.Skip, "Pairs of notes are skipped: too many notes have no vector.");
            return;
        }
        await EnterPhaseAsync(DreamPhase.Pairs, "Comparing pairs of similar notes.");
        await ExamineAsync(task, Enumerable.Range(fromTier, toTier - fromTier + 1), sample);
    }

    private async Task FoldersPhaseAsync(IDreamModelTask? task, int? sample)
    {
        if (task is null || !_ctx.Includes(DreamPhase.Folders)) return;
        await EnterPhaseAsync(DreamPhase.Folders, "Reviewing folders.");
        await ExamineAsync(task, [1], sample);
    }

    private async Task TreePhaseAsync(IDreamModelTask? task)
    {
        if (task is null || !_ctx.Includes(DreamPhase.Tree)) return;
        if (!_ctx.IsTrial && await store.GetLastExaminedAtAsync(task.Name) is { } last && Now - last < TimeSpan.FromDays(Thresholds.TreeReviewMinDays))
        {
            await WriteAsync(DreamEventKind.Skip, $"The folder tree was reviewed less than {Thresholds.TreeReviewMinDays} days ago.");
            return;
        }
        await EnterPhaseAsync(DreamPhase.Tree, "Reviewing the folder tree.");
        await ExamineAsync(task, [1], null);
    }

    private async Task ExamineAsync(IDreamModelTask task, IEnumerable<int> tiers, int? sample)
    {
        if (_ctx.Sample is { } plan)
        {
            var all = new List<DreamModelCandidate>();
            foreach (var tier in tiers)
                await foreach (var candidate in task.CandidatesAsync(_corpus, _ctx, tier, Token).WithCancellation(Token))
                    all.Add(candidate);

            var chosen = all.DistinctBy(c => c.CandidateKey, StringComparer.Ordinal)
                .OrderBy(c => SampleOrder(plan.Seed, c.CandidateKey), StringComparer.Ordinal)
                .Take(sample ?? int.MaxValue);
            foreach (var candidate in chosen) await ExamineOneAsync(task, candidate);
            return;
        }

        foreach (var tier in tiers)
            await foreach (var candidate in task.CandidatesAsync(_corpus, _ctx, tier, Token).WithCancellation(Token))
                await ExamineOneAsync(task, candidate);
    }

    private static string SampleOrder(int seed, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{key}")));

    private async Task ExamineOneAsync(IDreamModelTask task, DreamModelCandidate candidate)
    {
        Interrupt();

        // A pair can come up in two tiers, and a trial run has no registry to catch that.
        if (!_seenCandidates.Add(candidate.CandidateKey)) return;
        // Nothing is spent on a candidate whose notes went out of reach since the run began.
        if (candidate.ArticleIds.Any(_corpus.IsWithdrawn)) return;
        if (!_ctx.IsTrial && await store.WasExaminedAsync(candidate.CandidateKey, candidate.ContentMarker, task.PromptVersion)) return;
        if (_ctx.Budget.Exhausted) throw new DreamRunEndException(DreamRunOutcome.BudgetLimit);

        DreamTaskResult result;
        try
        {
            result = await task.ExamineAsync(candidate, _corpus, modelClient, _ctx, Token);
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex) && ex is not (DreamBudgetExceededException or DreamProviderException))
        {
            logger.LogWarning(ex, "A Dream model task failed on one candidate");
            result = DreamTaskResult.Failed($"The examination failed ({ex.GetType().Name}).");
        }

        _progress.AddCandidateExamined();
        _progress.AddArticlesSent(result.ArticlesSent);
        if (result.Outcome == DreamExaminedVerdict.Error) _progress.AddError();

        if (!_ctx.IsTrial)
        {
            await store.RecordExaminedAsync(candidate.CandidateKey, candidate.Task, candidate.ContentMarker, _plan.ModelId, task.PromptVersion, result.Outcome, _plan.RunId, Now);
            if (result.Outcome != DreamExaminedVerdict.Error && candidate.FamilyKey is { } family) await SupersedeAsync(family);
        }

        var first = result.Drafts.FirstOrDefault();
        await WriteAsync(
            result.Outcome == DreamExaminedVerdict.Error ? DreamEventKind.Error : DreamEventKind.Candidate,
            result.Error ?? $"Examined: {candidate.Task}, {result.Verdict ?? DreamWire.ToWire(result.Outcome)}.",
            candidate.ArticleIds,
            JsonSerializer.SerializeToNode(new DreamCandidateEventData(candidate.CandidateKey, candidate.Task, candidate.Tier, candidate.ArticleIds,
                candidate.FolderIds, result.Outcome, result.Verdict, first?.Kind, result.Score, result.Summary is null ? null : DreamDraftChecks.Cut(result.Summary, Thresholds.SummaryMaxLength)),
                DreamJson.Options) as JsonObject);

        foreach (var draft in result.Drafts)
        {
            Interrupt();
            await ProcessDraftAsync(draft);
        }
        await store.UpdateRunProgressAsync(_plan.RunId, _progress.Phase, _progress.Snapshot(), Now);

        if (_ctx.Budget.Exhausted) throw new DreamRunEndException(DreamRunOutcome.BudgetLimit);
    }

    // ── one proposal ────────────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadOpenAdviceAsync()
    {
        foreach (var entry in await store.LoadOpenAdviceIndexAsync())
        {
            _knownFingerprints.Add(entry.Fingerprint);
            if (entry is { Kind: DreamAdviceKind.NearDuplicate, Source: DreamAdviceSource.Rule, FamilyKey: { } family })
            {
                if (!_openNearDuplicates.TryGetValue(family, out var list)) _openNearDuplicates[family] = list = [];
                list.Add(entry);
            }
        }
    }

    private async Task SupersedeAsync(string family)
    {
        if (!_openNearDuplicates.Remove(family, out var entries)) return;
        foreach (var entry in entries)
            await store.TrySetAdviceStatusAsync(entry.Id, [DreamAdviceStatus.New, DreamAdviceStatus.InChat], DreamAdviceStatus.Expired,
                DreamStatusNotes.Superseded, null, Now);
    }

    /// <summary>Schema and ids, dismiss rules, repeats, min_score, the write, the counters, the limit. True when an advice was written.</summary>
    private async Task<bool> ProcessDraftAsync(DreamAdviceDraft raw)
    {
        // A proposal about a note that went out of reach during the run is dropped, not refused as malformed.
        if (NamedArticleIds(raw).Any(_corpus.IsWithdrawn))
        {
            await WriteAsync(DreamEventKind.Skip, WithdrawnNote);
            return false;
        }
        if (DreamDraftChecks.Check(raw, _corpus, Thresholds, out var draft) is { } problem)
        {
            _progress.AddError();
            await WriteAsync(DreamEventKind.Error, $"A proposal was refused: {problem}.");
            return false;
        }

        // The corpus is as the run found it; the notes a proposal names are asked about again as they are now.
        if ((await _corpus.RevalidateAsync(NamedArticleIds(draft), Token)).Count > 0)
        {
            await WriteAsync(DreamEventKind.Skip, WithdrawnNote);
            return false;
        }

        var fingerprint = DreamFingerprint.OfDraft(draft);
        if (_rules.Suppresses(draft, fingerprint))
        {
            _progress.AddAdviceSuppressed();
            await WriteAsync(DreamEventKind.Skip, $"Not offered, a dismiss rule covers it: {draft.Title}", ArticleIdsOf(draft));
            return false;
        }
        if (_knownFingerprints.Contains(fingerprint))
        {
            _progress.AddAdviceSuppressed();
            return false;
        }
        if (draft.Score < _plan.Settings.MinScore)
        {
            await WriteAsync(DreamEventKind.Skip, $"Below the minimum score ({draft.Score}): {draft.Title}", ArticleIdsOf(draft));
            return false;
        }

        var seq = await store.InsertAdviceAsync(new DreamAdviceRecord
        {
            Id = Guid.NewGuid(),
            RunId = _plan.RunId,
            Kind = draft.Kind,
            Source = draft.Source,
            ApplyMode = draft.ApplyMode,
            Score = draft.Score,
            Trial = _ctx.IsTrial,
            CreatedAt = Now,
            Payload = DreamAdvicePayload.FromDraft(draft),
        });
        _knownFingerprints.Add(fingerprint);
        _progress.AddAdviceCreated();
        await WriteAsync(DreamEventKind.Advice, $"Advice {seq}: {draft.Title}", ArticleIdsOf(draft));
        await store.UpdateRunProgressAsync(_plan.RunId, _progress.Phase, _progress.Snapshot(), Now);

        if (_plan.MaxAdvice > 0 && _progress.AdviceCreated >= _plan.MaxAdvice)
            throw new DreamRunEndException(DreamRunOutcome.AdviceLimit);
        return true;
    }

    /// <summary>Every article the stored advice would name: objects, action, preconditions, evidence.</summary>
    private static IReadOnlyList<Guid> NamedArticleIds(DreamAdviceDraft draft) =>
        new DreamAdvicePayload { Objects = draft.Objects, Action = draft.Action, Preconditions = draft.Preconditions, Evidence = draft.Evidence }.ArticleIds();

    private static List<Guid> ArticleIdsOf(DreamAdviceDraft draft) =>
        draft.Action.ArticleIdsNamed().Concat(draft.Objects.Where(o => o.Type == DreamObjectType.Article && o.Id.HasValue).Select(o => o.Id!.Value)).Distinct().ToList();

    // ── limits ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Throws when the run must end now: Stop, a lock, the time limit, a heavy operation.</summary>
    private void Interrupt()
    {
        CheckConditions();
        Token.ThrowIfCancellationRequested();
    }

    private void CheckConditions()
    {
        if (!session.IsUnlocked) _control.Request(DreamRunOutcome.Locked);
        else if (_plan.DeadlineUtc is { } deadline && Now >= deadline) _control.Request(DreamRunOutcome.TimeLimit);
        else if (DreamHeavyOperations.IsRunning(operations, maintenance)) _control.Request(DreamRunOutcome.HeavyOperation);
    }

    private static bool IsOrdinaryFailure(Exception ex) =>
        ex is not (OperationCanceledException or DreamRunEndException or SessionLockedException or VaultSealedException);

    /// <summary>The journal line written in place of one that would name a note that went out of reach.</summary>
    internal const string WithdrawnNote =
        "A note this step was about changed during the run (protected, moved out of reach or deleted); it is left out.";

    /// <summary>
    /// Every journal line of the run, the model tasks' too, goes through here: a line naming articles is written only when
    /// each of them is still within reach now; otherwise neither its text, its ids nor its data are kept, only
    /// <see cref="WithdrawnNote"/>.
    /// </summary>
    private async Task WriteAsync(DreamEventKind kind, string text, IReadOnlyList<Guid>? articleIds = null, JsonObject? data = null,
        CancellationToken ct = default)
    {
        if (articleIds is { Count: > 0 } && (_corpus is null || (await _corpus.RevalidateAsync(articleIds, ct)).Count > 0))
        {
            await _journal.WriteAsync(DreamEventKind.Skip, WithdrawnNote, null, null, ct);
            return;
        }
        await _journal.WriteAsync(kind, text, articleIds, data, ct);
    }

    /// <summary>The journal the detectors and model tasks see (DreamRunContext.Log).</summary>
    private sealed class RevalidatingLog(DreamRunner runner) : IDreamRunLog
    {
        public Task WriteAsync(DreamEventKind kind, string text, IReadOnlyList<Guid>? articleIds = null, JsonObject? data = null,
            CancellationToken ct = default) =>
            runner.WriteAsync(kind, text, articleIds, data, ct);
    }

    /// <summary>
    /// Proves the run is alive while a slow detector or a model call holds the main path, and notices a lock, a heavy
    /// operation or the time limit during such a call instead of after it.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(Thresholds.HeartbeatInterval, Clock);
            while (await timer.WaitForNextTickAsync(ct))
            {
                CheckConditions();
                await store.UpdateRunProgressAsync(_plan.RunId, _progress.Phase, _progress.Snapshot(), Now);
            }
        }
        catch (OperationCanceledException)
        {
            // The run has ended.
        }
        catch (Exception ex) when (ex is VaultSealedException or SessionLockedException)
        {
            _control.Request(DreamRunOutcome.Locked);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A Dream run's heartbeat failed");
        }
    }
}
