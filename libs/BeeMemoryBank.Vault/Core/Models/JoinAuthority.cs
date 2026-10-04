namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The authority (<see cref="WhitelistEntry.IsSuperadmin"/>) that each side of a master-password join
/// records for the other — one rule for all four join paths (the host's <c>/api/join</c>, the Web
/// <c>/api/init/join</c>, <c>bmb join</c> and the phone's setup), so they cannot drift apart (BMB-42).
///
/// <para>Owner's decision: whoever knows the master password is a superadmin. The master password
/// already hands over the DEK, which is strictly more than anything a superadmin-only event can do,
/// so withholding the flag protected nothing — it only made such a node apply its own hard delete or
/// password-change notice locally while every peer refused it, and the mesh diverged for good.</para>
///
/// <para>A blind node never gets the flag, whatever a row or a peer claims: it holds no DEK, proves
/// nothing about the password, and must not be able to steer cluster state (plan section 3.2).</para>
/// </summary>
public static class JoinAuthority
{
    /// <summary>
    /// Authority for a node whose side of the join proved the master password: the joiner as the host
    /// records it, and the host as the joiner records it.
    /// </summary>
    public static bool ForPasswordPeer(Guid nodeId) => !BlindNodeId.IsBlind(nodeId);

    /// <summary>
    /// Authority for a peer the joiner only learns about from the host's whitelist: whatever the host
    /// reports, never more — and never for a blind node.
    /// </summary>
    public static bool ForInheritedPeer(Guid nodeId, bool reportedByHost) =>
        reportedByHost && !BlindNodeId.IsBlind(nodeId);
}
