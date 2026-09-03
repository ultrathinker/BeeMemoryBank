namespace BeeMemoryBank.Core.Models;

/// <summary>
/// Coarse phase of a DEK rotation, surfaced to the admin UI as progress.
///
/// <para>
/// In Core, not the API project, because the rotation applier that reports these steps is shared
/// with hosts that have no API layer — a mobile or CLI node applies a peer's rotation with exactly
/// the same code path the server uses.
/// </para>
/// </summary>
public enum DekRotationFlowStep
{
    Idle,
    Proposing,
    AwaitingQuorum,
    Committing,
    SessionsClosing,
    PreRotationBackup,
    ReWrappingPerItem,
    ReEncryptingDirect,
    InvalidatingSlots,
    InvalidatingAgents,
    Finalizing,
    Completed,
    Failed,
    NeedsAdminDecision,
    NeedsNewRecoveryKey
}
