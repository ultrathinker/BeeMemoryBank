using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Core.Exceptions;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// IDreamModelClient over the chat's providers (plan 3.7, docs/ai-providers.md): the analysis model at its own provider, then the chat's
/// fallback chain, each provider with its own keys. The key rotation is the chat's, not a copy of it: the routes and the keys come from
/// <see cref="ModelRouter"/>, so a 401 disables the key, a 402 or 429 pauses it for five minutes, a 5xx or a transport error moves on to
/// the next key, and the key table the admin page shows is updated the same way. When every key of a model's provider failed, or the
/// provider cannot be called (not approved, no usable key, the egress refused it), the next model of the chain is tried. Nothing is ever
/// sent to a provider that is not approved.
///
/// <para><b>The budget counts every outgoing request.</b> A "call" of the run's budget is one HTTP attempt to a provider, not one answer:
/// each key tried in a failover, each model of the fallback chain and each retry round takes a call slot of its own. Before each attempt
/// the budget must admit the attempt's upper estimate (the input characters / 3 plus the output limit), otherwise nothing is sent and
/// <see cref="DreamBudgetExceededException"/> ends the step; a failing provider can therefore not make the run spend past its limits by
/// retrying, and the waits stop as soon as either limit is spent. After the attempt the budget is charged what it really cost: the usage
/// the provider reports on an answer (the estimate when it reports none); nothing in tokens for an attempt the provider refused with a
/// status (401, 402, 429, 5xx) or that never connected; and, because the provider may have run it and charged for it, the whole upper
/// estimate for an attempt whose outcome is unknown (the HTTP timeout, an answer that is not a completion, a Stop in flight).</para>
///
/// <para><b>The token limit is an estimate, not a hard cap.</b> The admission check counts characters / 3, which is not an upper bound for
/// every text (CJK-heavy notes can cost nearly a token per character) and no tokenizer of the chosen model is available here; so the last
/// request of a run can take it a little past <c>max_tokens</c>. The limit is meant to stop a run from spending without bound, not to be an
/// exact bill: where it is shown to a person it is called an estimate.</para>
///
/// <para>When every model of the chain has failed it waits 30, 60 and 120 seconds (checking Stop all along) and then gives up with
/// <see cref="DreamProviderException"/>; when no wait can cure any of the failures it gives up at once: no key enabled, every key disabled
/// by a 401, a key that cannot be decrypted on this node, a provider that is not approved, and a request the provider refuses for good (an
/// unknown model). The calls are strictly sequential, the reply is cut short by the output limit, asked at temperature 0 and, where the
/// provider takes it (<see cref="ProviderWireOptions.Reasoning"/>), with a low reasoning effort. A reply the cap stopped (<c>finish_reason</c>
/// length, or reasoning and no content) is flagged <see cref="DreamModelReply.CutOff"/> so the task can say so in the journal.</para>
///
/// <para>Safety: nothing is unlocked here (a locked vault throws what the chat's key reading throws, and the run ends <c>locked</c>); every
/// request goes through the provider egress guard; no key, request text or reply text goes into a log line or an exception message, and
/// the body of an upstream error is not logged either (a refusal can quote what it was sent).</para>
/// </summary>
public sealed class ProviderDreamModelClient : IDreamModelClient
{
    /// <summary>An answer to a pair, a folder or the tree is a short JSON object; the cap keeps a model that rambles from
    /// running up the bill. The tasks pass their own limit in the request (3000, 5000, 6000); this is the ceiling. The
    /// limits are generous because a model that reasons before it answers spends its reasoning against the same cap.</summary>
    public const int MaxOutputTokensCeiling = 8000;

    /// <summary>The reasoning effort of every Dream request: reasoning models think as little as they may before they answer (a pair or a
    /// folder does not need more), sent the provider's way; a provider whose options send no reasoning field ignores it. Dream only; the
    /// chat does not send it.</summary>
    public const string ReasoningEffort = "low";

    private readonly ModelRouter _router;
    private readonly ILogger<ProviderDreamModelClient> _logger;
    private readonly DreamThresholds _thresholds;
    private readonly TimeProvider _time;

    public ProviderDreamModelClient(ModelRouter router, ILogger<ProviderDreamModelClient> logger, DreamThresholds? thresholds = null,
        TimeProvider? time = null)
    {
        _router = router;
        _logger = logger;
        _thresholds = thresholds ?? DreamThresholds.Default;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>What one request is charged against: its upper estimate in tokens, split as the estimate is made.</summary>
    private readonly record struct Attempt(DreamModelRequest Request, IReadOnlyList<ChatRoleMessage> Messages, int MaxOutput, long InputEstimate)
    {
        public long Estimate => InputEstimate + MaxOutput;
    }

    public async Task<DreamModelReply> CompleteAsync(DreamModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ModelId))
            throw new DreamProviderException("No analysis model is set for this run.");
        var maxOutput = Math.Clamp(request.MaxOutputTokens, 1, MaxOutputTokensCeiling);

        var attempt = new Attempt(
            request,
            [new ChatRoleMessage("system", request.System), new ChatRoleMessage("user", request.User)],
            maxOutput,
            DreamRunBudget.EstimateTokens(request.System, request.User, 0));

        // Nothing is decrypted or sent for a step the budget does not admit.
        if (!request.Budget.TryReserve(attempt.Estimate))
            throw new DreamBudgetExceededException("The model budget of this run does not admit another call.");

        var chain = await ChainAsync(request);
        var waits = _thresholds.ProviderBackoff;
        // Models whose provider refused the request itself (400/404/413/422): permanent for that model, so a later round does not spend a
        // call on it again.
        var refused = new Dictionary<Guid, Failure>();
        for (var round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();

            var (reply, failure) = await TryRoundAsync(attempt, chain, refused, ct);
            if (reply != null) return reply;

            if (failure!.Permanent || round >= waits.Count)
                throw new DreamProviderException(failure.Message);

            // Another round needs a call slot and an allowance of its own: when either limit is spent, stop retrying
            // instead of waiting for a request that cannot be sent.
            if (!request.Budget.TryReserve(attempt.Estimate))
                throw new DreamBudgetExceededException(
                    $"The model budget of this run is spent and the provider keeps failing ({failure.Message})");

            var wait = waits[round];
            _logger.LogWarning("Dream model call failed ({Reason}); waiting {Seconds} s before trying the keys again.",
                failure.Message, (int)wait.TotalSeconds);
            await Task.Delay(wait, _time, ct);
        }
    }

    /// <summary>The analysis model, then the chat's fallback chain. A request without a catalogue row (as before providers) names a model
    /// id at the built-in OpenRouter provider.</summary>
    private async Task<IReadOnlyList<ChatModelRow>> ChainAsync(DreamModelRequest request)
    {
        var first = request.ModelRowId is { } rowId ? await _router.Settings.GetModelByIdAsync(rowId) : null;
        first ??= new ChatModelRow
        {
            Id = request.ModelRowId ?? Guid.Empty, ProviderId = BuiltInOpenRouter.Id, ModelId = request.ModelId, Label = request.ModelId, IsText = true,
        };
        return await _router.TextChainAsync(first);
    }

    private sealed record Failure(string Message, bool Permanent);

    /// <summary>One pass over the chain, each model's keys the chat's way. A reply, or what to do about the failures.</summary>
    private async Task<(DreamModelReply? Reply, Failure? Failure)> TryRoundAsync(Attempt attempt, IReadOnlyList<ChatModelRow> chain,
        Dictionary<Guid, Failure> refused, CancellationToken ct)
    {
        var failures = new List<Failure>();
        foreach (var model in chain)
        {
            ct.ThrowIfCancellationRequested();
            if (refused.TryGetValue(model.Id, out var known))
            {
                failures.Add(known);
                continue;
            }
            // Needs an unlocked vault: when it is locked this throws what the chat throws and the runner maps it to "locked".
            var (route, problem) = await _router.PrepareAsync(model);
            if (route is null)
            {
                failures.Add(new Failure(problem!.Message, problem.Permanent));
                continue;
            }

            int? lastStatus = null;
            try
            {
                var reply = await _router.RunOnRouteAsync(route,
                    (call, token) => SendAsync(route, call, attempt, status => lastStatus = status, token), ct);
                return (reply, null);
            }
            catch (AllKeysExhaustedException ex)
            {
                if (!route.Keys.Any(k => k.IsKeyless) && !(await _router.Settings.ListByProviderAsync(route.Target.ProviderId)).Any(k => k.Enabled))
                    failures.Add(new Failure($"Every key of provider '{route.ProviderName}' was refused and disabled.", Permanent: true));
                else if (lastStatus is 400 or 403 or 404 or 413 or 422)
                {
                    var failure = new Failure($"Provider '{route.ProviderName}' refused the request (HTTP {lastStatus}); check the analysis model.", Permanent: true);
                    failures.Add(failure);
                    if (ModelRouter.IsRequestError(lastStatus.Value)) refused[model.Id] = failure;
                }
                else
                    failures.Add(new Failure($"Every key of provider '{route.ProviderName}' failed: {ex.Message}", Permanent: false));
            }
            catch (ProviderUnavailableException ex)
            {
                failures.Add(new Failure(ex.Message, Permanent: true));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The HTTP client's own timeout, not Stop: the provider was too slow; the next model, then the same waits.
                failures.Add(new Failure($"Provider '{route.ProviderName}' did not answer in time.", Permanent: false));
            }
            catch (Exception ex) when (ex is JsonException || (ex is InvalidOperationException && ex is not SessionLockedException))
            {
                // A 200 with a body that is not a completion: not the key's fault; the next model, then the same waits.
                failures.Add(new Failure($"Provider '{route.ProviderName}' answered with something that is not a completion.", Permanent: false));
            }
        }
        return (null, new Failure(ModelRouter.Summarize(failures.Select(f => f.Message).ToList()),
            Permanent: failures.Count > 0 && failures.All(f => f.Permanent)));
    }

    /// <summary>
    /// One outgoing request, with the budget kept honestly: admitted before it is sent, charged after (see the class remarks). Called by
    /// the chat's failover once per key of each model of the chain, so a key tried is a call used.
    /// </summary>
    private async Task<DreamModelReply> SendAsync(ModelRoute route, ModelCall call, Attempt attempt, Action<int> noteStatus, CancellationToken token)
    {
        var budget = attempt.Request.Budget;
        if (!budget.TryReserve(attempt.Estimate))
            throw new DreamBudgetExceededException("The model budget of this run does not admit another call.");

        ChatCompleteResponse answer;
        try
        {
            answer = await route.Client.CompleteAsync(call, attempt.Messages,
                new CompletionOptions(attempt.MaxOutput, 0.0, ReasoningEffort), token);
        }
        catch (ProviderHttpException ex)
        {
            // The provider answered with a status and did no work for it: a call used, no tokens.
            noteStatus(ex.StatusCode);
            budget.Record(0, 0);
            throw;
        }
        catch (HttpRequestException)
        {
            // Never connected, or the connection broke: a call used; the provider did not charge for a request it did not get.
            budget.Record(0, 0);
            throw;
        }
        catch (ProviderUnavailableException)
        {
            // Refused by the egress guard before anything was sent: no call was made.
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException
                                   || (ex is InvalidOperationException && ex is not SessionLockedException))
        {
            // The timeout, a Stop in flight, or an answer that is not a completion: the provider may have run the
            // request and charged for it, and there is no usage to go by, so the whole upper estimate is charged.
            budget.Record(attempt.InputEstimate, attempt.MaxOutput);
            throw;
        }

        // What it really cost; the estimate stands in for a usage the provider did not send.
        var prompt = answer.PromptTokens > 0 ? answer.PromptTokens : attempt.InputEstimate;
        var completion = answer.CompletionTokens > 0 ? answer.CompletionTokens : (answer.Content.Length + 2) / 3;
        budget.Record(prompt, completion);
        return new DreamModelReply(answer.Content, prompt, completion, answer.Model, CutOff: WasCutOff(answer), Provider: route.ProviderName);
    }

    /// <summary>The output limit stopped the answer: the provider said so (<c>finish_reason</c> length), or the model
    /// reasoned and sent no content, which is the same thing seen from the other side (the cap went on the reasoning).</summary>
    internal static bool WasCutOff(ChatCompleteResponse answer) =>
        answer.FinishReason is "length" or "max_tokens"
        || (string.IsNullOrWhiteSpace(answer.Content) && answer.HasReasoning);
}
