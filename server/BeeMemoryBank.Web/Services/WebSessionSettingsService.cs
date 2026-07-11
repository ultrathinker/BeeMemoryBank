namespace BeeMemoryBank.Web.Services;

/// <summary>
/// In-memory mirror of the admin-configurable web login cookie lifetime (source of truth is
/// the API's tbl_node_identity row). Populated lazily by a per-request middleware in Program.cs
/// (same "try once, stick once confirmed" idiom as the init-status check) and refreshed
/// immediately whenever an admin saves new settings via the Admin page.
/// </summary>
/// <remarks>
/// AUDIT NOTE: No locking needed. Same reasoning as InvisibleModeService — a plain mutable
/// singleton read across request scopes is fine here; worst case on a torn read is one request
/// using a slightly stale value, which self-corrects on the very next request.
/// </remarks>
public class WebSessionSettingsService
{
    public int ExpireHours { get; set; } = 48;
    public bool SlidingExpiration { get; set; } = true;
    public bool Loaded { get; set; }
}
