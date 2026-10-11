using System.Reflection;

namespace BeeMemoryBank.Sync.Metadata;

/// <summary>
/// The application version this build advertises next to its capabilities (docs/design/step-a-spec.md 5.1): the
/// AssemblyInformationalVersion every assembly gets from the repo-root VERSION file (Directory.Build.props), without the
/// SDK's "+commit" suffix. The same value as the Api's AppVersion, readable where the Api is not referenced.
/// </summary>
public static class BuildVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(BuildVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            info = asm.GetName().Version?.ToString() ?? "0.0.0";
        var plus = info.IndexOf('+');
        return (plus >= 0 ? info[..plus] : info).Trim();
    }
}
