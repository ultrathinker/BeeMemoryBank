namespace BeeMemoryBank.Core.Models;

public class WhitelistEntry
{
    public Guid NodeId { get; set; }
    public string DisplayName { get; set; } = "";
    public byte[] Ed25519PublicKey { get; set; } = [];
    public string? ApiAddress { get; set; }
    public bool CanGenerateEmbeddings { get; set; }
    public string Status { get; set; } = "A";
    public bool AutoAcceptRestore { get; set; }
    public bool AutoAcceptDekRotation { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// The version of the write that produced this row, in the same (Lamport, node) shape every
    /// other replicated row carries — read together as a <see cref="RowVersion"/>.
    ///
    /// <para>
    /// Without it, whitelist add, revoke and update apply in arrival order, so a stale
    /// <c>whitelist_add</c> from a peer that was offline during a revoke silently puts the revoked
    /// node back into the mesh. See migration 021.
    /// </para>
    ///
    /// <para>
    /// Zero and null mean "written before this column existed" — <see cref="RowVersion.Of"/> reads
    /// the null node id as <see cref="Guid.Empty"/>, which sorts below every real one, so such a row
    /// loses to any attributed write.
    /// </para>
    /// </summary>
    public long LamportTs { get; set; }

    /// <inheritdoc cref="LamportTs"/>
    public Guid? SourceNodeId { get; set; }

    /// <summary>This row's version as one value, for handing to <see cref="RowVersion"/>-shaped APIs.</summary>
    public RowVersion Version => RowVersion.Of(LamportTs, SourceNodeId);

    /// <summary>
    /// True if this peer is authorized to issue cluster-state-modifying sync events:
    /// whitelist add/revoke, hard-delete, restore_network. Default false, so a
    /// peer cannot escalate its own privileges.
    ///
    /// <para>Set on every node that joins with the master password, since a join grants full
    /// trust. It can be cleared again from Admin → Nodes, which emits a whitelist_update carrying
    /// the new value so the whole mesh agrees; a peer can never RAISE its own flag through that
    /// event, only an existing superadmin peer can promote it.</para>
    /// </summary>
    public bool IsSuperadmin { get; set; }

    /// <summary>
    /// The sync protocol this peer last declared to THIS node, and when (migration 028). A local
    /// observation, never replicated. Null = not heard from since this build — a peer on an older
    /// build declares nothing. The PC reads it before adding a blind node (plan 3.1).
    /// </summary>
    public int? LastProtocolVersion { get; set; }

    /// <inheritdoc cref="LastProtocolVersion"/>
    public DateTime? LastProtocolSeenAt { get; set; }

    /// <summary>
    /// base64url(SHA-256(SubjectPublicKeyInfo)) of the TLS key this peer serves HTTPS with, when that
    /// certificate is self-signed — a blind node (plan 4.4, migration 029). Replicated with the row.
    /// Every sync connection to <see cref="ApiAddress"/> must present exactly this key. Null = no
    /// pin, ordinary certificate validation.
    /// </summary>
    public string? TlsSpki { get; set; }

    /// <summary>
    /// How blind copies trust this node's TLS endpoint (<see cref="BlindTrust"/>, migration 036, ADR 0007):
    /// <c>pin</c> (with <see cref="TlsSpki"/>), <c>public-ca</c> (no pin: the system's chain decides) or null =
    /// not set. Replicated with the row. A row with a pin and no mode — written by an older build — stands as
    /// <c>pin</c> (<see cref="EffectiveTlsTrust"/>).
    /// </summary>
    public string? TlsTrust { get; set; }

    /// <summary>The mode this row stands in: <see cref="TlsTrust"/>, or <c>pin</c> for a pin without one, or null.</summary>
    public string? EffectiveTlsTrust => BlindTrust.Effective(TlsTrust, TlsSpki);

    /// <summary>The pin this row holds as far as it counts: only a row in <c>pin</c> mode has one (<see cref="BlindTrust.PinOf"/>).</summary>
    public string? EffectivePin => BlindTrust.PinOf(TlsTrust, TlsSpki);
}
