namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Applies the tombstones of removed people (<c>tbl_removed_person</c>, release 2.12.0, plan R1) to this node's own accounts: the person's doors,
/// recovery keys, auto-unlock agents and tokens go, and the account is deleted (or, on the node that holds the removal's own record, demoted as that
/// record says). Called when a <c>person_removed</c> event applies, when the database opens (after a restore or a join the tombstones are there before
/// anybody signs in) and after a network restore. Idempotent; an account a removal process on this node still holds is left to that process.
/// </summary>
public interface IRemovedPersonEnforcer
{
    /// <param name="personId">One person, or null for every tombstone this node keeps.</param>
    /// <returns>How many local accounts were changed.</returns>
    Task<int> EnforceAsync(Guid? personId = null, CancellationToken ct = default);
}

/// <summary>Told when a replicated <c>offboarding_state</c> was stored, so a node can lock the person's local row out at once (plan E2).</summary>
public interface IOffboardingReplicaObserver
{
    Task OnReplicatedStateAsync(Guid processId, Guid personId, bool open, CancellationToken ct = default);
}

/// <summary>What one node reported for one step of a removal (plan E4/E6).</summary>
public sealed record OffboardingNodeReport(Guid NodeId, bool Confirmed, string? Waiting = null);

/// <summary>
/// Step S1 of a removal (plan WS-D, E4): the accounts node and every shared full node change their identity key, so a node seed taken in a backup
/// while the person was a superadmin signs nothing any more. Defined here by the removal process, implemented with the
/// re-key service (<c>IdentityRekeyStep</c>): this node re-keys itself, and the shared full nodes are asked with a signed
/// <c>node_rekey_request</c>.
/// </summary>
public interface IIdentityRekeyStep
{
    /// <summary>False while this build cannot re-key at all; the step then waits with <see cref="UnavailableReason"/>.</summary>
    bool IsAvailable { get; }

    /// <summary>A plain sentence for the page when the step cannot run here.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Starts a re-key of THIS node (reason <c>offboarding:&lt;process id&gt;</c>) and asks it of <paramref name="sharedNodes"/>. The removal calls it
    /// on every pass while S1 waits, with the CURRENT shared full nodes (a node that appeared is included).
    /// <para><b>Idempotent per process, not per time</b>: a node that has already re-keyed FOR THIS PROCESS - a re-key whose reason is
    /// <c>offboarding:&lt;<paramref name="processId"/>&gt;</c> - is not re-keyed again. A re-key for any other reason, or for another process, does not
    /// count, even one after <paramref name="notBeforeUtc"/> (the start of S0, given for the record and for logs only). Keyed on time instead, a
    /// re-key that landed between S0 and the moment S1 takes a node's baseline fingerprint would satisfy this call while the baseline already shows
    /// the new key: the fingerprint would never change again and S1 would wait for ever, with no skip and no cancel.</para>
    /// </summary>
    Task StartAsync(Guid processId, IReadOnlyList<Guid> sharedNodes, DateTime notBeforeUtc, CancellationToken ct = default);

    /// <summary>The identity key fingerprint node <paramref name="nodeId"/> has now, as this node knows it (its own key, or the peer's stored key).</summary>
    Task<string?> CurrentKeyFingerprintAsync(Guid nodeId, CancellationToken ct = default);

    /// <summary>
    /// Why node <paramref name="nodeId"/> has not re-keyed for this process, when the step knows better than "not yet": its key is kept outside its
    /// database (it never can: pair it again or revoke it), or its last re-key was refused (devices that cannot follow, a key operation running).
    /// Null when there is nothing particular to say.
    /// </summary>
    Task<string?> WhyWaitingAsync(Guid processId, Guid nodeId, CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>
    /// The active devices other than <paramref name="self"/> that cannot follow a new identity key: a version before it, or not
    /// seen since the update. The removal re-keys without waiting for them and then waits for each until it is updated or revoked. None by default.
    /// </summary>
    Task<IReadOnlyList<Guid>> CannotFollowAsync(Guid self, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
}

/// <summary>
/// Step S2 of a removal (plan WS-C, E4): the shared full nodes get the doors list without the person and drop every local superadmin door. Defined
/// here by the removal process, implemented with the list publisher (<c>SharedDoorsStep</c>). The list goes out only from
/// the accounts node: when that is another node, or none is set, the step waits and says what to set.
/// </summary>
public interface ISharedDoorsStep
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>Publishes the list of superadmins without everyone a removal holds. Returns the list version published (0 when nothing was published).</summary>
    Task<long> PublishWithoutAsync(IReadOnlyCollection<Guid> removedPersonIds, CancellationToken ct = default);

    /// <summary>Whether shared node <paramref name="nodeId"/> reports at least <paramref name="listVersion"/> applied and no local superadmin door.</summary>
    Task<OffboardingNodeReport> ReportAsync(Guid nodeId, long listVersion, CancellationToken ct = default);

    /// <summary>
    /// Why the list cannot go out from this node now, as a plain sentence for the page (this node is not the accounts node, none is set, a shared
    /// node cannot take the list yet, this node is locked), or null when <see cref="PublishWithoutAsync"/> can publish.
    /// </summary>
    Task<string?> WhyNotPublishingAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}
