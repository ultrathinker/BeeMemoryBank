using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Night-7: SyncFailureClassifier is the single place an apply-failure exception is sorted into
/// Permanent vs Deferred (see its own remarks for why this must not be re-derived at each call
/// site). These tests pin down every exception type EventApplier/SyncClient actually throw for
/// this decision — including the DIFFERENT UnauthorizedAccessException uses in EventApplier
/// ("not in whitelist" and "not superadmin yet" vs "revoked" and "blind author"), which is exactly
/// the split a naive "classify by BCL exception type" rule would get wrong.
/// </summary>
public class SyncFailureClassifierTests
{
    [Fact]
    public void BlobMissingException_IsDeferred() =>
        SyncFailureClassifier.Classify(new BlobMissingException("deadbeef"))
            .Should().Be(SyncFailureKind.Deferred);

    [Fact]
    public void OriginatorNotWhitelistedException_IsDeferred() =>
        SyncFailureClassifier.Classify(new OriginatorNotWhitelistedException(Guid.NewGuid()))
            .Should().Be(SyncFailureKind.Deferred);

    [Fact]
    public void DekRotationPredecessorMissingException_IsDeferred() =>
        SyncFailureClassifier.Classify(new DekRotationPredecessorMissingException(Guid.NewGuid().ToString()))
            .Should().Be(SyncFailureKind.Deferred);

    [Fact]
    public void InvalidDataException_BadSignatureOrMalformedPayload_IsPermanent() =>
        SyncFailureClassifier.Classify(new InvalidDataException("Invalid Ed25519 signature"))
            .Should().Be(SyncFailureKind.Permanent);

    /// <summary>
    /// Plan 4.2: the PC's promotion (from the hub) and the PC's own superadmin-only event (adding a
    /// blind node) reach a phone by different paths, in either order. A permanent answer here
    /// quarantined the add after a few tries and the phone never learned about the blind node.
    /// </summary>
    [Fact]
    public void OriginatorNotSuperadminException_IsDeferred() =>
        SyncFailureClassifier.Classify(new OriginatorNotSuperadminException(Guid.NewGuid(), EventTypes.WhitelistAdd))
            .Should().Be(SyncFailureKind.Deferred);

    [Fact]
    public void PlainUnauthorizedAccessException_IsPermanent() =>
        // What EventApplier throws for a revoked originator or a blind author — answers about a
        // fully-resolved peer. Must NOT be swept into Deferred just because it shares a BCL base
        // type with the two subclasses that should be.
        SyncFailureClassifier.Classify(new UnauthorizedAccessException("blind nodes never author events"))
            .Should().Be(SyncFailureKind.Permanent);

    [Fact]
    public void NotSupportedException_UnknownProtocolVersion_IsPermanent() =>
        SyncFailureClassifier.Classify(new NotSupportedException("Unknown protocol version: 99"))
            .Should().Be(SyncFailureKind.Permanent);

    /// <summary>
    /// A revoked peer is an ANSWER, not a missing precondition, and must not be deferred.
    ///
    /// <para>
    /// Both "never heard of this node" and "this node is revoked" reach the same branch, because
    /// the whitelist lookup filters on status = 'A' and returns null for either. Treating them
    /// alike would keep a revoked node's backlog alive for the whole deferred budget and let it
    /// apply in full if the peer were re-added inside that window — resurrecting exactly the writes
    /// the revocation was meant to discard.
    /// </para>
    /// </summary>
    [Fact]
    public void RevokedOriginator_IsPermanent_NotDeferred()
    {
        // The revoked branch throws the plain base type; only the never-seen branch throws the
        // deferrable subclass.
        SyncFailureClassifier.Classify(new UnauthorizedAccessException("Node x is revoked."))
            .Should().Be(SyncFailureKind.Permanent);

        SyncFailureClassifier.Classify(new OriginatorNotWhitelistedException(Guid.NewGuid()))
            .Should().Be(SyncFailureKind.Deferred);
    }
}
