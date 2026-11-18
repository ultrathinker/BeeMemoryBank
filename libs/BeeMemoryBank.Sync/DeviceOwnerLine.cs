using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Whose device this is, for the settings of an app (release 2.12, device ownership): <c>shared</c> (the organisation's) or <c>personal</c> with the
/// owner's display label. A node has no whitelist row of its own; what the network says about it is in the whitelist events other nodes wrote about it
/// (<see cref="WhitelistHistory.ResolveSelfOwnershipAsync"/>).
/// </summary>
/// <param name="Ownership"><see cref="OwnershipKinds.Shared"/> or <see cref="OwnershipKinds.Personal"/>.</param>
/// <param name="OwnerLabel">For a personal device, the owner's display label as the network recorded it (never a login).</param>
public sealed record DeviceOwnerLine(string Ownership, string? OwnerLabel)
{
    /// <summary>
    /// The line to show, or null: nothing is known (no event says it, or it says <c>unknown</c>), or the bank has only one person who holds the key -
    /// a one-person bank is never shown whose its devices are. The people are counted from what this node knows: the owners of the personal superadmin
    /// devices on its whitelist and its own owner.
    /// </summary>
    public static async Task<DeviceOwnerLine?> ReadAsync(IDbConnectionFactory db, IWhitelistRepository whitelist, Guid selfNodeId)
    {
        var self = await WhitelistHistory.ResolveSelfOwnershipAsync(db, selfNodeId);
        var kind = OwnershipKinds.NormalizeOrNull(self?.Ownership);
        if (self is null || kind is null or OwnershipKinds.Unknown) return null;
        if (kind == OwnershipKinds.Personal && (self.OwnerPersonId is null || string.IsNullOrWhiteSpace(self.OwnerLabel))) return null;

        var people = (await whitelist.GetAllActiveAsync())
            .Where(r => r.NodeId != selfNodeId && r.IsSuperadmin && r.OwnerPersonId is not null
                        && OwnershipKinds.NormalizeOrNull(r.Ownership) == OwnershipKinds.Personal)
            .Select(r => r.OwnerPersonId!.Value)
            .ToHashSet();
        if (kind == OwnershipKinds.Personal) people.Add(self.OwnerPersonId!.Value);
        if (people.Count < 2) return null;

        return new DeviceOwnerLine(kind, kind == OwnershipKinds.Personal ? Shorten(self.OwnerLabel!.Trim()) : null);
    }

    /// <summary>A label another node wrote is shown, but never longer than a settings row holds.</summary>
    private static string Shorten(string label) => label.Length <= MaxLabel ? label : label[..MaxLabel] + "...";

    private const int MaxLabel = 80;
}
