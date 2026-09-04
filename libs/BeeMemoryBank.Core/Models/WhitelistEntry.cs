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
    /// This table was the last replicated one without it, and the gap was not cosmetic: whitelist
    /// add, revoke and update applied in arrival order, so a stale <c>whitelist_add</c> from a peer
    /// that had been offline during a revoke put the revoked node back into the mesh on arrival,
    /// silently. See migration 021 for the full account.
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
    /// whitelist add/revoke, hard-delete, restore_network. Default false. (Wave 2:
    /// gemini #1 / #2 / #3 — privilege escalation prevention.)
    ///
    /// <para>Set on every node that joins with the master password, since a join grants full
    /// trust. It can be cleared again from Admin → Nodes, which emits a whitelist_update carrying
    /// the new value so the whole mesh agrees; a peer can never RAISE its own flag through that
    /// event, only an existing superadmin peer can promote it.</para>
    /// </summary>
    public bool IsSuperadmin { get; set; }
}
