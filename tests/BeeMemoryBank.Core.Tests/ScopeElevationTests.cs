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
    public void DisposingTwice_DoesNotClobberAScopeSetInBetween()
    {
        // A second Dispose must not re-apply the scope this elevation captured. If it did, an
        // elevation disposed twice (a stray extra Dispose, or a struct copy — which is why this
        // is a class) would silently roll a later, unrelated scope change backwards.
        var holder = new CallerScopeHolder();
        var original = DenyAllScope.Instance;
        holder.Scope = original;

        var elevation = holder.ElevateToSystem();
        elevation.Dispose();
        holder.Scope.Should().BeSameAs(original);

        var laterScope = new HttpCallerScope(
            isSuperadmin: false,
            denyPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            allowPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Work" });
        holder.Scope = laterScope;

        elevation.Dispose();

        holder.Scope.Should().BeSameAs(laterScope, "a redundant Dispose must be a no-op");
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

        // Matched against the whole file, not line by line: `Scope =` and the value can sit on
        // separate lines after a reformat, and a line-oriented scan would see neither half.
        // `Singleline` lets `\s` span the newline. The optional namespace prefix covers a fully
        // qualified `BeeMemoryBank.Core.Services.SystemCallerScope.Instance`.
        var handRolled = new Regex(
            @"\.Scope\s*=\s*(?:[\w.]+\.)?SystemCallerScope\.Instance",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // Any assignment of SystemCallerScope.Instance to a local, which could then be assigned
        // to .Scope out of this pattern's sight. There is no legitimate reason to hold the
        // singleton in a variable in production code — it is passed directly where it is needed.
        var launderedViaLocal = new Regex(
            @"=\s*(?:[\w.]+\.)?SystemCallerScope\.Instance\s*;",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        var filesScanned = 0;
        foreach (var root in searchRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Skip build output: obj/bin hold generated copies that would double-report.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                // Three files legitimately name SystemCallerScope.Instance and are not elevations:
                // the helper that performs (and restores) the elevation, and the two
                // ICallerScopeStore implementations, whose reference is the DEFAULT a store starts
                // at before any caller identity is known — a CLI run, a background job, a test.
                // CallerScopeMiddleware overwrites it for every HTTP request, and never with
                // SystemCallerScope (a superadmin gets an empty-rule HttpCallerScope instead).
                if (Path.GetFileName(file) is "CallerScopeHolder.cs"
                    or "InstanceCallerScopeStore.cs"
                    or "HttpContextCallerScopeStore.cs") continue;

                filesScanned++;
                var text = File.ReadAllText(file);
                var relative = Path.GetRelativePath(repoRoot, file);

                foreach (Match m in handRolled.Matches(text))
                    offenders.Add($"{relative} (offset {m.Index}): direct assignment");

                foreach (Match m in launderedViaLocal.Matches(text))
                {
                    // A direct `.Scope = ...Instance;` also matches this second pattern; do not
                    // report the same occurrence twice.
                    if (handRolled.Matches(text).Any(h => h.Index + h.Length == m.Index + m.Length))
                        continue;
                    offenders.Add($"{relative} (offset {m.Index}): SystemCallerScope.Instance held in a local");
                }
            }
        }

        // A scan that walked nothing would make the assertion below vacuously true.
        filesScanned.Should().BeGreaterThan(100,
            "the scan must actually be walking the production source tree");

        offenders.Should().BeEmpty(
            "elevation must go through CallerScopeHolder.ElevateToSystem(), whose Dispose restores " +
            "the caller's real scope; a hand-written assignment can lose its restore and leak " +
            "full-vault access to whatever runs next on the same scope store");
    }

    /// <summary>
    /// The stronger rule after the elevation refactor: production code elevates ONLY through
    /// <see cref="CallerScopeHolder.RunAsSystem"/>/<c>RunAsSystemAsync</c>, whose elevated region is
    /// exactly the delegate body and therefore cannot be widened by a later control-flow change to
    /// cover user-facing work. The raw <see cref="CallerScopeHolder.ElevateToSystem"/> disposable —
    /// the building block those helpers are implemented on — must appear only inside
    /// <c>CallerScopeHolder.cs</c> itself. This pins the invariant so a new ambient
    /// <c>using var _ = holder.ElevateToSystem();</c> cannot reappear at a call site unnoticed.
    /// </summary>
    [Fact]
    public void ProductionCodeElevatesOnlyThroughRunAsSystem()
    {
        var repoRoot = FindRepoRoot();
        var searchRoots = new[] { "libs", "server", "desktop", "mobile" }
            .Select(d => Path.Combine(repoRoot, d))
            .Where(Directory.Exists);

        var directElevation = new Regex(@"\.ElevateToSystem\s*\(", RegexOptions.Compiled);

        var offenders = new List<string>();
        var filesScanned = 0;
        foreach (var root in searchRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                // CallerScopeHolder defines ElevateToSystem and the RunAsSystem(Async) helpers
                // built on it — the one place the raw primitive legitimately appears.
                if (Path.GetFileName(file) == "CallerScopeHolder.cs") continue;

                filesScanned++;
                var text = File.ReadAllText(file);
                var relative = Path.GetRelativePath(repoRoot, file);
                foreach (Match m in directElevation.Matches(text))
                    offenders.Add($"{relative} (offset {m.Index})");
            }
        }

        filesScanned.Should().BeGreaterThan(100,
            "the scan must actually be walking the production source tree");

        offenders.Should().BeEmpty(
            "production code must elevate via CallerScopeHolder.RunAsSystem/RunAsSystemAsync, whose " +
            "elevated region is bounded to the delegate; a raw ElevateToSystem() using-block can be " +
            "widened by a later refactor to cover user-facing work after the intended section");
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
