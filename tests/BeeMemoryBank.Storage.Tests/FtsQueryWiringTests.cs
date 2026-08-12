using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// WP-07 tests: wiring of <see cref="ArticleRepository.SearchAsync"/> /
/// <see cref="FolderRepository.SearchAsync"/> to the FTS5 metadata index (migration 005) via
/// <see cref="FtsQueryBuilder"/>. Covers:
///   - the MATCH-expression builder directly (escaping, AND semantics, empty input, morphology);
///   - the differential guarantee: FTS results are a superset of the old
///     <c>unicode_contains</c> substring-scan results, with the token-boundary behavior change
///     (mid-word substrings like "art" in "article") documented and asserted explicitly;
///   - the <c>"сервера"</c>/<c>"сервер"</c> morphology example from WP-05, end-to-end through the
///     wiring (not just inside the stemmer library);
///   - soft-delete filtering (stale FTS rows must not surface);
///   - FTS5-special-character queries never crash;
///   - the preserved <see cref="ArticleRepository.SearchByExactSubstringAsync"/> still returns
///     what the pre-WP-07 path used to return.
/// </summary>
public class FtsQueryWiringTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private ArticleRepository _articleRepo = null!;
    private FolderRepository _folderRepo = null!;
    private CallerScopeHolder _scopeHolder = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_fts_wiring_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _scopeHolder = new CallerScopeHolder();
        _articleRepo = new ArticleRepository(_factory, _scopeHolder);
        _folderRepo = new FolderRepository(_factory, _scopeHolder);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ---- low-level row helpers (raw SQL, mirroring the sync/import write paths) ----

    private async Task<Guid> InsertArticleAsync(string title, string treePath = "/", string status = "A")
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at)
              VALUES (@id, @title, @treePath, @status, @now, @now)",
            new { id, title, treePath, status, now });
        return id;
    }

    private async Task<int> InsertTagAsync(string name)
    {
        using var conn = _factory.CreateConnection();
        return await conn.QuerySingleAsync<int>(
            "INSERT INTO tbl_concept_tag (name) VALUES (@name) RETURNING id",
            new { name });
    }

    private readonly Dictionary<string, int> _tagCache = new();

    private async Task<int> GetOrCreateTagAsync(string name)
    {
        // tbl_concept_tag.name is UNIQUE: the same tag name shared by several articles must be
        // inserted once and linked many times, not re-inserted per article.
        if (_tagCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        using var conn = _factory.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT id FROM tbl_concept_tag WHERE name = @name", new { name });
        var id = existing ?? await conn.QuerySingleAsync<int>(
            "INSERT INTO tbl_concept_tag (name) VALUES (@name) RETURNING id", new { name });
        _tagCache[name] = id;
        return id;
    }

    private async Task LinkTagAsync(Guid articleId, int tagId)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article_concept_tag (article_id, concept_tag_id) VALUES (@articleId, @tagId)",
            new { articleId, tagId });
    }

    private async Task<Guid> InsertFolderAsync(string path, string name)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        var parent = path == "/" ? null : path[..path.LastIndexOf('/', path.Length - 1)];
        if (parent == "") parent = null;
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_folder (id, path, name, parent_path, status, created_at, updated_at)
              VALUES (@id, @path, @name, @parent, 'A', @now, @now)",
            new { id, path, name, parent, now });
        return id;
    }

    private async Task SoftDeleteArticleAsync(Guid id)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET status = 'D', deleted_at = @now, updated_at = @now WHERE id = @id",
            new { id, now });
    }

    // =======================================================================
    // End-to-end: "сервера" <-> "сервер" morphology through the wiring
    // =======================================================================

    [Fact]
    public async Task SearchAsync_Ru_Morphology_Сервера_Finds_Сервер_And_ViceVersa()
    {
        // Article titled with the dictionary form "сервер"; also one tagged with "серверов".
        // The concrete WP-05 example: a query for "сервера" must find content stored as "сервер".
        var titled = await InsertArticleAsync("Сервер — главный узел");
        var tagged = await InsertArticleAsync("Нечто совершенно другое");
        var tagId = await InsertTagAsync("серверов");
        await LinkTagAsync(tagged, tagId);

        // Querying the inflected form "сервера" must find both the "сервер"-titled and the
        // "серверов"-tagged article (stem "сервер" prefix-matches all forms).
        var byInflected = await _articleRepo.SearchAsync("сервера");
        byInflected.Select(a => a.Id).Should().Contain(titled);
        byInflected.Select(a => a.Id).Should().Contain(tagged);

        // And the reverse: querying the dictionary form "сервер" also finds them.
        var byStem = await _articleRepo.SearchAsync("сервер");
        byStem.Select(a => a.Id).Should().Contain(titled);
        byStem.Select(a => a.Id).Should().Contain(tagged);
    }

    // =======================================================================
    // Differential test: FTS results are a superset of the old substring scan
    // (with the documented token-boundary exception)
    // =======================================================================

    [Fact]
    public async Task SearchAsync_Fts_Is_Superset_Of_Old_Substring_Scan_For_Whole_Word_Queries()
    {
        // Realistic mixed ru/en corpus with whole-word titles and tags.
        // (Russian words here deliberately avoid 'ё': FTS5's default unicode61 tokenizer removes
        // Latin diacritics but not Cyrillic ones, while the query-side stemmer strips 'ё' too —
        // so ё-words are a known indexing/query mismatch surfaced in the WP-07 report, not a bug
        // in this wiring. We keep the corpus ё-free so the differential assertion is exact.)
        var corpus = new (string title, string[] tags)[]
        {
            ("Postgres runbook", new[] { "database", "postgres" }),
            ("Redis cache notes", new[] { "database", "caching" }),
            ("Server maintenance log", new[] { "ops" }),
            ("Настройка серверов", new[] { "инфраструктура" }),
            ("Доклад по проекту", new[] { "проект", "доклад" }),
            ("Cooking recipes index", new[] { "food" }),
        };

        var ids = new List<(string title, Guid id)>();
        foreach (var (title, tags) in corpus)
        {
            var id = await InsertArticleAsync(title);
            ids.Add((title, id));
            foreach (var t in tags)
            {
                await LinkTagAsync(id, await GetOrCreateTagAsync(t));
            }
        }

        // Query strings chosen to match WHOLE WORDS (so the old substring scan matches them as a
        // proper word, not a mid-word accident). For every such query, the FTS path must find at
        // least every article the old path found.
        var wholeWordQueries = new[]
        {
            "runbook", "cache", "server", "maintenance", "серверов", "проект", "доклад",
            "database", "redis", "postgres"
        };

        foreach (var q in wholeWordQueries)
        {
            var oldHits = (await _articleRepo.SearchByExactSubstringAsync(q)).Select(a => a.Id).ToList();
            var newHits = (await _articleRepo.SearchAsync(q)).Select(a => a.Id).ToList();

            oldHits.Should().NotBeEmpty($"sanity: the old scan must find something for '{q}' to make the superset check meaningful");
            oldHits.Should().BeSubsetOf(newHits,
                "FTS must find at least every article the old exact-substring scan found for '{0}'", q);
        }
    }

    [Fact]
    public async Task SearchAsync_Fts_Finds_More_Than_Substring_Scan_Thanks_To_Morphology()
    {
        // "сервера" (inflected) is NOT a substring of "сервер" or "серверов", so the old scan
        // finds nothing; the FTS path (stem "сервер" prefix) finds both. This is the "finds more"
        // half of the differential guarantee.
        await InsertArticleAsync("Сервер недоступен");
        await InsertArticleAsync("Список серверов");

        var oldHits = await _articleRepo.SearchByExactSubstringAsync("сервера");
        var newHits = await _articleRepo.SearchAsync("сервера");

        oldHits.Should().BeEmpty("\"сервера\" is not a substring of \"сервер\" / \"серверов\"");
        newHits.Should().HaveCountGreaterOrEqualTo(2,
            "stem-based prefix matching spans the inflected forms the substring scan cannot");
    }

    [Fact]
    public async Task SearchAsync_Token_Boundary_Change_Mid_Word_Non_Prefix_Substring_No_Longer_Matches()
    {
        // The genuine token-boundary behavior change of moving from raw substring to prefix-based
        // FTS search: a query that the old scan matched ONLY because it was a *mid-word substring
        // that is not a token prefix* no longer matches.
        //
        // Note on the "art"/"article" example cited in the brief: under this WP's prefix-query
        // design ("art" -> "art" stem -> "art"*), "art" IS a prefix of the token "article", so
        // FTS matches it the same as the old scan did — no behavior change there. The behavior
        // change is narrower than the brief's illustration: it only bites for substrings that are
        // not a prefix of any token. "xyg" inside "Oxygen" is such a case.
        var id = await InsertArticleAsync("Oxygen tank");

        var oldHits = await _articleRepo.SearchByExactSubstringAsync("xyg");
        var newHits = await _articleRepo.SearchAsync("xyg");

        oldHits.Should().ContainSingle(a => a.Id == id,
            "raw substring scan matches 'xyg' inside 'Oxygen'");
        newHits.Should().BeEmpty(
            "FTS is token+prefix based: 'xyg' is neither a whole token nor a token-prefix of 'oxygen'");
    }

    [Fact]
    public async Task SearchAsync_Prefix_Substring_Still_Matches_No_Behavior_Change()
    {
        // Complement to the boundary test above: a query that is a *prefix* of a longer token
        // (e.g. "art" of "article") matches under BOTH the old substring scan and the new FTS
        // prefix path — so the token-boundary change does not affect prefix substrings, only
        // mid-word non-prefix ones. This documents the precise boundary the report draws.
        var id = await InsertArticleAsync("Article about something");

        var oldHits = await _articleRepo.SearchByExactSubstringAsync("art");
        var newHits = await _articleRepo.SearchAsync("art");

        oldHits.Should().ContainSingle(a => a.Id == id);
        newHits.Should().ContainSingle(a => a.Id == id,
            "'art' is a prefix of 'article', so the prefix query \"art\"* matches it just as the substring scan did");
    }

    // =======================================================================
    // Soft-delete: stale FTS rows must not surface
    // =======================================================================

    [Fact]
    public async Task SearchAsync_SoftDeleted_Article_With_Stale_Fts_Row_Does_Not_Surface()
    {
        var id = await InsertArticleAsync("Zeta unique runbook");
        // Soft-delete (status flip) does NOT fire the FTS delete trigger — the row stays indexed.
        await SoftDeleteArticleAsync(id);

        var results = await _articleRepo.SearchAsync("zeta");

        results.Should().NotContain(a => a.Id == id,
            "the join back to tbl_article must re-apply status = 'A' and drop the stale FTS hit");
    }

    // =======================================================================
    // FTS5-special-character queries never crash
    // =======================================================================

    [Theory]
    [InlineData("\"")]            // raw double-quote
    [InlineData("*")]             // bare star
    [InlineData("a:b")]           // colon (column filter syntax)
    [InlineData("(sql)")]         // parentheses
    [InlineData("AND OR NOT")]    // FTS5 operators as text
    [InlineData("a*b")]           // star mid-token
    public async Task SearchAsync_Special_Characters_Do_Not_Throw(string query)
    {
        await InsertArticleAsync("Some neutral title");

        Func<Task> act = async () => await _articleRepo.SearchAsync(query);

        // Must never throw a SQL/FTS syntax error: either matches literally-quoted terms or
        // returns no results.
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SearchAsync_Empty_Or_Whitespace_Query_Returns_Empty_Not_Everything()
    {
        await InsertArticleAsync("Alpha");
        await InsertArticleAsync("Beta");

        (await _articleRepo.SearchAsync("")).Should().BeEmpty();
        (await _articleRepo.SearchAsync("   ")).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_Latin_Diacritics_Match_End_To_End()
    {
        // FTS5 unicode61 defaults to remove_diacritics=1, which strips LATIN diacritics; the
        // query-side DefaultTokenizer also strips them. Both sides agree on "cafe" for "café",
        // so a diacritic-free query finds the accented source. Confirmed end-to-end here.
        await InsertArticleAsync("Café menu");

        var results = await _articleRepo.SearchAsync("cafe");
        results.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_Cyrillic_Yo_Mismatch_Is_A_Known_Limitation()
    {
        // FTS5 unicode61 remove_diacritics=1 does NOT touch CYRILLIC marks, so the source token
        // "сёрфинг" stays "сёрфинг" in the index. The query-side stemmer (TextNormalization)
        // strips ё→е, so a query for "сёрфинг" becomes "серфинг"* and does NOT match the indexed
        // "сёрфинг". This is a tokenizer-configuration asymmetry in migration 005 (out of WP-07's
        // scope — we must not touch the migration); it is documented here as a known limitation,
        // not a bug in this wiring. Most modern Russian text uses е in place of ё anyway, and the
        // common case (е-spelled query against е-spelled source, or ё-query against ё-source
        // typed verbatim) is unaffected: the failure is specifically the mixed ё↔е case.
        await InsertArticleAsync("Сёрфинг на побережье");

        // Querying with the ё form: indexed "сёрфинг" vs query stem "серфинг" -> no match.
        (await _articleRepo.SearchAsync("сёрфинг")).Should().BeEmpty();
    }

    // =======================================================================
    // Preserved exact-substring path still works (regression guard)
    // =======================================================================

    [Fact]
    public async Task SearchByExactSubstringAsync_Preserves_Pre_Wp07_Behavior_Title_And_Tag()
    {
        var titleMatch = await InsertArticleAsync("Falcon launch notes");
        var tagOnly = await InsertArticleAsync("Totally different");
        await LinkTagAsync(tagOnly, await InsertTagAsync("falcon-9"));
        await InsertArticleAsync("Unrelated sparrow");

        var results = await _articleRepo.SearchByExactSubstringAsync("falcon");

        results.Select(a => a.Id).Should().BeEquivalentTo([titleMatch, tagOnly]);
    }

    [Fact]
    public async Task SearchByExactSubstringAsync_No_Morphology_Сервера_Does_Not_Find_Сервер()
    {
        // Proves the old path was preserved AS-IS (no morphology leak): "сервера" is not a
        // substring of "сервер", so the exact-substring path returns nothing, while the FTS path
        // returns it. This contrast is exactly what distinguishes the two modes.
        await InsertArticleAsync("Сервер");

        (await _articleRepo.SearchByExactSubstringAsync("сервера")).Should().BeEmpty();
        (await _articleRepo.SearchAsync("сервера")).Should().HaveCount(1);
    }

    // =======================================================================
    // Folder search parity (same FTS wiring)
    // =======================================================================

    [Fact]
    public async Task Folder_SearchAsync_Matches_Name_And_Path_With_Morphology()
    {
        await InsertFolderAsync("/Work/Инфраструктура", "Инфраструктура");
        await InsertFolderAsync("/Work/Runbooks", "Runbooks");

        // Inflected query "серверов" should NOT match "Инфраструктура"; pick a morphology case:
        // "runbook" (singular) should match the "Runbooks" folder via stem prefix "runbook"*.
        var byStem = await _folderRepo.SearchAsync("runbook");
        byStem.Select(f => f.Path).Should().Contain("/Work/Runbooks");

        // Name exact-token match.
        var byName = await _folderRepo.SearchAsync("инфраструктура");
        byName.Select(f => f.Path).Should().Contain("/Work/Инфраструктура");
    }

    [Fact]
    public async Task Folder_SearchAsync_Is_Superset_Of_Substring_Scan_And_Drops_Mid_Word()
    {
        var id = await InsertFolderAsync("/Work/Runbooks", "Runbooks");

        var oldWhole = (await _folderRepo.SearchByExactSubstringAsync("runbook")).Select(f => f.Id);
        var newWhole = (await _folderRepo.SearchAsync("runbook")).Select(f => f.Id);
        oldWhole.Should().BeSubsetOf(newWhole);

        // Mid-word substring "unboo" inside "Runbooks": old matches, new (token-based) does not.
        (await _folderRepo.SearchByExactSubstringAsync("unboo")).Should().ContainSingle(f => f.Id == id);
        (await _folderRepo.SearchAsync("unboo")).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_Title_Match_Ranks_Above_Tag_Only_Match()
    {
        // Two articles: one matches by title only, one by tag only. Title must rank first.
        var titleId = await InsertArticleAsync("runbook special");
        var tagId = await InsertArticleAsync("something else entirely");
        await LinkTagAsync(tagId, await InsertTagAsync("runbook-tag"));

        var results = await _articleRepo.SearchAsync("runbook");

        results.Select(a => a.Id).Should().Contain(titleId).And.Contain(tagId);
        results.FindIndex(a => a.Id == titleId).Should().BeLessThan(
            results.FindIndex(a => a.Id == tagId),
            "title matches (tier 0, bm25-ranked) sort ahead of tag-only matches (tier 1)");
    }

    [Fact]
    public async Task SearchAsync_Underscore_Prefix_Title_Sorts_First_Ahead_Of_Relevance()
    {
        // The underscore-prefix-sorts-first quirk is preserved as the PRIMARY key: a "_"-pinned
        // title sorts above a non-pinned title even when the non-pinned one would otherwise rank
        // higher on relevance.
        var pinned = await InsertArticleAsync("_System runbook");
        var normal = await InsertArticleAsync("runbook runbook runbook"); // denser bm25

        var results = await _articleRepo.SearchAsync("runbook");

        results.Select(a => a.Id).Should().Contain(pinned).And.Contain(normal);
        results.FindIndex(a => a.Id == pinned).Should().BeLessThan(
            results.FindIndex(a => a.Id == normal),
            "underscore-prefixed titles keep sorting first, ahead of bm25 relevance");
    }
}
