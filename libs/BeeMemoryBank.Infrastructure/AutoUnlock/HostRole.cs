namespace BeeMemoryBank.Infrastructure.AutoUnlock;

/// <summary>What the node is (docs/design/step-b-spec.md 11.0): a desktop application's own node, or a server of any kind.</summary>
public enum HostRole
{
    /// <summary>Every other start - a service, a headless <c>bmbd</c>, a container, a run by hand: the server checkbox with the stricter warnings. The default value: when in doubt the stricter warnings apply.</summary>
    Server = 0,

    /// <summary>Started by a desktop application (<c>BMB_HOST_ROLE=desktop</c> in the child's environment): the OS auto-unlock card.</summary>
    Desktop = 1,
}

public static class HostRoles
{
    public const string EnvironmentVariable = "BMB_HOST_ROLE";

    /// <summary>
    /// <c>desktop</c> (ordinal, case-insensitive; no trimming, so <c>"desktop "</c> is not it) is <see cref="HostRole.Desktop"/>. Anything else, or
    /// nothing, is <see cref="HostRole.Server"/>: when in doubt the stricter warnings apply (spec 11.0, 19.2).
    /// </summary>
    public static HostRole Parse(string? value) =>
        string.Equals(value, "desktop", StringComparison.OrdinalIgnoreCase) ? HostRole.Desktop : HostRole.Server;

    /// <summary>The role of this process, read from the environment.</summary>
    public static HostRole FromEnvironment() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>The wire word of a role in the status answers (<c>desktop</c> or <c>server</c>).</summary>
    public static string Word(this HostRole role) => role == HostRole.Desktop ? "desktop" : "server";
}
