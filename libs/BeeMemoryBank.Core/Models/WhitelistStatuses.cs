namespace BeeMemoryBank.Core.Models;

/// <summary>The values of <see cref="WhitelistEntry.Status"/>.</summary>
public static class WhitelistStatuses
{
    public const string Active = "A";
    public const string Revoked = "R";

    /// <summary>
    /// A row a restore took over but no integrity anchor vouched for: kept, never used — no authentication, no
    /// sync, no event of its applies — until a superadmin confirms it. Unlike <see cref="Revoked"/> it is not an
    /// answer: its events are deferred, not rejected, so they still apply once the row is confirmed.
    /// </summary>
    public const string Unconfirmed = "U";
}
