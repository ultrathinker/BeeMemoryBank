namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The product name shown in the web header and the browser tab title. A node stores an
/// override in tbl_node_identity.brand_name; NULL/blank falls back to <see cref="DefaultName"/>,
/// so an untouched node looks exactly like it did before the setting existed.
/// </summary>
public static class Branding
{
    public const string DefaultName = "Bee Memory Bank";

    /// <summary>
    /// Upper bound on a custom name. The header is a single line next to the logo — anything
    /// longer wraps and breaks the layout, so the limit is enforced at the API, not just in the
    /// admin form.
    /// </summary>
    public const int MaxNameLength = 40;

    /// <summary>Blank (or whitespace-only) stored values mean "not set" and resolve to the default.</summary>
    public static string Resolve(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? DefaultName : stored.Trim();
}
