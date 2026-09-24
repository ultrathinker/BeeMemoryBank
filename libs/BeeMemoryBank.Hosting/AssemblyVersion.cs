using System.Reflection;

namespace BeeMemoryBank.Hosting;

/// <summary>
/// The version of a given assembly, read from its compiled-in AssemblyInformationalVersion
/// (set by Directory.Build.props from the repo-root VERSION file — the same source of truth the
/// Api reports via its AppVersion helper). Build metadata appended by the SDK
/// (e.g. "1.0.4+&lt;commit&gt;") is stripped so the value is a clean semver string.
/// </summary>
public static class AssemblyVersion
{
    /// <summary>
    /// Resolves the version of <paramref name="assembly"/>. Falls back to the numeric assembly
    /// version when no informational version was compiled in, and to "unknown" as the last resort
    /// — a neutral placeholder, never a hardcoded release number that would outlive the release
    /// it names.
    /// </summary>
    public static string Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString()
            : informational;
        if (string.IsNullOrWhiteSpace(version))
            return "unknown";

        var plus = version.IndexOf('+');
        return (plus >= 0 ? version[..plus] : version).Trim();
    }
}
