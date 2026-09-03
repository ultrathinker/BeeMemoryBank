using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Covers <see cref="CallerScopeHolder.ElevateToSystem"/> and the rule it exists to enforce.
///
/// Elevating to <see cref="SystemCallerScope"/> turns off folder ACL and the read-only guard for
/// whatever runs next on the same scope store. When that store is HttpContext-backed, "next" means
/// the rest of the HTTP request — so an elevation whose restore is skipped does not fail loudly,
/// it silently hands full-vault access to code that was never authorized for it. Making the
/// restore part of a <c>using</c> is the fix; this file keeps it that way.
/// </summary>
public class ScopeElevationTests
{
    [Fact]
    public void ElevateToSystem_RestoresThePreviousScope_OnDispose()
    {
        var holder = new CallerScopeHolder();
        var original = new HttpCallerScope(
            isSuperadmin: false,
            denyPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Secret" },
            allowPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        holder.Scope = original;

        using (holder.ElevateToSystem())
        {
            holder.Scope.Should().BeSameAs(SystemCallerScope.Instance);
            holder.Scope.IsAccessDenied("/Secret").Should().BeFalse("the whole point of elevating");
        }

        holder.Scope.Should().BeSameAs(original);
        holder.Scope.IsAccessDenied("/Secret").Should().BeTrue();
    }

    [Fact]
    public void ElevateToSystem_RestoresThePreviousScope_WhenTheBodyThrows()
    {
        // The hand-written form this replaces needed a try/finally to survive an exception. A
        // `using` gets that for free — but only if the restore really is in Dispose, so assert it.
        var holder = new CallerScopeHolder();
        var original = new HttpCallerScope(
            isSuperadmin: false,
            denyPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Secret" },
            allowPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        holder.Scope = original;

        var act = () =>
        {
            using (holder.ElevateToSystem())
                throw new InvalidOperationException("boom");
        };

        act.Should().Throw<InvalidOperationException>();
        holder.Scope.Should().BeSameAs(original, "an exception must not leave the caller elevated");
    }

    [Fact]
    public void ElevateToSystem_Nests()
    {
        var holder = new CallerScopeHolder();
        var original = DenyAllScope.Instance;
        holder.Scope = original;

        using (holder.ElevateToSystem())
        {
            using (holder.ElevateToSystem())
                holder.Scope.Should().BeSameAs(SystemCallerScope.Instance);

            holder.Scope.Should().BeSameAs(SystemCallerScope.Instance,
                "the inner block restores to the outer elevation, not past it");
        }

        holder.Scope.Should().BeSameAs(original);
    }

    [Fact]
    public void Dispose_OnADefaultElevation_DoesNotThrow()
    {
        // `default(ScopeElevation)` is constructible whether we like it or not; disposing it must
        // be a no-op rather than a NullReferenceException.
        var act = () => default(CallerScopeHolder.ScopeElevation).Dispose();
        act.Should().NotThrow();
    }

    /// <summary>
    /// The rule, enforced against the source itself: production code elevates through
    /// <see cref="CallerScopeHolder.ElevateToSystem"/>, never by assigning
    /// <c>Scope = SystemCallerScope.Instance</c> by hand. A hand-rolled assignment is exactly the
    /// shape that can lose its restore, and every existing one has been converted — this keeps a
    /// new one from appearing.
    ///
    /// Deliberately allowed: <c>CallerScopeMiddleware</c> ASSIGNS a scope rather than elevating
    /// (it establishes the request's scope, there is nothing to restore), and it never assigns
    /// SystemCallerScope — a superadmin gets an empty-rule HttpCallerScope instead. So the ban can
    /// be absolute for production code without an exception list.
    /// </summary>
    [Fact]
    public void NoProductionCodeAssignsSystemScopeByHand()
    {
        var repoRoot = FindRepoRoot();
        var searchRoots = new[] { "libs", "server", "desktop", "mobile" }
            .Select(d => Path.Combine(repoRoot, d))
            .Where(Directory.Exists);

        // `<anything>.Scope = SystemCallerScope.Instance` — the hand-rolled elevation this replaces.
        var handRolled = new Regex(@"\.Scope\s*=\s*SystemCallerScope\.Instance", RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var root in searchRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Skip build output: obj/bin hold generated copies that would double-report.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                // The helper itself is where the assignment legitimately lives.
                if (Path.GetFileName(file) == "CallerScopeHolder.cs") continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (handRolled.IsMatch(lines[i]))
                        offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "elevation must go through CallerScopeHolder.ElevateToSystem(), whose Dispose restores " +
            "the caller's real scope; a hand-written assignment can lose its restore and leak " +
            "full-vault access to whatever runs next on the same scope store");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate the repository root (no BeeMemoryBank.slnx above the test binary).");
    }
}
