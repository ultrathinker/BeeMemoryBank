namespace BeeMemoryBank.Web.Models;

/// <summary>
/// The restore modes the Admin dialog offers, named as the API names them (<c>SnapshotRestoreModes</c> in
/// BeeMemoryBank.Api; the Web project does not reference it). The API's third mode, which keeps the snapshot's
/// own identity, is deliberately not offered here.
/// </summary>
public static class RestoreModes
{
    /// <summary>This node only: it leaves the network and becomes a new node.</summary>
    public const string Standalone = "standalone";

    /// <summary>The whole network: every trusted node is asked to restore the same snapshot.</summary>
    public const string Network = "network";

    /// <summary>Whether a posted value asks for a restore of this node only: nothing posted is the dialog's default, anything else must say so.</summary>
    public static bool IsStandalone(string? value) =>
        string.IsNullOrEmpty(value) || string.Equals(value.Trim(), Standalone, StringComparison.OrdinalIgnoreCase);
}
