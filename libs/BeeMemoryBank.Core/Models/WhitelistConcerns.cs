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

    All = Membership | Superadmin | Name | Address | Tls,
}

/// <summary>The single concerns of <see cref="WhitelistConcerns"/>, in a fixed order.</summary>
public static class WhitelistConcernSet
{
    public static readonly WhitelistConcerns[] Each =
    [
        WhitelistConcerns.Membership, WhitelistConcerns.Superadmin, WhitelistConcerns.Name, WhitelistConcerns.Address, WhitelistConcerns.Tls,
    ];
}
