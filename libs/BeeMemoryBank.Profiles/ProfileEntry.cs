using System;

namespace BeeMemoryBank.Profiles;

/// <summary>
/// Represents a single profile entry in the profiles registry.
/// </summary>
public sealed class ProfileEntry
{
    /// <summary>
    /// Gets or sets the unique short identifier of the profile.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the profile.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute data directory path for the profile.
    /// </summary>
    public string DataPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the creation timestamp (UTC, ISO 8601).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the last used timestamp (UTC, ISO 8601).
    /// </summary>
    public DateTime LastUsedAt { get; set; }
}
