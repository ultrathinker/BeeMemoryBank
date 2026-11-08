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

    /// <summary>
    /// This row's version as one value, for handing to <see cref="RowVersion"/>-shaped APIs: since migration 045 the newest of the
    /// versions of its concerns (<see cref="VersionOf"/>), so a join can raise its clock past it. No whitelist event is judged against it any
    /// more; each is judged per concern.
    /// </summary>
    public RowVersion Version => RowVersion.Of(LamportTs, SourceNodeId);

    // The version of each concern (migration 045): the newest whitelist event that wrote it, last-writer-wins per concern. A revoke writes
    // membership only, so an update that never saw it cannot undo it; two updates that change different fields both stand. Zero and null:
    // nothing versioned wrote it (a tombstone's fields, a row a join from a 2.7.5 host wrote, a row from before versioning).
    public long MemberLamportTs { get; set; }
    public Guid? MemberSourceNodeId { get; set; }
    public long SuperadminLamportTs { get; set; }
    public Guid? SuperadminSourceNodeId { get; set; }
    public long NameLamportTs { get; set; }
    public Guid? NameSourceNodeId { get; set; }
    public long AddressLamportTs { get; set; }
    public Guid? AddressSourceNodeId { get; set; }
    public long TlsLamportTs { get; set; }
    public Guid? TlsSourceNodeId { get; set; }

    /// <summary>The version of one concern (a single flag of <see cref="WhitelistConcerns"/>).</summary>
    public RowVersion VersionOf(WhitelistConcerns concern) => concern switch
    {
        WhitelistConcerns.Membership => RowVersion.Of(MemberLamportTs, MemberSourceNodeId),
        WhitelistConcerns.Superadmin => RowVersion.Of(SuperadminLamportTs, SuperadminSourceNodeId),
        WhitelistConcerns.Name => RowVersion.Of(NameLamportTs, NameSourceNodeId),
        WhitelistConcerns.Address => RowVersion.Of(AddressLamportTs, AddressSourceNodeId),
        WhitelistConcerns.Tls => RowVersion.Of(TlsLamportTs, TlsSourceNodeId),
        _ => throw new ArgumentOutOfRangeException(nameof(concern), concern, "one concern at a time"),
    };

    /// <summary>
    /// The fields whose writer this node cannot name (migration 045; never membership), kept at the version they have: a value a join from a
    /// 2.7.5 host, the host's own row on a joiner, a restore, a blind package, a backup or an older build wrote. Their version stays the real one,
    /// so an update is compared with it exactly as before; but a <c>whitelist_add</c> that is not a re-add (newer than a revoke of the peer that is
    /// newer than the value) does not change such a field: what the source said cannot be dated against it, as an older build never let an add
    /// change an active row. An event that writes the field dates it (<see cref="SetVersionOf"/>).
    /// </summary>
    public WhitelistConcerns UndatedFields { get; set; }

    /// <summary>Whether a field's value has no known writer (<see cref="UndatedFields"/>). Never membership.</summary>
    public bool IsUndated(WhitelistConcerns concern) => concern != WhitelistConcerns.Membership && (UndatedFields & concern) == concern;

    /// <summary>Marks <paramref name="concerns"/> undated (fields only), keeping their versions.</summary>
    public void MarkUndated(WhitelistConcerns concerns) => UndatedFields |= concerns & Fields;

    /// <summary>The fields of a row, without membership.</summary>
    public const WhitelistConcerns Fields = WhitelistConcerns.All & ~WhitelistConcerns.Membership;

    /// <summary>
    /// Sets the version of every concern in <paramref name="concerns"/> to <paramref name="version"/>, and dates them: the version comes from an
    /// event (or a decision of this node) that wrote them. The row's own version is the caller's: the applier keeps it at the newest concern, and
    /// a local write uses <see cref="StampLocal"/>.
    /// </summary>
    public void SetVersionOf(WhitelistConcerns concerns, RowVersion version)
    {
        UndatedFields &= ~concerns;
        var source = SourceOf(version);
        if (concerns.HasFlag(WhitelistConcerns.Membership)) (MemberLamportTs, MemberSourceNodeId) = (version.LamportTs, source);
        if (concerns.HasFlag(WhitelistConcerns.Superadmin)) (SuperadminLamportTs, SuperadminSourceNodeId) = (version.LamportTs, source);
        if (concerns.HasFlag(WhitelistConcerns.Name)) (NameLamportTs, NameSourceNodeId) = (version.LamportTs, source);
        if (concerns.HasFlag(WhitelistConcerns.Address)) (AddressLamportTs, AddressSourceNodeId) = (version.LamportTs, source);
        if (concerns.HasFlag(WhitelistConcerns.Tls)) (TlsLamportTs, TlsSourceNodeId) = (version.LamportTs, source);
    }

    /// <summary>Sets the row's own version (the applier: the newest of its concerns).</summary>
    public void SetRowVersion(RowVersion version)
    {
        LamportTs = version.LamportTs;
        SourceNodeId = SourceOf(version);
    }

    // No node id (Guid.Empty, the convention RowVersion.Of reads a null as) is stored as null, as before versions per concern.
    private static Guid? SourceOf(RowVersion version) => version.SourceNodeId == Guid.Empty ? null : version.SourceNodeId;

    /// <summary>
    /// A write made on THIS node with the version its event just minted: the concerns that event carries take it, and so does the row. Only
    /// for a fresh local version, which is newer than every version on the row (the clock is past every event this node has applied).
    /// </summary>
    public void StampLocal(RowVersion version, WhitelistConcerns concerns)
    {
        // An event that carries no concern is taken by no node: nothing here takes its version either.
        if (concerns == WhitelistConcerns.None) return;
        SetVersionOf(concerns, version);
        SetRowVersion(version);
    }


    /// <summary>
    /// The row written whole by a decision of this node at one known version (a seed's grant): every concern and the row take
    /// <paramref name="version"/>. A row copied from a source with one version for all of it sets the row's version alone instead
    /// (<see cref="NormalizeVersionsForWrite"/>: its fields are undated).
    /// </summary>
    public void SetAllVersions(RowVersion version)
    {
        SetVersionOf(WhitelistConcerns.All, version);
        SetRowVersion(version);
    }

    /// <summary>
    /// Before a write: a row whose own version no concern carries was given a version by a writer that sets <see cref="LamportTs"/> alone (a row
    /// built from a source with one version for all of it: a blind manifest, a backup, a recovery restore; or a writer that does not know of
    /// concerns). Every concern takes that version, as every row was compared before migration 045, and the fields are undated
    /// (<see cref="UndatedFields"/>): an update is judged against the version as before, and an add that is not a re-add does not change them.
    /// A field with no version at all on a row that is not revoked is undated too. A write of an event this node made stamps the concerns it carries (<see cref="StampLocal"/>),
    /// and the applier each concern it wins.
    /// </summary>
    public void NormalizeVersionsForWrite()
    {
        var row = Version;
        if (!WhitelistConcernSet.Each.Any(concern => VersionOf(concern) == row))
        {
            // Written whole at one version: every concern takes it, and the fields are undated, since their writer is not known here.
            SetVersionOf(WhitelistConcerns.All, row);
            MarkUndated(Fields);
        }
        // A row that is not revoked (active, or kept unconfirmed by a restore) whose field has no version at all (a join from a 2.7.5 host, the
        // host's own row on a joiner, a setup, a restore of such a row): the value came from somewhere, and nothing dates it. A revoked row's
        // unversioned field (a tombstone's) has no value yet: the first event fills it.
        if (Status != WhitelistStatuses.Revoked)
            foreach (var concern in WhitelistConcernSet.Each)
                if (concern != WhitelistConcerns.Membership && VersionOf(concern) == new RowVersion(0, Guid.Empty))
                    MarkUndated(concern);
    }

    /// <summary>
    /// True if this peer is authorized to issue cluster-state-modifying sync events:
    /// whitelist add/revoke, hard-delete, restore_network. Default false, so a
    /// peer cannot escalate its own privileges.
    ///
    /// <para>Set on every node that joins with the master password, since a join grants full
    /// trust. It can be cleared again from Admin → Trusted Nodes, which emits a whitelist_update carrying
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
    /// The capabilities (comma-separated, e.g. <c>metadata-envelope-v1</c>) and application version this peer last showed
    /// THIS node, and when (migration 037, docs/design/step-a-spec.md 5.2). A local observation, never replicated; the
    /// readiness report of the metadata activation reads it. An empty list seen is a peer that has none (a 2.5 node).
    /// </summary>
    public string? LastCapabilities { get; set; }

    /// <inheritdoc cref="LastCapabilities"/>
    public string? LastAppVersion { get; set; }

    /// <inheritdoc cref="LastCapabilities"/>
    public DateTime? LastCapabilitiesSeenAt { get; set; }

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
