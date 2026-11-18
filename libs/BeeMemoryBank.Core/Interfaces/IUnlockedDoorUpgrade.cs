namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// The self-upgrade of a machine door (docs/adr/0012-sealed-doors.md): the OS and server auto-unlock services seal their v1 slot again as a v2
/// door as soon as the node is open and their secret is readable. Run by the session's post-unlock catch-up, after a pending rotation has been
/// applied (the door is sealed to the DEK the session holds). Best effort: an implementation logs and never throws for an ordinary failure.
/// </summary>
public interface IUnlockedDoorUpgrade
{
    Task UpgradeAsync(CancellationToken ct = default);
}
