namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// Produces the recovery-set document (plan §6.8, CONTRACTS §2): the open JSON
/// (<c>bmb-recovery-set-v1</c>) with boxes, chain links, anchors and sealed secrets that lies NEXT
/// TO every restic repository — outside it, so prune/check never touch it — because a restore
/// needs it BEFORE it can open the backup: the repo password is sealed under the DEK, the DEK is
/// inside a box, and the box is inside this file.
///
/// <para>The real builder (<c>RecoverySetBuilder</c>, BMB-53) reads the recovery tables; this
/// interface is the seam it plugs into. The stub registered here returns null — meaning "no
/// recovery material to publish yet" — and the backup writes no recovery-set file, exactly as it
/// will for a node whose tables are empty.</para>
/// </summary>
public interface IRecoverySetSource
{
    /// <summary>The recovery-set JSON, or null when this node has nothing to publish.</summary>
    Task<string?> BuildAsync(CancellationToken ct);
}

/// <summary>Placeholder until BMB-53's RecoverySetBuilder lands; see the interface comment.</summary>
public sealed class StubRecoverySetSource : IRecoverySetSource
{
    public Task<string?> BuildAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}
