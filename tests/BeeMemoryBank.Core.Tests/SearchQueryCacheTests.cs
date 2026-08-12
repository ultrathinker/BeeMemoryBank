using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using System.Collections.Concurrent;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Tests for <see cref="SearchQueryCache"/> (WP-17): single-flight coalescing, ACL-safe cache
/// keying, TTL expiry, bounded eviction, and fault handling.
///
/// <para>
/// The load-bearing test (<see cref="SingleFlight_NConcurrentIdentical_InvokeFactoryOnce"/>)
/// mirrors the style of <c>IndexBuilderConcurrencyTests</c> / <c>SearchContentConcurrencyTests</c>:
/// real concurrent <see cref="Task.Run"/> threads with deterministic operation counts, where the
/// assertion is a structural property (factory invoked exactly once) that holds under ANY thread
/// interleaving — not a timing accident.
/// </para>
/// </summary>
public class SearchQueryCacheTests
{
    // Matches the brief's example target concurrency (~20 concurrent users/agents).
    private const int ConcurrentCallers = 20;

    private static SearchResults MakeResult(params (Guid id, string title)[] articles)
        => new([], articles.Select(a => new Article { Id = a.id, Title = a.title }).ToList());

    private static readonly Guid Alpha = Guid.NewGuid();
    private static readonly Guid PublicId = Guid.NewGuid();
    private static readonly Guid SecretId = Guid.NewGuid();

    // ─────────────────────────────────────────────────────────────────────────────
    // 1. Single-flight coalescing — the load-bearing test for this WP.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SingleFlight_NConcurrentIdentical_InvokeFactoryOnce()
    {
        var cache = new SearchQueryCache();
        int factoryCalls = 0;

        // Gate: the (single) in-flight factory call stays open until every concurrent caller has
        // had a chance to register on it. This shapes genuine concurrency — the assertion itself
        // (factoryCalls == 1) is timing-independent under the cache's unified design, so this gate
        // is not the thing being asserted, just what makes the run meaningfully concurrent.
        var callersStarted = new CountdownEvent(ConcurrentCallers);
        var releaseFactory = new ManualResetEventSlim(false);

        async Task<SearchResults> Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            releaseFactory.Wait(TimeSpan.FromSeconds(10));
            return MakeResult((Alpha, "alpha"));
        }

        var tasks = new List<Task<SearchResults>>(ConcurrentCallers);
        for (int i = 0; i < ConcurrentCallers; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                callersStarted.Signal();
                return await cache.ExecuteAsync("search", "query", "scope-A", Factory);
            }));
        }

        // Wait until all callers are running, then let the in-flight factory complete. A short
        // delay here only widens the concurrency window — it is not an assertion.
        callersStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("all callers must start");
        await Task.Delay(50);
        releaseFactory.Set();

        var results = await Task.WhenAll(tasks);

        factoryCalls.Should().Be(1,
            "{0} concurrent identical calls must coalesce onto a single factory invocation (single-flight), " +
            "not each pay the full cost independently", ConcurrentCallers);
        results.Should().HaveCount(ConcurrentCallers);
        foreach (var r in results)
        {
            r.Articles.Should().HaveCount(1);
            r.Articles[0].Id.Should().Be(Alpha);
            r.Articles[0].Title.Should().Be("alpha");
        }
    }

    [Fact]
    public async Task SingleFlight_SecondCallWithinTtl_DoesNotReinvokeFactory()
    {
        var cache = new SearchQueryCache();
        int factoryCalls = 0;

        var first = await cache.ExecuteAsync("search", "q", "scope", () =>
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(MakeResult((Alpha, "alpha")));
        });
        var second = await cache.ExecuteAsync("search", "q", "scope", () =>
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(MakeResult((Guid.NewGuid(), "should-not-happen")));
        });

        factoryCalls.Should().Be(1, "the second call is within the TTL window and must be served from cache");
        second.Articles[0].Id.Should().Be(Alpha, "cached hit returns the structurally identical result, not a freshly-computed one");
    }

    // A cache hit must return a copy that is structurally identical to the uncached result, but
    // must NOT hand out the cached list instances themselves (a caller could otherwise mutate the
    // shared list and poison the cache). Asserting content equality + reference-distinct lists
    // proves both the identity and the defensive copy.
    [Fact]
    public async Task CacheHit_ReturnsStructurallyIdenticalButDistinctCopy()
    {
        var cache = new SearchQueryCache();

        var first = await cache.ExecuteAsync("search", "q", "scope",
            () => Task.FromResult(MakeResult((Alpha, "alpha"))));
        var second = await cache.ExecuteAsync("search", "q", "scope",
            () => Task.FromResult(MakeResult((Guid.NewGuid(), "other"))));

        second.Articles.Should().HaveCount(1);
        second.Articles[0].Id.Should().Be(Alpha);
        second.Articles[0].Title.Should().Be("alpha");
        ReferenceEquals(first.Articles, second.Articles).Should().BeFalse(
            "a cache hit must hand back a defensive copy of the list, not the shared cached instance");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 2. ACL isolation — two scopes must never share a cached result.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DifferentScopeFingerprints_NeverShareCacheEntry()
    {
        var cache = new SearchQueryCache();
        int factoryCalls = 0;

        // Scope "public-only" can only see the Public article; scope "privileged" sees Public AND
        // Secret. These are two DIFFERENT effective visible-result-sets for the same query string.
        Task<SearchResults> PublicOnly() =>
            Task.FromResult(MakeResult((PublicId, "Public")));
        Task<SearchResults> PublicAndSecret() =>
            Task.FromResult(MakeResult((PublicId, "Public"), (SecretId, "Secret")));

        // 1. Privileged caller runs first and would seed the cache with Secret visible.
        var privileged = await cache.ExecuteAsync("search", "term", "scope-privileged", () =>
        {
            Interlocked.Increment(ref factoryCalls);
            return PublicAndSecret();
        });
        privileged.Articles.Select(a => a.Id).Should().Contain(SecretId);

        // 2. Restricted caller issues the SAME query string. If the cache ignored scope it would
        //    serve the privileged entry and LEAK Secret. It must instead re-execute under its own
        //    key and return only Public.
        var restricted = await cache.ExecuteAsync("search", "term", "scope-public-only", () =>
        {
            Interlocked.Increment(ref factoryCalls);
            return PublicOnly();
        });
        restricted.Articles.Select(a => a.Id).Should().NotContain(SecretId,
            "a restricted scope must never receive a result a privileged caller cached for the same query");
        restricted.Articles.Select(a => a.Id).Should().Contain(PublicId);

        factoryCalls.Should().Be(2, "the two scopes occupy different cache keys, so both compute");

        // 3. Even if the restricted caller's factory now tried to return Secret, the privileged
        //    re-query is served from the privileged entry — proving the entries are fully isolated
        //    and the restricted caller did not overwrite or poison the privileged one.
        var privilegedAgain = await cache.ExecuteAsync("search", "term", "scope-privileged", () =>
        {
            Interlocked.Increment(ref factoryCalls);
            return PublicOnly(); // would drop Secret if the cache served THIS; it must serve the cached entry instead
        });
        privilegedAgain.Articles.Select(a => a.Id).Should().Contain(SecretId,
            "the privileged entry is untouched by the restricted caller's computation");
        factoryCalls.Should().Be(2, "the privileged re-query is a cache hit (factory not invoked again)");
    }

    [Fact]
    public async Task SameScopeFingerprint_DifferentMethod_DoNotCollide()
    {
        // SearchAsync and SearchWithContentAsync return different results for the same query; the
        // method name is part of the key so they must occupy separate cache slots.
        var cache = new SearchQueryCache();
        int calls = 0;

        await cache.ExecuteAsync("search", "q", "scope", () => { calls++; return Task.FromResult(MakeResult((Alpha, "meta"))); });
        await cache.ExecuteAsync("searchContent", "q", "scope", () => { calls++; return Task.FromResult(MakeResult((SecretId, "body"))); });

        calls.Should().Be(2, "different methods are different queries and must both compute");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3. TTL expiry & key normalization.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ttl_WithinWindow_ServesCache_AfterExpiry_Reexecutes()
    {
        // Controlled clock so expiry is deterministic — no wall-clock waiting as an assertion.
        var now = DateTime.UtcNow;
        var cache = new SearchQueryCache(ttl: TimeSpan.FromSeconds(30), clock: () => now);
        int factoryCalls = 0;

        Task<SearchResults> Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(MakeResult((Alpha, "alpha")));
        }

        await cache.ExecuteAsync("search", "q", "scope", Factory);
        await cache.ExecuteAsync("search", "q", "scope", Factory); // within TTL
        factoryCalls.Should().Be(1, "second call within TTL is served from cache");

        now = now.AddSeconds(31); // cross the TTL boundary
        await cache.ExecuteAsync("search", "q", "scope", Factory);
        factoryCalls.Should().Be(2, "after TTL expiry a fresh call must re-execute, not serve stale data");
    }

    [Fact]
    public async Task NormalizeQuery_TrimsLeadingTrailingWhitespace()
    {
        var cache = new SearchQueryCache();
        int factoryCalls = 0;

        Task<SearchResults> Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(MakeResult((Alpha, "alpha")));
        }

        await cache.ExecuteAsync("search", "docker", "scope", Factory);
        await cache.ExecuteAsync("search", "   docker   ", "scope", Factory); // trimmed → same key
        factoryCalls.Should().Be(1, "leading/trailing whitespace is part of the query but does not change search results, so it is normalized out of the key");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 4. Bounded eviction.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Eviction_AboveCap_DropsEarliestExpiryFirst()
    {
        var now = DateTime.UtcNow;
        var cache = new SearchQueryCache(maxEntries: 2, clock: () => now);
        var invocations = new ConcurrentDictionary<string, int>();

        Task<SearchResults> FactoryFor(string key)
        {
            invocations.AddOrUpdate(key, 1, (_, c) => c + 1);
            return Task.FromResult(MakeResult((Alpha, key)));
        }

        // Insert three distinct keys with progressively-later expiries (each call advances the
        // clock so its TTL starts later).
        await cache.ExecuteAsync("search", "q1", "scope", () => FactoryFor("q1"));
        now = now.AddSeconds(1);
        await cache.ExecuteAsync("search", "q2", "scope", () => FactoryFor("q2"));
        now = now.AddSeconds(1);
        await cache.ExecuteAsync("search", "q3", "scope", () => FactoryFor("q3")); // triggers trim → q1 (earliest) evicted

        cache.EntryCount.Should().BeLessOrEqualTo(2, "the cache must never exceed its cap");

        // Verify the survivors are served from cache FIRST (a hit does not insert a new entry, so
        // it does not itself re-trigger eviction). Then re-access the evicted key, which must
        // recompute. (Re-accessing q1 last matters: its re-insertion evicts whichever survivor now
        // has the earliest expiry, but by then we have already recorded q2/q3 as hits.)
        now = now.AddSeconds(1);
        await cache.ExecuteAsync("search", "q2", "scope", () => FactoryFor("q2"));
        await cache.ExecuteAsync("search", "q3", "scope", () => FactoryFor("q3"));
        await cache.ExecuteAsync("search", "q1", "scope", () => FactoryFor("q1"));

        invocations["q1"].Should().Be(2, "q1 (earliest-expiry) was evicted when q3 pushed the cache over cap, so re-access recomputes");
        invocations["q2"].Should().Be(1, "q2 survived eviction and was served from cache");
        invocations["q3"].Should().Be(1, "q3 survived eviction and was served from cache");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 5. Fault handling — a fault is not cached; a retry re-executes.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fault_Propagates_AndIsNotCached_RetryReexecutes()
    {
        var cache = new SearchQueryCache();
        int factoryCalls = 0;
        var shouldThrow = true;

        async Task<SearchResults> Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            await Task.Yield();
            if (shouldThrow)
                throw new InvalidOperationException("boom");
            return MakeResult((Alpha, "alpha"));
        }

        var firstAct = async () => await cache.ExecuteAsync("search", "q", "scope", Factory);
        await firstAct.Should().ThrowAsync<InvalidOperationException>();

        // The fault must not have been cached, and the in-flight slot must have been reclaimed.
        shouldThrow = false;
        var result = await cache.ExecuteAsync("search", "q", "scope", Factory);

        factoryCalls.Should().Be(2, "the faulted attempt was not cached, so the retry re-executes the factory");
        result.Articles[0].Id.Should().Be(Alpha);
    }
}
