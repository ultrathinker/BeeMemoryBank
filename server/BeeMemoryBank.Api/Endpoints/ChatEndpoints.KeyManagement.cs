using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.Providers;

namespace BeeMemoryBank.Api.Endpoints;

public static partial class ChatEndpoints
{
    // ── multi-key failover and the fallback chain ─────────────────────────
    //
    // The failover lives in ModelRouter (Services/Providers), shared with Dream: every available key of a model's provider in priority
    // order (401 disables a key, 402/429 pause it for five minutes, any other status or a transport error tries the next key), then the
    // next model of the default text model's fallback chain, which may belong to another provider. Nothing is sent to a provider that is
    // not approved. When every model of the chain failed, an AllKeysExhaustedException bubbles up (→ 502 JSON for the non-streaming
    // endpoints, → an `event: error` SSE frame for the streaming loop).

    /// <summary>The routes of one chat turn, prepared the first time each model of the chain is reached and kept for the turn (as the
    /// decrypted keys always were): a key paused during the turn is still tried by a later round of the same turn, never a provider the
    /// turn found unapproved.</summary>
    internal sealed class ChatRoutes(ModelRouter router, IReadOnlyList<ChatModelRow> chain)
    {
        private readonly Dictionary<Guid, (ModelRoute? Route, RouteProblem? Problem)> _prepared = new();

        public ModelRouter Router => router;
        public IReadOnlyList<ChatModelRow> Chain => chain;

        public async Task<(ModelRoute? Route, RouteProblem? Problem)> GetAsync(ChatModelRow model)
        {
            if (_prepared.TryGetValue(model.Id, out var known)) return known;
            var prepared = await router.PrepareAsync(model);
            _prepared[model.Id] = prepared;
            return prepared;
        }

        /// <summary>The first model of the chain that can be called now, or null with the reason none can (the turn is refused before it
        /// commits to a stream).</summary>
        public async Task<(ModelRoute? Route, string? Problem)> FirstReadyAsync()
        {
            var problems = new List<string>();
            foreach (var model in chain)
            {
                var (route, problem) = await GetAsync(model);
                if (route is not null) return (route, null);
                problems.Add(problem!.Message);
            }
            return (null, ModelRouter.Summarize(problems));
        }

        /// <summary>Drops the decrypted keys once the turn is over.</summary>
        public void Forget() => _prepared.Clear();
    }

    // Short display fragment (never the full secret). Mirrors AgentKeyHelper.GetKeyPrefix length.
    private static string ComputeKeyPrefix(string apiKey)
        => apiKey.Length > 12 ? string.Concat(apiKey.AsSpan(0, 12), "…") : apiKey;
}
