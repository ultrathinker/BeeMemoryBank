using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Correctness tests for <see cref="ICallerScope.ReadScopeFingerprint"/> — the ACL digest that
/// makes the WP-17 query cache ACL-safe. The cache's entire safety argument rests on these
/// properties: scopes that make identical read-visibility decisions share a fingerprint, and
/// scopes that could see different rows never collide.
/// </summary>
public class CallerScopeFingerprintTests
{
    private static HashSet<string> Set(params string[] paths)
        => new(paths, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SystemCallerScope_HasConstantSysFingerprint()
    {
        SystemCallerScope.Instance.ReadScopeFingerprint.Should().Be("sys");
    }

    [Fact]
    public void DenyAllScope_HasConstantDenyAllFingerprint()
    {
        DenyAllScope.Instance.ReadScopeFingerprint.Should().Be("denyall");
    }

    [Fact]
    public void HttpCallerScope_Superadmin_CollapsesToSysFingerprint()
    {
        // A superadmin HttpCallerScope sees everything, exactly like SystemCallerScope, so it must
        // share the same fingerprint (safe to share results, and maximizes cache hits among admins).
        var admin = new HttpCallerScope(isSuperadmin: true, denyPaths: Set(), allowPaths: Set());
        admin.ReadScopeFingerprint.Should().Be("sys");
    }

    [Fact]
    public void EquivalentAclSets_ProduceSameFingerprint_RegardlessOfOrderOrCase()
    {
        var a = new HttpCallerScope(false, denyPaths: Set("/Work/Secret"), allowPaths: Set("/Public", "/Work"));
        // Same paths, different insertion order and different element case — same effective read ACL.
        var b = new HttpCallerScope(false, denyPaths: Set("/work/SECRET"), allowPaths: Set("/WORK", "/public"));

        a.ReadScopeFingerprint.Should().Be(b.ReadScopeFingerprint,
            "two scopes whose deny+allow sets cover the same paths (modulo order and case) must collapse to one key");
    }

    [Fact]
    public void DifferentAllowSets_ProduceDifferentFingerprints()
    {
        var publicOnly = new HttpCallerScope(false, denyPaths: Set(), allowPaths: Set("/Public"));
        var secretOnly = new HttpCallerScope(false, denyPaths: Set(), allowPaths: Set("/Secret"));

        publicOnly.ReadScopeFingerprint.Should().NotBe(secretOnly.ReadScopeFingerprint,
            "scopes with different visible subtrees must never collide — this is the property that prevents a result cached by one from leaking to the other");
    }

    [Fact]
    public void ReadOnlyFlag_DoesNotAffectReadFingerprint()
    {
        // Read-only paths gate writes, not reads, so they must NOT change the read-visibility
        // fingerprint — two callers with identical deny+allow but different read-only flags see
        // the exact same search results and may safely share a cached entry.
        var ro = new HttpCallerScope(false, Set(), Set("/Public"), Set("/Public"));
        var rw = new HttpCallerScope(false, Set(), Set("/Public"), Set());

        ro.ReadScopeFingerprint.Should().Be(rw.ReadScopeFingerprint,
            "read-only paths affect write-denial only and must not fragment the read-result cache");
    }

    [Fact]
    public void DenyChange_AltersFingerprint()
    {
        // Adding a deny rule narrows the visible set, so the fingerprint must change.
        var before = new HttpCallerScope(false, denyPaths: Set(), allowPaths: Set("/Public", "/Secret"));
        var after = new HttpCallerScope(false, denyPaths: Set("/Secret"), allowPaths: Set("/Public", "/Secret"));

        before.ReadScopeFingerprint.Should().NotBe(after.ReadScopeFingerprint,
            "a deny rule changes what the caller can see and must produce a different key");
    }
}
