using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// End-to-end ACL-isolation test for the WP-17 query cache, exercised through the real
/// <see cref="SearchService"/> (which wraps its query methods in <see cref="SearchQueryCache"/>).
///
/// <para>
/// This is the safety test the brief asks for: two callers with <em>different effective
/// visible-result-sets</em> for the same query string must never share a cached result. If the
/// cache key omitted the caller's read-scope, a privileged caller's result (which includes a
/// Secret article) would be served to a restricted caller and the assertion below would fail. So
/// a passing run proves the scope is load-bearing in the key, not just that two scope objects
/// happen to differ in code.
/// </para>
/// </summary>
public class SearchQueryCacheAclIntegrationTests : TestFixture
{
    private const string SharedTerm = "wp17sharedneedle";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "Wp17Node", "password");
        await Session.UnlockAsync("password");

        // Seed two articles matching the SAME query term in two different folders. Both are
        // reachable by the metadata (title) search path, so the assertion is about ACL filtering,
        // not about which search tier found them.
        ScopeHolder.Scope = SystemCallerScope.Instance;
        await ArticleService.CreateAsync("Public Doc " + SharedTerm, "/Public", [], SharedTerm + " body");
        await ArticleService.CreateAsync("Secret Doc " + SharedTerm, "/Secret", [], SharedTerm + " body");
    }

    private static HashSet<string> Set(params string[] paths)
        => new(paths, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<string> Titles(SearchResults r) => r.Articles.Select(a => a.Title);

    [Fact]
    public async Task SameQuery_DifferentScopes_NeverLeakAcrossCacheEntries()
    {
        // 1. Privileged (system) caller: sees BOTH articles. This seeds the cache for the
        //    (query, "sys") key with Secret visible — the dangerous entry a leak would expose.
        ScopeHolder.Scope = SystemCallerScope.Instance;
        var privileged = await SearchService.SearchAsync(SharedTerm);
        Titles(privileged).Should().Contain(new[] { "Public Doc " + SharedTerm, "Secret Doc " + SharedTerm },
            "the privileged caller sees the whole vault");

        // 2. Restricted caller (allow /Public only) issues the SAME query string. It must NOT
        //    receive the privileged caller's cached entry — it must re-execute under its own ACL
        //    key and see only Public. If the cache leaked, Secret would be present here.
        ScopeHolder.Scope = new HttpCallerScope(isSuperadmin: false, denyPaths: Set(), allowPaths: Set("/Public"));
        var restrictedToPublic = await SearchService.SearchAsync(SharedTerm);
        Titles(restrictedToPublic).Should().NotContain(t => t.StartsWith("Secret"),
            "a caller scoped to /Public must never see the Secret article, even though a privileged caller just cached the identical query");
        Titles(restrictedToPublic).Should().Contain(t => t.StartsWith("Public"));

        // 3. Back to privileged: must STILL see Secret — the restricted caller's computation must
        //    not have overwritten or poisoned the privileged cache entry (proves separate slots).
        ScopeHolder.Scope = SystemCallerScope.Instance;
        var privilegedAgain = await SearchService.SearchAsync(SharedTerm);
        Titles(privilegedAgain).Should().Contain(t => t.StartsWith("Secret"),
            "the privileged entry is isolated from the restricted caller's and remains intact");

        // 4. A DIFFERENT restricted scope (allow /Secret only) for the same query: sees Secret but
        //    not Public. Proves the keying is on the actual ACL contents, not just "privileged vs
        //    not-privileged" — two non-privileged scopes with different visible sets stay isolated.
        ScopeHolder.Scope = new HttpCallerScope(isSuperadmin: false, denyPaths: Set(), allowPaths: Set("/Secret"));
        var restrictedToSecret = await SearchService.SearchAsync(SharedTerm);
        Titles(restrictedToSecret).Should().Contain(t => t.StartsWith("Secret"));
        Titles(restrictedToSecret).Should().NotContain(t => t.StartsWith("Public"),
            "a caller scoped to /Secret must not see Public, proving ACL-content-based keying (not just a privileged/non-privileged split)");

        // 5. Reset to system scope for teardown safety.
        ScopeHolder.Scope = SystemCallerScope.Instance;
    }

    [Fact]
    public async Task SameQuery_SameScope_RepeatReturnsConsistentResults()
    {
        // Confirms a cache hit returns a structurally identical result to a fresh call, end to end
        // through the real SearchService + repos + FTS5.
        ScopeHolder.Scope = SystemCallerScope.Instance;

        var first = await SearchService.SearchAsync(SharedTerm);
        var second = await SearchService.SearchAsync(SharedTerm);

        Titles(second).Should().Equal(Titles(first),
            "a cache hit must return the same set of article titles in the same order as the fresh call");
    }
}
