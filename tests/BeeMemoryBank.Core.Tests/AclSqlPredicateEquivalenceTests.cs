using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Cross-checks <see cref="FolderAccessService.BuildReadAclPredicate"/> and
/// <see cref="FolderAccessService.BuildFolderVisibilityPredicate"/> -- the SQL translation of the
/// ACL deny/allow prefix rules that <c>ArticleRepository.ListAsync</c> and
/// <c>FolderRepository.GetAllActiveAsync</c>/<c>GetChildrenAsync</c> now push into their WHERE
/// clauses -- against the pre-existing, already-trusted in-memory reference
/// (<see cref="FolderAccessService.IsAccessDenied"/> and <see cref="HttpCallerScope.IsNavigable"/>)
/// they are meant to replace.
///
/// <para>
/// Runs the GENERATED SQL against a real SQLite connection over a synthetic set of candidate
/// paths -- including paths that contain literal '%' and '_' (SQLite LIKE metacharacters) plus
/// decoy paths that would wrongly match if those characters were treated as wildcards instead of
/// literal text -- and asserts, for every candidate, that SQL agrees with the C# reference. A
/// disagreement here means the SQL predicate would leak a row the caller cannot see, or hide one
/// they can: exactly the class of bug this ACL-to-SQL push must never introduce.
/// </para>
/// </summary>
public class AclSqlPredicateEquivalenceTests : IDisposable
{
    private readonly DbConnectionFactory _factory = DbConnectionFactory.CreateInMemory($"acl_sql_{Guid.NewGuid():N}");

    public AclSqlPredicateEquivalenceTests()
    {
        using var conn = _factory.CreateConnection();
        conn.Execute("CREATE TABLE test_paths (path TEXT NOT NULL)");
        foreach (var p in CandidatePaths)
            conn.Execute("INSERT INTO test_paths (path) VALUES (@p)", new { p });
    }

    public void Dispose() => _factory.Dispose();

    private static HashSet<string> Set(params string[] paths) => new(paths, StringComparer.OrdinalIgnoreCase);

    // Deliberately includes: root, exact matches, true descendants, siblings whose name
    // textually starts with the same characters ("/Work" vs "/Workshop", "/Work/Project1" vs
    // "/Work/Project12"/"/Work/Project1Other"), a case-variant duplicate and a case-variant
    // descendant, and -- the point of the LIKE-escaping coverage -- folder names containing a
    // literal '%' or '_' alongside decoys that would wrongly match a DIFFERENT, unrelated folder
    // name if that character were treated as a SQL wildcard instead of literal text.
    private static readonly string[] CandidatePaths =
    [
        "/",
        "/Work",
        "/Work/Project1",
        "/Work/Project1/Sub",
        "/Work/Project12",
        "/Work/Project1Other",
        "/Workshop",
        "/Personal",
        "/WORK",              // case-variant duplicate of "/Work"
        "/work/Sub",           // case-variant descendant of "/Work"
        "/Te%st",              // folder name containing a literal '%'
        "/Te%st/Child",        // genuine descendant of "/Te%st"
        "/TeANYst/Child",      // decoy: unrelated folder -- would match "/Te%st/%" if '%' were a real wildcard
        "/Te_st",              // folder name containing a literal '_'
        "/Te_st/Child",        // genuine descendant of "/Te_st"
        "/TeXst/Child",        // decoy: unrelated folder -- would match "/Te_st/%" if '_' were a real wildcard
    ];

    private List<string> RunPredicate(AclSqlPredicate? predicate)
    {
        using var conn = _factory.CreateConnection();
        var sql = predicate == null
            ? "SELECT path FROM test_paths"
            : $"SELECT path FROM test_paths WHERE {predicate.Sql}";
        var parameters = new DynamicParameters();
        if (predicate != null)
            foreach (var (key, value) in predicate.Parameters)
                parameters.Add(key, value);
        return conn.Query<string>(sql, parameters).ToList();
    }

    public static IEnumerable<object[]> Scenarios()
    {
        yield return [Array.Empty<string>(), Array.Empty<string>()];                 // no rules at all
        yield return [new[] { "/Work/Project1" }, Array.Empty<string>()];            // deny-only
        yield return [Array.Empty<string>(), new[] { "/Work/Project1" }];            // allow-only
        yield return [new[] { "/Personal" }, new[] { "/Work" }];                     // mixed, disjoint
        yield return [new[] { "/Work/Project1" }, new[] { "/Work/Project1" }];       // deny wins over identical allow
        yield return [new[] { "/" }, Array.Empty<string>()];                         // deny-all via "/"
        yield return [Array.Empty<string>(), new[] { "/" }];                         // allow-all via "/"
        yield return [new[] { "/Te%st" }, Array.Empty<string>()];                    // deny path with literal '%'
        yield return [Array.Empty<string>(), new[] { "/Te_st" }];                    // allow path with literal '_'
        yield return [Array.Empty<string>(), new[] { "/Work/Project1/Sub" }];        // deep allow -> ancestor stubs
        yield return [new[] { "/" }, new[] { "/Work/Project1/Sub" }];                // deny-all root beats even a deep allow
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void ReadPredicate_AgreesWithIsAccessDenied_ForEveryCandidatePath(string[] denyArr, string[] allowArr)
    {
        var deny = Set(denyArr);
        var allow = Set(allowArr);

        var predicate = FolderAccessService.BuildReadAclPredicate(deny, allow, "path", "acl");
        var sqlVisible = RunPredicate(predicate).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedVisible = CandidatePaths
            .Where(p => !FolderAccessService.IsAccessDenied(deny, allow, p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        sqlVisible.Should().BeEquivalentTo(expectedVisible);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void FolderVisibilityPredicate_AgreesWithIsNavigable_ForEveryCandidatePath(string[] denyArr, string[] allowArr)
    {
        var deny = Set(denyArr);
        var allow = Set(allowArr);
        var scope = new HttpCallerScope(false, deny, allow);

        var predicate = FolderAccessService.BuildFolderVisibilityPredicate(deny, allow, "path", "acl");
        var sqlVisible = RunPredicate(predicate).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedVisible = CandidatePaths
            .Where(scope.IsNavigable)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        sqlVisible.Should().BeEquivalentTo(expectedVisible);
    }

    [Fact]
    public void ReadPredicate_Superadmin_SkipsFilterEntirely()
    {
        // SystemCallerScope/superadmin never even builds a predicate (null = "no restriction") --
        // verified directly against the two scope implementations that must guarantee it.
        SystemCallerScope.Instance.BuildReadAclPredicate("path", "acl").Should().BeNull();
        SystemCallerScope.Instance.BuildFolderVisibilityPredicate("path", "acl").Should().BeNull();

        var superadminHttpScope = new HttpCallerScope(true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Personal" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Work" });
        superadminHttpScope.BuildReadAclPredicate("path", "acl").Should().BeNull();
        superadminHttpScope.BuildFolderVisibilityPredicate("path", "acl").Should().BeNull();
    }

    [Fact]
    public void DenyAllScope_BuildsUnconditionalFalsePredicate()
    {
        var readPredicate = DenyAllScope.Instance.BuildReadAclPredicate("path", "acl");
        var folderPredicate = DenyAllScope.Instance.BuildFolderVisibilityPredicate("path", "acl");

        RunPredicate(readPredicate).Should().BeEmpty();
        RunPredicate(folderPredicate).Should().BeEmpty();
    }
}
