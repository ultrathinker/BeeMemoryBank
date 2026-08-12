using BeeMemoryBank.Core.Models;
using System.Collections.Concurrent;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Single-flight coalescing + short-TTL result cache for <see cref="SearchService"/>'s query
/// methods (WP-17).
///
/// <para>
/// <b>Why this is a singleton.</b> The cache lives across requests so that concurrent callers in
/// different DI scopes (different <see cref="SearchService"/> instances) can coalesce identical
/// in-flight queries onto one shared <see cref="Task{TResult}"/>. A scoped cache would defeat the
/// whole point — every request would get its own empty cache and nothing would coalesce.
/// </para>
///
/// <para>
/// <b>One dictionary, not two.</b> A single <see cref="ConcurrentDictionary{TKey, TValue}"/> of
/// <c>Lazy&lt;Task&lt;CacheResult&gt;&gt;</c> serves BOTH roles: while the inner task is running it
/// is the single-flight rendezvous (every concurrent caller for the key awaits the SAME task), and
/// once it completes it IS the TTL cache (its <see cref="CacheResult.ExpiresAt"/> is checked before
/// serving). Unifying them eliminates the straddle race a separate (in-flight, ttl) pair would
/// have: a caller could otherwise observe a TTL miss, then by the time it reaches the in-flight
/// table find the entry already removed (the owner finished and cleaned up) and start a redundant
/// second computation. With one structure that sequence is impossible — the entry the caller
/// coalesced onto is the same entry that later holds the cached result.
/// </para>
///
/// <para>
/// <b>"Re-run after expiry" without eager removal.</b> The brief asks that a fresh call after the
/// in-flight work settles re-executes rather than forever serving the settled task. This design
/// keeps the settled entry (it is now the TTL cache) and instead <em>replaces</em> it atomically
/// (via <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/>) when a caller finds it expired.
/// The effect is identical — a post-expiry call recomputes — but without the race, and the settled
/// task is dropped for GC once the swap lands.
/// </para>
///
/// <para>
/// <b>ACL safety.</b> The cache key embeds the caller's <c>ReadScopeFingerprint</c>, a stable
/// digest of the read-visibility rules in <see cref="ICallerScope"/>. Two callers whose
/// fingerprints differ can never share an entry, so a more-privileged caller's result can never
/// leak to a less-privileged caller that issued the same query string. The cache never bypasses
/// the ACL; it only shares results between callers proven to see the same rows.
/// </para>
///
/// <para>
/// <b>Invalidation.</b> Freshness is bounded solely by <see cref="_ttl"/> (default 30s). See the
/// WP-17 report for why a proactive write-hook was rejected: the obvious candidate (the Lamport
/// clock) does NOT tick on concept-tag mutations, which DO affect FTS5 search results, so it is an
/// incomplete invalidation signal. A clean, honest TTL is preferred over a fragile half-signal.
/// </para>
/// </summary>
public sealed class SearchQueryCache
{
    /// <summary>
    /// Default result TTL. 30s sits at the top of the brief's suggested 20-30s range: long enough
    /// to absorb a read burst (several users/agents issuing the same popular query back-to-back)
    /// and let both single-flight and the TTL cache pay off, short enough that a user re-running a
    /// search shortly after an edit sees fresh results within "feels live" time for a 20-user
    /// collaborative knowledge base.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hard cap on live entries so a long-running process facing many distinct queries can't grow
    /// the cache without bound. When exceeded, completed entries with the earliest expiry are
    /// evicted first (which naturally prioritizes already-expired ones). Generously sized for ~20
    /// concurrent users/agents: even a fully-distinct query from every caller stays well under it.
    /// </summary>
    public const int DefaultMaxEntries = 512;

    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly Func<DateTime> _clock;

    private readonly ConcurrentDictionary<QueryKey, Lazy<Task<CacheResult>>> _entries = new();

    public SearchQueryCache(TimeSpan? ttl = null, int? maxEntries = null, Func<DateTime>? clock = null)
    {
        _ttl = ttl ?? DefaultTtl;
        if (_ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), _ttl, "TTL must be positive (a non-positive TTL would make every entry instantly expire and loop forever trying to refresh it).");
        var max = maxEntries ?? DefaultMaxEntries;
        if (max <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), max, "Must be positive.");
        _maxEntries = max;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Number of keys currently tracked (in-flight or cached). Test/diagnostic hook.</summary>
    public int EntryCount => _entries.Count;

    /// <summary>
    /// Returns the result for <paramref name="method"/>/<paramref name="query"/> under the given
    /// ACL <paramref name="scopeFingerprint"/>. Concurrent callers for the same key all await a
    /// single invocation of <paramref name="factory"/> (single-flight); once it settles, the result
    /// is served from cache until its TTL elapses, after which the next call recomputes.
    /// <paramref name="factory"/> is the underlying search call and must already reflect the
    /// caller's scope (the repos apply the ambient scope at execution time).
    /// </summary>
    public async Task<SearchResults> ExecuteAsync(
        string method,
        string query,
        string scopeFingerprint,
        Func<Task<SearchResults>> factory)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scopeFingerprint);
        ArgumentNullException.ThrowIfNull(factory);

        var key = new QueryKey(method, NormalizeQuery(query), scopeFingerprint);

        // Hot path: a completed, still-fresh entry is served without even building a candidate Lazy.
        while (true)
        {
            var now = _clock();

            if (_entries.TryGetValue(key, out var hot))
            {
                var settled = TryGetSettled(hot);
                if (settled is not null && settled.ExpiresAt > now)
                    return CopyResult(settled.Result);
            }

            // A fresh candidate Lazy for this key. GetOrAdd takes the value directly (no factory
            // delegate), so if the key is already present we coalesce onto the existing entry and
            // this candidate is simply never awaited (GC'd). ExecutionAndPublication guarantees the
            // winning Lazy's factory runs exactly once across all coalesced callers.
            var candidate = new Lazy<Task<CacheResult>>(
                () => ComputeWithExpiryAsync(factory), LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<Task<CacheResult>> entry = _entries.GetOrAdd(key, candidate);

            CacheResult cr;
            try
            {
                cr = await entry.Value.ConfigureAwait(false);
            }
            catch
            {
                // Faulted task: every coalesced caller gets the exception. Reclaim the slot so a
                // later retry re-executes instead of forever rethrowing the cached fault. The
                // reference-conditional remove avoids evicting an entry a concurrent expiry-swap
                // may have already replaced with a fresh one.
                RemoveEntryIfMine(key, entry);
                throw;
            }

            if (cr.ExpiresAt > now)
            {
                // Fresh: either just computed or still within TTL. Single-flight AND cache hit,
                // resolved by the same entry.
                EnforceCap();
                return CopyResult(cr.Result);
            }

            // Expired: race to install a fresh entry that will recompute. TryUpdate is atomic and
            // conditional on `entry`, so among the concurrent callers that found this entry expired,
            // exactly one wins the swap and recomputes; the rest loop and pick up the winner's new
            // entry (no duplicate recomputation). If we lose, someone else already replaced it and
            // we loop onto theirs.
            var fresh = new Lazy<Task<CacheResult>>(
                () => ComputeWithExpiryAsync(factory), LazyThreadSafetyMode.ExecutionAndPublication);
            _entries.TryUpdate(key, fresh, entry);
            // Loop: re-read the (now-replaced) entry and either await the winner's recomputation or,
            // if we won, await our own `fresh`.
        }
    }

    private async Task<CacheResult> ComputeWithExpiryAsync(Func<Task<SearchResults>> factory)
    {
        var result = await factory().ConfigureAwait(false);
        return new CacheResult(result, _clock() + _ttl);
    }

    /// <summary>
    /// Normalizes the raw query string into the cache-key form. Only leading/trailing whitespace
    /// is stripped: case-folding is deliberately NOT applied, because the cache key's notion of
    /// equality must never be coarser than the underlying search engine's. The FTS5 tokenizer, the
    /// SQLite LIKE used for partial-id matching, and the OrdinalIgnoreCase body scan each fold
    /// case in subtly different (and locale-dependent) ways; if the key lowercased two queries the
    /// engine treats as distinct, the cache would serve a wrong result. Trimming is safe (the
    /// engine trims whitespace too), so we trim and stop there — trading a little hit rate for a
    /// hard guarantee that we never over-share.
    /// </summary>
    private static string NormalizeQuery(string query)
    {
        var trimmed = query.AsSpan().Trim();
        return trimmed.Length == query.Length ? query : trimmed.ToString();
    }

    // Bounded-size guard. Runs on every served result, but the fast path (count under cap) is a
    // single read. Only the rare over-cap case does the eviction sweep, which removes settled
    // entries earliest-expiry-first (expired ones go first naturally). In-flight / faulted entries
    // are left alone when possible so a concurrent coalesced caller isn't orphaned onto a second
    // running computation; if even after evicting every settled entry we're still over cap
    // (pathological: everything in flight), we fall back to trimming the oldest in-flight entries
    // too, since an unbounded cache is the worse failure.
    private void EnforceCap()
    {
        if (_entries.Count <= _maxEntries)
            return;

        var now = _clock();

        // Phase 1: drop settled, already-expired entries (free — they'd be recomputed on next access).
        foreach (var kvp in _entries)
        {
            var settled = TryGetSettled(kvp.Value);
            if (settled is not null && settled.ExpiresAt <= now)
                RemoveEntry(kvp);
        }

        if (_entries.Count <= _maxEntries)
            return;

        // Phase 2: drop settled entries earliest-expiry-first until at cap.
        var phase2 = _entries
            .Select(e => (entry: e, settled: TryGetSettled(e.Value)))
            .Where(x => x.settled is not null)
            .OrderBy(x => x.settled!.ExpiresAt)
            .Take(_entries.Count - _maxEntries)
            .ToArray();
        foreach (var (entry, _) in phase2)
            RemoveEntry(entry);

        if (_entries.Count <= _maxEntries)
            return;

        // Phase 3 (rare): everything left is in flight or faulted. Trim until at cap.
        foreach (var kvp in _entries.ToArray().Take(_entries.Count - _maxEntries))
            RemoveEntry(kvp);
    }

    // Returns the cached result only if the Lazy's task has already run to completion — safe to
    // read synchronously without forcing computation, and safe to evict without orphaning an
    // in-flight coalesced caller. In-flight and faulted tasks return null (a faulted entry is
    // normally already reclaimed by the catch in ExecuteAsync; this just treats any straggler as
    // non-cacheable rather than rethrowing here).
    private static CacheResult? TryGetSettled(Lazy<Task<CacheResult>> lazy)
    {
        if (!lazy.IsValueCreated) return null;
        var task = lazy.Value;
        return task.Status == TaskStatus.RanToCompletion ? task.Result : null;
    }

    private void RemoveEntry(KeyValuePair<QueryKey, Lazy<Task<CacheResult>>> kvp)
        => ((ICollection<KeyValuePair<QueryKey, Lazy<Task<CacheResult>>>>)_entries).Remove(kvp);

    private void RemoveEntryIfMine(QueryKey key, Lazy<Task<CacheResult>> mine)
        => ((ICollection<KeyValuePair<QueryKey, Lazy<Task<CacheResult>>>>)_entries)
            .Remove(KeyValuePair.Create(key, mine));

    private static SearchResults CopyResult(SearchResults result)
        => new(new List<Folder>(result.Folders), new List<Article>(result.Articles));

    private readonly record struct QueryKey(string Method, string Query, string Scope);

    private sealed record CacheResult(SearchResults Result, DateTime ExpiresAt);
}
