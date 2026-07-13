using System;
using System.Collections.Generic;
using System.IO;

namespace BeeMemoryBank.Profiles;

/// <summary>
/// Represents the root model for the profiles registry JSON file.
/// </summary>
public sealed class ProfilesRegistry
{
    /// <summary>
    /// Gets or sets the schema version of the registry file.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Gets or sets the identifier of the last used profile.
    /// </summary>
    public string? LastUsedProfileId { get; set; }

    /// <summary>
    /// Gets or sets the autostart mode for the registry.
    /// </summary>
    public AutostartMode AutostartMode { get; set; } = AutostartMode.LastUsed;

    /// <summary>
    /// Gets or sets the fixed profile identifier for autostart when mode is FixedProfile.
    /// </summary>
    public string? AutostartProfileId { get; set; }

    /// <summary>
    /// Gets or sets the list of registered profiles.
    /// </summary>
    public List<ProfileEntry> Profiles { get; set; } = new();

    /// <summary>
    /// Validates the registry data against the required schema invariants.
    /// </summary>
    /// <returns>True if the registry is valid; otherwise, false.</returns>
    public bool IsValid()
    {
        if (SchemaVersion < 1)
        {
            return false;
        }

        if (Profiles == null || Profiles.Count == 0)
        {
            return false;
        }

        foreach (var profile in Profiles)
        {
            if (profile == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.DataPath))
            {
                return false;
            }

            if (!Path.IsPathRooted(profile.DataPath))
            {
                return false;
            }
        }

        return true;
    }
}
