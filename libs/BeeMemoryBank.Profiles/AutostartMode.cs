namespace BeeMemoryBank.Profiles;

/// <summary>
/// Defines the autostart modes for the profile registry.
/// </summary>
public enum AutostartMode
{
    /// <summary>
    /// Autostart the last used profile.
    /// </summary>
    LastUsed,

    /// <summary>
    /// Autostart a specific, fixed profile.
    /// </summary>
    FixedProfile
}
