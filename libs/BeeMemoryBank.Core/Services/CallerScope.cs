using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Core.Services;

public sealed class SystemCallerScope : ICallerScope
{
    public static readonly SystemCallerScope Instance = new();

    public bool IsSuperadmin => true;

    public bool IsAccessDenied(string? treePath) => false;

    public bool IsWriteDenied(string? treePath) => false;

    public bool IsReadOnly(string? treePath) => false;

    public bool IsNavigable(string? treePath) => true;

    public List<Article> FilterArticles(List<Article> articles) => articles;

    public List<Folder> FilterFolders(List<Folder> folders) => folders;

    // All SystemCallerScope callers (and superadmins generally) see the entire vault, so they all
    // share one constant fingerprint. Collapsing every full-access caller onto the same key is safe
    // AND maximizes cache sharing among them.
    public string ReadScopeFingerprint => "sys";

    // No ACL at all -- every row already passes, so no WHERE clause is needed.
    public AclSqlPredicate? BuildReadAclPredicate(string pathExpr, string paramPrefix) => null;

    public AclSqlPredicate? BuildFolderVisibilityPredicate(string pathExpr, string paramPrefix) => null;
}

/// <summary>
/// Fail-closed scope. Returned when an HTTP request reaches repository code before
/// CallerScopeMiddleware has set a proper scope — i.e. "we don't know who you are,
/// so you see nothing." Never pick this scope explicitly; it's a safety net.
/// </summary>
public sealed class DenyAllScope : ICallerScope
{
    public static readonly DenyAllScope Instance = new();

    public bool IsSuperadmin => false;

    public bool IsAccessDenied(string? treePath) => true;

    public bool IsWriteDenied(string? treePath) => true;

    public bool IsReadOnly(string? treePath) => false;

    public bool IsNavigable(string? treePath) => false;

    public List<Article> FilterArticles(List<Article> articles) => [];

    public List<Folder> FilterFolders(List<Folder> folders) => [];

    // Two deny-all callers both see nothing, so they share one constant fingerprint. Sharing is
    // safe because the shared (empty) result is identical for both.
    public string ReadScopeFingerprint => "denyall";

    private static readonly Dictionary<string, object?> NoParameters = new();

    // "1 = 0" -- nothing is ever visible, matching FilterArticles/FilterFolders' unconditional [].
    public AclSqlPredicate? BuildReadAclPredicate(string pathExpr, string paramPrefix) =>
        new("1 = 0", NoParameters);

    public AclSqlPredicate? BuildFolderVisibilityPredicate(string pathExpr, string paramPrefix) =>
        new("1 = 0", NoParameters);
}

public sealed class HttpCallerScope : ICallerScope
{
    private readonly HashSet<string> _denyPaths;
    private readonly HashSet<string> _allowPaths;
    private readonly HashSet<string> _readOnlyPaths;
    private readonly HashSet<string> _ancestors;
    private readonly string _readScopeFingerprint;

    public bool IsSuperadmin { get; }

    // Back-compat overload — read-only paths default to empty.
    public HttpCallerScope(bool isSuperadmin, HashSet<string> denyPaths, HashSet<string> allowPaths)
        : this(isSuperadmin, denyPaths, allowPaths, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
    {
    }

    public HttpCallerScope(
        bool isSuperadmin,
        HashSet<string> denyPaths,
        HashSet<string> allowPaths,
        HashSet<string> readOnlyPaths)
    {
        IsSuperadmin = isSuperadmin;
        _denyPaths = denyPaths;
        _allowPaths = allowPaths;
        _readOnlyPaths = readOnlyPaths;
        _ancestors = allowPaths.Count > 0
            ? FolderAccessService.ComputeAncestors(allowPaths)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _readScopeFingerprint = ComputeReadScopeFingerprint(isSuperadmin, denyPaths, allowPaths);
    }

    public bool IsAccessDenied(string? treePath)
        => IsSuperadmin ? false : FolderAccessService.IsAccessDenied(_denyPaths, _allowPaths, treePath);

    public bool IsWriteDenied(string? treePath)
        => IsSuperadmin ? false : FolderAccessService.IsWriteDenied(_denyPaths, _allowPaths, _readOnlyPaths, treePath);

    public bool IsReadOnly(string? treePath)
        => IsSuperadmin ? false : FolderAccessService.IsReadOnlyForCaller(_readOnlyPaths, treePath);

    public bool IsNavigable(string? treePath)
    {
        if (IsSuperadmin) return true;
        if (string.IsNullOrEmpty(treePath)) return false;
        if (!FolderAccessService.IsAccessDenied(_denyPaths, _allowPaths, treePath)) return true;
        return _ancestors.Contains(treePath);
    }

    public List<Article> FilterArticles(List<Article> articles)
        => IsSuperadmin ? articles : FolderAccessService.FilterArticles(articles, _denyPaths, _allowPaths);

    public List<Folder> FilterFolders(List<Folder> folders)
    {
        if (IsSuperadmin) return folders;
        return folders.Where(f => IsNavigable(f.Path)).ToList();
    }

    // Read visibility is determined entirely by (IsSuperadmin, denyPaths, allowPaths) — readOnlyPaths
    // only gates writes and is intentionally excluded. Two callers whose deny+allow rule sets cover
    // the same paths make byte-identical FilterArticles/FilterFolders decisions, so their results are
    // safe to share. Superadmins see everything, so they collapse onto the same fingerprint as
    // SystemCallerScope. The canonical string is SHA-256-hashed to keep the key bounded regardless of
    // how many ACL paths a caller carries; the hash input is order-independent (sets are sorted) so
    // two scopes built with differently-ordered but equivalent sets hash identically.
    public string ReadScopeFingerprint => _readScopeFingerprint;

    public AclSqlPredicate? BuildReadAclPredicate(string pathExpr, string paramPrefix) =>
        IsSuperadmin ? null : FolderAccessService.BuildReadAclPredicate(_denyPaths, _allowPaths, pathExpr, paramPrefix);

    public AclSqlPredicate? BuildFolderVisibilityPredicate(string pathExpr, string paramPrefix) =>
        IsSuperadmin ? null : FolderAccessService.BuildFolderVisibilityPredicate(_denyPaths, _allowPaths, pathExpr, paramPrefix);

    private static string ComputeReadScopeFingerprint(bool isSuperadmin, HashSet<string> denyPaths, HashSet<string> allowPaths)
    {
        if (isSuperadmin) return "sys";

        var sb = new StringBuilder("acl|");
        AppendSorted(sb, "d:", denyPaths);
        AppendSorted(sb, "a:", allowPaths);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return "acl:" + Convert.ToHexString(hash);
    }

    private static void AppendSorted(StringBuilder sb, string prefix, HashSet<string> paths)
    {
        // Order + content are both canonicalized case-insensitively: ACL access decisions compare
        // paths with OrdinalIgnoreCase, so two paths that differ only in case are the SAME rule and
        // must hash identically. Lowercasing is a safe canonical form here — if ToLowerInvariant(a)
        // == ToLowerInvariant(b) then a.Equals(b, OrdinalIgnoreCase), so this can never merge two
        // paths the access engine keeps distinct (no leak risk), it only collapses case variants
        // that the engine already treats as identical.
        var ordered = paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var path in ordered)
        {
            sb.Append(prefix);
            sb.Append(path.ToLowerInvariant());
            sb.Append(';');
        }
    }
}
