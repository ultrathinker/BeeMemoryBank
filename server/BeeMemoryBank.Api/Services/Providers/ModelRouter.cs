using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>A decrypted provider key, held transiently for one turn. <see cref="Id"/> is empty for the one keyless attempt a local provider
/// with no key gets.</summary>
public sealed record KeyMaterial(Guid Id, Guid ProviderId, string PlaintextKey)
{
    public bool IsKeyless => Id == Guid.Empty;
}

/// <summary>One model of a chain, ready to be called: its provider approved (<see cref="Target"/>), its protocol's client found, its keys
/// open. A key here belongs to <see cref="Target"/>'s provider and to no other.</summary>
public sealed record ModelRoute(ChatModelRow Model, ProviderEgressTarget Target, IModelProtocolClient Client, IReadOnlyList<KeyMaterial> Keys)
{
    public string ProviderName => Target.Name;

    public ModelCall Call(KeyMaterial key) =>
        key.ProviderId == Target.ProviderId
            ? new ModelCall(Target, key.PlaintextKey, Model.ModelId)
            : throw new InvalidOperationException("A key is only ever used for its own provider.");
}

/// <summary>Why a model of a chain cannot be called now. <see cref="Permanent"/>: no wait cures it (not approved, no key, a key that does
/// not open on this node, no client for the protocol); otherwise every key is only paused.</summary>
public sealed record RouteProblem(ChatModelRow Model, string Message, bool Permanent);

/// <summary>Every model of the chain was tried (or could not be) and none answered. Surfaced as a 502 (JSON endpoints) or an
/// <c>event: error</c> SSE frame (streaming loop). Deliberately NOT derived from <see cref="InvalidOperationException"/> so it is
/// distinguishable from a malformed-but-200 upstream response (which has nothing to do with a bad key and is not failed over).</summary>
public sealed class AllKeysExhaustedException(string message) : Exception(message);

/// <summary>What a failed request says about the key it was sent with. <see cref="Request"/>: nothing - the request itself was refused, so no
/// other key of that provider is tried and the key is not marked; the next model of the chain is.</summary>
public enum KeyFailureKind { Disable, Cooldown, Transient, Request }

/// <summary>Whether a chain can be called now (<see cref="ModelRouter.ReadinessAsync"/>).</summary>
public enum ModelReadiness { Ready, NotApproved, NoKey }

/// <summary>
/// Where a model request goes, for the chat and for Dream. Two levels of failover, both here:
/// <list type="number">
/// <item><b>Keys of one provider</b>, as the chat had them for OpenRouter: every available key (enabled, not cooling down) in priority order; a
/// 401 disables the key, a 402 or 429 pauses it for five minutes, any other status or a transport error tries the next key; the key table the
/// admin page shows is updated the same way. A request error (400, 404, 413, 422: a schema the provider refuses, a model it does not know, a
/// request too long) is not the key's: every key would get the same answer, so none is tried and none is marked, and the chain moves on to
/// its next model, which may accept the request.</item>
/// <item><b>The fallback chain</b>: the default text model (or Dream's model) followed by its ordered fallback models, which may belong to
/// other providers. When a model cannot be called (its provider not approved, no usable key, the egress refused) or every key of its provider
/// failed, the next model is tried. Streaming fails over only before the first byte (<c>ChatEndpoints.StreamWithFailoverAsync</c>).</item>
/// </list>
/// A model's provider is approved or nothing is sent to it (<see cref="ChatProviderRepository.ResolveTargetAsync"/>), and a key is only ever
/// put on a request for its own provider's target.
/// </summary>
public sealed class ModelRouter(ChatSettingsRepository repo, ChatProviderRepository providers, ModelClientRegistry clients)
{
    /// <summary>Cooldown applied on 402 (insufficient credits) / 429 (rate limit): the key is retried automatically once this elapses. 401
    /// (unauthorized/revoked) disables the row until an admin re-enables it.</summary>
    public static readonly TimeSpan KeyCooldownWindow = TimeSpan.FromMinutes(5);

    public static KeyFailureKind ClassifyKeyFailure(int statusCode) => statusCode switch
    {
        401 => KeyFailureKind.Disable,
        402 or 429 => KeyFailureKind.Cooldown,
        _ when IsRequestError(statusCode) => KeyFailureKind.Request,
        _ => KeyFailureKind.Transient
    };

    /// <summary>The provider refused the request itself, not the key: 400 (invalid request: a schema, a field, a context too long), 404 (no such
    /// model), 413 (too large), 422 (unprocessable). Another key of the same provider would get the same answer. (Google's invalid key arrives
    /// as 400 <c>API_KEY_INVALID</c> and is mapped to 401 by its client before it gets here.)</summary>
    public static bool IsRequestError(int statusCode) => statusCode is 400 or 404 or 413 or 422;

    public ChatSettingsRepository Settings => repo;

    public async Task RecordKeyOutcomeAsync(KeyMaterial key, KeyFailureKind kind, string lastError)
    {
        if (key.IsKeyless || kind == KeyFailureKind.Request) return;
        switch (kind)
        {
            case KeyFailureKind.Disable:
                await repo.RecordKeyFailureAsync(key.Id, disable: true, disabledUntil: null, lastError);
                break;
            case KeyFailureKind.Cooldown:
                await repo.RecordKeyFailureAsync(key.Id, disable: false, disabledUntil: DateTime.UtcNow + KeyCooldownWindow, lastError);
                break;
            default:
                await repo.RecordKeyFailureAsync(key.Id, disable: false, disabledUntil: null, lastError);
                break;
        }
    }

    public Task RecordKeySuccessAsync(KeyMaterial key) => key.IsKeyless ? Task.CompletedTask : repo.RecordKeySuccessAsync(key.Id);

    /// <summary>A transport error is not the key's fault: noted, the key stays available.</summary>
    public Task RecordTransportErrorAsync(KeyMaterial key, string message) =>
        key.IsKeyless ? Task.CompletedTask : repo.RecordUsageAsync(key.Id, message);

    /// <summary>The fallback models of the default text model, in order (missing and non-text models left out).</summary>
    public async Task<List<ChatModelRow>> FallbackModelsAsync()
    {
        var list = new List<ChatModelRow>();
        foreach (var id in await repo.GetFallbackModelIdsAsync())
            if (await repo.GetModelByIdAsync(id) is { IsText: true } m) list.Add(m);
        return list;
    }

    /// <summary><paramref name="first"/> followed by the fallback models, each once.</summary>
    public async Task<List<ChatModelRow>> TextChainAsync(ChatModelRow first)
    {
        var chain = new List<ChatModelRow> { first };
        foreach (var m in await FallbackModelsAsync())
            if (chain.All(c => c.Id != m.Id)) chain.Add(m);
        return chain;
    }

    /// <summary>
    /// Makes one model callable: its provider exists and is approved, a client speaks its protocol, and its keys are opened (needs an
    /// unlocked vault). An undecryptable key is skipped with a note on its row. A local provider with no key at all gets one keyless
    /// attempt (Ollama and the like need none).
    /// </summary>
    public async Task<(ModelRoute? Route, RouteProblem? Problem)> PrepareAsync(ChatModelRow model)
    {
        var provider = await providers.GetAsync(model.ProviderId);
        if (provider is null)
            return (null, new RouteProblem(model, model.ProviderId == BuiltInOpenRouter.Id
                ? "No OpenRouter key is enabled; add one in Admin, AI / Chat."
                : $"The provider of model '{model.Label}' no longer exists.", Permanent: true));
        if (!clients.IsAvailable(provider.Protocol))
            return (null, new RouteProblem(model, $"This node has no client for the '{provider.Protocol}' protocol of provider '{provider.Name}' yet.", Permanent: true));

        // A locked vault throws SessionLockedException here, as the chat's key reading always has.
        var (target, problem) = await providers.ResolveTargetAsync(provider);
        if (target is null)
            return (null, new RouteProblem(model, problem!, Permanent: true));

        var keys = await OpenKeysAsync(provider);
        if (keys.Count > 0)
            return (new ModelRoute(model, target, clients.Resolve(provider.Protocol), keys), null);

        var all = await repo.ListByProviderAsync(provider.Id);
        if (all.Count == 0 && provider.IsLocal)
            return (new ModelRoute(model, target, clients.Resolve(provider.Protocol), [new KeyMaterial(Guid.Empty, provider.Id, "")]), null);
        if (all.Count == 0)
            return (null, new RouteProblem(model, $"Provider '{provider.Name}' has no key; add one in Admin, AI / Chat.", Permanent: true));
        if (!all.Any(k => k.Enabled))
            return (null, new RouteProblem(model, $"Every key of provider '{provider.Name}' was refused and disabled.", Permanent: true));
        if ((await repo.ListAvailableOrderedAsync(provider.Id)).Count > 0)
            return (null, new RouteProblem(model, $"A key of provider '{provider.Name}' cannot be read on this node; enter it again in Admin, AI / Chat.", Permanent: true));
        return (null, new RouteProblem(model, $"Every key of provider '{provider.Name}' is paused (cooling down).", Permanent: false));
    }

    private async Task<List<KeyMaterial>> OpenKeysAsync(ChatProvider provider)
    {
        var rows = await repo.ListAvailableOrderedAsync(provider.Id);
        var secrets = await repo.OpenSecretsAsync(rows);
        var result = new List<KeyMaterial>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if (secrets[i] is { } secret)
            {
                result.Add(new KeyMaterial(rows[i].Id, provider.Id, secret));
                continue;
            }
            // Skip — this key can't be used until it is re-entered. Note it for admin.
            await repo.RecordUsageAsync(rows[i].Id, "decrypt failed");
        }
        return result;
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> against the route's keys in order, retrying on a key-specific HTTP failure (401/402/429 →
    /// disable/cooldown, any other status → next) or a transport error, the HTTP client's timeout included (next, no cooldown; see
    /// <see cref="IsNoAnswerInTime"/>). Records success on the winning key. Throws
    /// <see cref="AllKeysExhaustedException"/> if every key failed. A refusal by the egress (<see cref="ProviderUnavailableException"/>) ends
    /// the route at once: another key would be refused the same way. So does a request error (<see cref="IsRequestError"/>), as an
    /// <see cref="AllKeysExhaustedException"/> with its message and nothing recorded on the key.
    /// </summary>
    public async Task<T> RunOnRouteAsync<T>(ModelRoute route, Func<ModelCall, CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        string? last = null;
        foreach (var key in route.Keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await attempt(route.Call(key), ct);
                await RecordKeySuccessAsync(key);
                return result;
            }
            catch (ProviderHttpException ex) when (IsRequestError(ex.StatusCode))
            {
                // The request, not the key: every key would get the same answer. Nothing is marked; the chain's next model may accept it.
                throw new AllKeysExhaustedException(ex.Message);
            }
            catch (ProviderHttpException ex)
            {
                await RecordKeyOutcomeAsync(key, ClassifyKeyFailure(ex.StatusCode), ex.Message);
                last = ex.Message;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                await RecordTransportErrorAsync(key, ex.Message);
                last = ex.Message;
            }
            catch (OperationCanceledException) when (IsNoAnswerInTime(ct))
            {
                // The HTTP client's own timeout (the provider stalled), not the caller going away: a transport failure of this key.
                await RecordTransportErrorAsync(key, NoAnswerInTime);
                last = NoAnswerInTime;
            }
        }
        throw new AllKeysExhaustedException(last ?? "All configured API keys failed.");
    }

    /// <summary>What a key's note and a failover summary say about a request the provider did not answer in time.</summary>
    public const string NoAnswerInTime = "The provider did not answer in time.";

    /// <summary>
    /// Whether a cancellation that just ended a provider call is the provider's fault: the caller's own token is NOT cancelled, so it was the
    /// HTTP client's timeout (a provider that stalled before its headers), and the call counts as a transport failure of that key - the next
    /// key, then the next model, is tried. When the caller's token IS cancelled (the browser went away, Dream was stopped) it is the caller's,
    /// and nothing is retried.
    /// </summary>
    public static bool IsNoAnswerInTime(CancellationToken callerToken) => !callerToken.IsCancellationRequested;

    /// <summary>
    /// Runs <paramref name="attempt"/> along the chain: each model is prepared when it is reached, its keys failed over by
    /// <see cref="RunOnRouteAsync{T}"/>, and the next model is tried when it cannot be called or all its keys failed. Returns the answer and
    /// the route that gave it (which model of which provider answered). Throws <see cref="AllKeysExhaustedException"/> naming why each model
    /// failed when none answered. A malformed 200 (<see cref="InvalidOperationException"/>) is not failed over.
    /// </summary>
    public async Task<(T Value, ModelRoute Route)> RunAsync<T>(IReadOnlyList<ChatModelRow> chain,
        Func<ModelRoute, ModelCall, CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var model in chain)
        {
            ct.ThrowIfCancellationRequested();
            var (route, problem) = await PrepareAsync(model);
            if (route is null)
            {
                failures.Add(problem!.Message);
                continue;
            }
            try
            {
                var value = await RunOnRouteAsync(route, (call, token) => attempt(route, call, token), ct);
                return (value, route);
            }
            catch (AllKeysExhaustedException ex)
            {
                failures.Add(ex.Message);
            }
            catch (ProviderUnavailableException ex)
            {
                failures.Add(ex.Message);
            }
        }
        throw new AllKeysExhaustedException(Summarize(failures));
    }

    /// <summary>
    /// Whether some model of <paramref name="chain"/> can be called now, without opening any key: its provider exists, a client speaks its
    /// protocol, it is approved, and it has an available key (or is local and has none). While the vault is locked a sealed approval cannot
    /// be checked and counts as given here: this answer is advisory (a status line, whether a run may start); the call itself checks again.
    /// When none can, the reason of the first model is returned.
    /// </summary>
    public async Task<ModelReadiness> ReadinessAsync(IReadOnlyList<ChatModelRow> chain)
    {
        ModelReadiness? first = null;
        foreach (var model in chain)
        {
            var r = await ReadinessOfAsync(model);
            if (r == ModelReadiness.Ready) return r;
            first ??= r;
        }
        return first ?? ModelReadiness.NoKey;
    }

    private async Task<ModelReadiness> ReadinessOfAsync(ChatModelRow model)
    {
        var provider = await providers.GetAsync(model.ProviderId);
        if (provider is null) return ModelReadiness.NoKey;
        if (!clients.IsAvailable(provider.Protocol) || (await providers.CheckApprovalsAsync([provider]))[0] == false)
            return ModelReadiness.NotApproved;
        if ((await repo.ListAvailableOrderedAsync(provider.Id)).Count > 0) return ModelReadiness.Ready;
        return provider.IsLocal && (await repo.ListByProviderAsync(provider.Id)).Count == 0 ? ModelReadiness.Ready : ModelReadiness.NoKey;
    }

    /// <summary>One failure as it is; several as "model 1: ...; model 2: ..." so the person sees why each fallback failed.</summary>
    public static string Summarize(IReadOnlyList<string> failures) => failures.Count switch
    {
        0 => "No model is configured.",
        1 => failures[0],
        _ => string.Join(" ", failures.Select((f, i) => $"[{(i == 0 ? "model" : "fallback " + i)}] {f}")),
    };
}
