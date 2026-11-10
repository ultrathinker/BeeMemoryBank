using System.Text.Json;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// IDreamModelClient over <see cref="OpenRouterClient.CompleteAsync(string, string, IReadOnlyList{ChatRoleMessage}, int?, double?, string?, CancellationToken)"/>
/// and the chat's own keys (plan 3.7). The key rotation is the chat's, not a copy of it: the keys come from
/// <c>ChatEndpoints.DecryptAvailableKeysAsync</c> and the call goes through <c>ChatEndpoints.RunWithFailoverAsync</c>,
/// so a 401 disables the key, a 402 or 429 pauses it for five minutes, a 5xx or a transport error moves on to the next
/// key, and the key table the admin page shows is updated the same way.
///
/// <para><b>The budget counts every outgoing request.</b> A "call" of the run's budget is one HTTP attempt to the
/// provider, not one answer: each key tried in a failover and each retry round takes a call slot of its own. Before
/// each attempt the budget must admit the attempt's upper estimate (the input characters / 3 plus the output limit),
/// otherwise nothing is sent and <see cref="DreamBudgetExceededException"/> ends the step; a failing provider can
/// therefore not make the run spend past its limits by retrying, and the waits stop as soon as either limit is spent.
/// After the attempt the budget is charged what it really cost: the usage the provider reports on an answer (the
/// estimate when it reports none); nothing in tokens for an attempt the provider refused with a status (401, 402, 429,
/// 5xx) or that never connected; and, because the provider may have run it and charged for it, the whole upper estimate
/// for an attempt whose outcome is unknown (the HTTP timeout, an answer that is not a completion, a Stop in flight).</para>
///
/// <para><b>The token limit is an estimate, not a hard cap.</b> The admission check counts characters / 3, which is not
/// an upper bound for every text (CJK-heavy notes can cost nearly a token per character) and no tokenizer of the chosen
/// model is available here; so the last request of a run can take it a little past <c>max_tokens</c>. The limit is
/// meant to stop a run from spending without bound, not to be an exact bill: where it is shown to a person it is
/// called an estimate.</para>
///
/// <para>When every key has failed it waits 30, 60 and 120 seconds (checking Stop all along) and then gives up with
/// <see cref="DreamProviderException"/>; an error that no wait can cure gives up at once: no key enabled, every key
/// disabled by a 401, a key that cannot be decrypted on this node (a setup problem for the administrator, not a pause)
/// and a request the provider refuses for good (an unknown model). The calls are strictly sequential, the reply is cut
/// short by <c>max_tokens</c>, asked at temperature 0 and with <c>reasoning.effort</c> low. A reply the cap stopped
/// (<c>finish_reason</c> length, or reasoning and no content) is flagged <see cref="DreamModelReply.CutOff"/> so the
/// task can say so in the journal.</para>
///
/// <para>Safety: nothing is unlocked here (a locked vault throws what the chat's key reading throws, and the run ends
/// <c>locked</c>); the egress host is the chat client's pinned one; no key, request text or reply text goes into a log
/// line or an exception message.</para>
/// </summary>
public sealed class OpenRouterDreamModelClient : IDreamModelClient
{
    /// <summary>An answer to a pair, a folder or the tree is a short JSON object; the cap keeps a model that rambles from
    /// running up the bill. The tasks pass their own limit in the request (3000, 5000, 6000); this is the ceiling. The
    /// limits are generous because a model that reasons before it answers spends its reasoning against the same cap.</summary>
    public const int MaxOutputTokensCeiling = 8000;

    /// <summary>OpenRouter's unified <c>reasoning.effort</c> of every Dream request: reasoning models think as little as
    /// they may before they answer (a pair or a folder does not need more), the others ignore it. Dream only; the chat
    /// does not send it.</summary>
    public const string ReasoningEffort = "low";

    private readonly ChatSettingsRepository _repo;
    private readonly SessionService _session;
    private readonly OpenRouterClient _openRouter;
    private readonly ILogger<OpenRouterDreamModelClient> _logger;
    private readonly DreamThresholds _thresholds;
    private readonly TimeProvider _time;

    public OpenRouterDreamModelClient(ChatSettingsRepository repo, SessionService session, OpenRouterClient openRouter,
        ILogger<OpenRouterDreamModelClient> logger, DreamThresholds? thresholds = null, TimeProvider? time = null)
    {
        _repo = repo;
        _session = session;
        _openRouter = openRouter;
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

        var waits = _thresholds.ProviderBackoff;
        for (var round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();

            var (reply, failure) = await TryRoundAsync(attempt, ct);
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

    private sealed record Failure(string Message, bool Permanent);

    /// <summary>One pass over the keys the chat's way. A reply, or what to do about the failure.</summary>
    private async Task<(DreamModelReply? Reply, Failure? Failure)> TryRoundAsync(Attempt attempt, CancellationToken ct)
    {
        // Needs an unlocked vault: when it is locked this throws what the chat throws and the runner maps it to "locked".
        var keys = await ChatEndpoints.DecryptAvailableKeysAsync(_repo, _session);
        if (keys.Count == 0) return (null, await NoUsableKeyAsync());

        int? lastStatus = null;
        try
        {
            var reply = await ChatEndpoints.RunWithFailoverAsync(_repo, keys,
                (key, token) => SendAsync(key, attempt, status => lastStatus = status, token), ct);
            return (reply, null);
        }
        catch (ChatEndpoints.AllKeysExhaustedException ex)
        {
            if (!await AnyKeyEnabledAsync())
                return (null, new Failure("Every OpenRouter key was refused and disabled.", Permanent: true));
            if (lastStatus is 400 or 403 or 404 or 413 or 422)
                return (null, new Failure($"The provider refused the request (HTTP {lastStatus}); check the analysis model.", Permanent: true));
            return (null, new Failure($"Every OpenRouter key failed: {ex.Message}", Permanent: false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The HTTP client's own timeout, not Stop: the provider was too slow, worth the same waits.
            return (null, new Failure("The provider did not answer in time.", Permanent: false));
        }
        catch (Exception ex) when (ex is JsonException || (ex is InvalidOperationException && ex is not SessionLockedException))
        {
            // A 200 with a body that is not a completion: not the key's fault, worth the same waits.
            return (null, new Failure("The provider answered with something that is not a completion.", Permanent: false));
        }
    }

    /// <summary>
    /// One outgoing request, with the budget kept honestly: admitted before it is sent, charged after (see the class
    /// remarks). Called by the chat's failover once per key, so a key tried is a call used.
    /// </summary>
    private async Task<DreamModelReply> SendAsync(string key, Attempt attempt, Action<int> noteStatus, CancellationToken token)
    {
        var budget = attempt.Request.Budget;
        if (!budget.TryReserve(attempt.Estimate))
            throw new DreamBudgetExceededException("The model budget of this run does not admit another call.");

        ChatCompleteResponse answer;
        try
        {
            answer = await _openRouter.CompleteAsync(key, attempt.Request.ModelId, attempt.Messages, attempt.MaxOutput, 0.0, ReasoningEffort, token);
        }
        catch (OpenRouterHttpException ex)
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
        return new DreamModelReply(answer.Content, prompt, completion, answer.Model, CutOff: WasCutOff(answer));
    }

    /// <summary>The output limit stopped the answer: the provider said so (<c>finish_reason</c> length), or the model
    /// reasoned and sent no content, which is the same thing seen from the other side (the cap went on the reasoning).</summary>
    internal static bool WasCutOff(ChatCompleteResponse answer) =>
        answer.FinishReason is "length" or "max_tokens"
        || (string.IsNullOrWhiteSpace(answer.Content) && answer.HasReasoning);

    /// <summary>Why a round found no key to try. Keys that are enabled and not paused but could not be decrypted (sealed
    /// under a key this node no longer holds) are a setup problem, not a pause: no wait cures it, the administrator has to
    /// enter the key again, so the step ends at once.</summary>
    private async Task<Failure> NoUsableKeyAsync()
    {
        if ((await _repo.ListAvailableOrderedAsync()).Count > 0)
            return new Failure("An OpenRouter key cannot be read on this node; enter it again in Admin, AI / Chat.", Permanent: true);
        return await AnyKeyEnabledAsync()
            ? new Failure("Every OpenRouter key is paused.", Permanent: false)
            : new Failure("No OpenRouter key is enabled; add one in Admin, AI / Chat.", Permanent: true);
    }

    private async Task<bool> AnyKeyEnabledAsync() => (await _repo.ListAsync()).Any(k => k.Enabled);
}
