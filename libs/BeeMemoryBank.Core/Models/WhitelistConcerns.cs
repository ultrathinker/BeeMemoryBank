namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The parts of a peer's whitelist row that whitelist events change independently, each with its own version (migration 045). A
/// <c>whitelist_add</c> writes all five, a <c>whitelist_revoke</c> membership only, a <c>whitelist_update</c> the fields it names. Each
/// concern is last-writer-wins by its own version, so the order the events arrive in does not decide what a node ends with.
/// </summary>
[Flags]
public enum WhitelistConcerns
{
    None = 0,

    /// <summary>Whether the peer is a member: active or revoked (<c>status</c>).</summary>
    Membership = 1,

    /// <summary><c>is_superadmin</c>.</summary>
    Superadmin = 2,

    /// <summary><c>display_name</c>.</summary>
    Name = 4,

    /// <summary><c>api_address</c>.</summary>
    Address = 8,

    /// <summary><c>tls_spki</c> and <c>tls_trust</c>, one value: the mode is the authority over the pin.</summary>
    Tls = 16,

    /// <summary><c>ownership</c>, <c>owner_person_id</c> and <c>owner_label</c>, one value: whether this device is shared, personal to one person, or unknown.</summary>
    Ownership = 32,

    All = Membership | Superadmin | Name | Address | Tls | Ownership,
}

/// <summary>The single concerns of <see cref="WhitelistConcerns"/>, in a fixed order.</summary>
public static class WhitelistConcernSet
{
    public static readonly WhitelistConcerns[] Each =
    [
        WhitelistConcerns.Membership, WhitelistConcerns.Superadmin, WhitelistConcerns.Name, WhitelistConcerns.Address, WhitelistConcerns.Tls,
        WhitelistConcerns.Ownership,
    ];
}

/// <summary>Valid values of <c>tbl_whitelist.ownership</c> and <c>tbl_blind_invite.ownership</c>.</summary>
public static class OwnershipKinds
{
    public const string Shared = "shared";
    public const string Personal = "personal";
    public const string Unknown = "unknown";

    /// <summary>Returns canonical lowercase kind (<c>shared</c>, <c>personal</c>, <c>unknown</c>), or null when unrecognized.</summary>
    public static string? NormalizeOrNull(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        return kind.Trim().ToLowerInvariant() switch
        {
            Shared => Shared,
            Personal => Personal,
            Unknown => Unknown,
            _ => null,
        };
    }

    public static string Normalize(string? kind) => NormalizeOrNull(kind) ?? Unknown;

    public static bool IsKnown(string? kind) => NormalizeOrNull(kind) is Shared or Personal;

    /// <summary>
    /// Enforces the invariant that <see cref="Personal"/> ownership always carries a non-empty <see cref="Guid"/> owner.
    /// Any <see cref="Personal"/> input with a missing or empty <paramref name="ownerPersonId"/> falls back to
    /// <see cref="Unknown"/> with null owner fields.
    /// </summary>
    public static (string Ownership, Guid? OwnerPersonId, string? OwnerLabel) Resolve(
        string? kind, Guid? ownerPersonId, string? ownerLabel)
    {
        var normalized = NormalizeOrNull(kind);
        if (normalized == Personal)
        {
            if (ownerPersonId is not { } pid || pid == Guid.Empty)
                return (Unknown, null, null);
            return (Personal, pid, string.IsNullOrWhiteSpace(ownerLabel) ? null : ownerLabel.Trim());
        }
        if (normalized == Shared)
            return (Shared, null, null);
        return (Unknown, null, null);
    }
}
