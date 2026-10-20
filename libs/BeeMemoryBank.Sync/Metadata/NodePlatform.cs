namespace BeeMemoryBank.Sync.Metadata;

/// <summary>
/// What this build runs on, told to the peers next to the application version (the sync identity and the authentication carry it) so that the
/// network map can draw a node as a Mac, a Windows PC, a Linux server, a phone or a container. A closed set of names: a peer's word is read through
/// <see cref="Normalize"/>, never shown as it came, and a peer that says nothing (a build before 2.6.4) is simply drawn without a platform.
/// </summary>
public static class NodePlatform
{
    public const string Windows = "windows";
    public const string MacOs = "macos";
    public const string Linux = "linux";
    public const string Android = "android";
    public const string Ios = "ios";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { Windows, MacOs, Linux, Android, Ios };

    /// <summary>The platform of this process: one of the names above, or null for a system this code does not know.</summary>
    public static string? Current { get; } = Detect(OperatingSystem.IsAndroid(), OperatingSystem.IsIOS(),
        OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst(), OperatingSystem.IsWindows(), OperatingSystem.IsLinux());

    /// <summary>This process runs in a container (Docker, Podman, Kubernetes): the .NET images set <c>DOTNET_RUNNING_IN_CONTAINER</c>, and a Docker host leaves <c>/.dockerenv</c>.</summary>
    public static bool InContainer { get; } = DetectContainer(OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable, File.Exists);

    /// <summary>The name for the flags the runtime reports (Android and iOS report themselves as Linux and macOS too, so they are asked first).</summary>
    public static string? Detect(bool android, bool ios, bool macOs, bool windows, bool linux) =>
        android ? Android : ios ? Ios : macOs ? MacOs : windows ? Windows : linux ? Linux : null;

    public static bool DetectContainer(bool linux, Func<string, string?> environment, Func<string, bool> fileExists)
    {
        if (string.Equals(environment("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)) return true;
        return linux && (fileExists("/.dockerenv") || fileExists("/run/.containerenv"));
    }

    /// <summary>A platform name a peer sent, as one of ours (case and spaces do not matter), or null when it is not one: nothing a peer writes reaches a screen as it came.</summary>
    public static string? Normalize(string? declared)
    {
        var name = declared?.Trim().ToLowerInvariant();
        return name is not null && Known.Contains(name) ? name : null;
    }
}
