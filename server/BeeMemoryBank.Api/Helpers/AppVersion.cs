using System.Reflection;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// The version this server was built from, read once from the compiled-in
/// AssemblyInformationalVersion (set by Directory.Build.props from the repo-root VERSION file).
/// Build metadata appended by the SDK (e.g. "1.0.0+&lt;commit&gt;") is stripped so the value
/// is a clean semver string ready for comparison against the update feed.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            info = asm.GetName().Version?.ToString() ?? "0.0.0";

        var plus = info.IndexOf('+');
        return (plus >= 0 ? info[..plus] : info).Trim();
    }
}
