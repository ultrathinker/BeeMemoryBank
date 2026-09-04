using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Night-7: SyncFailureClassifier is the single place an apply-failure exception is sorted into
/// Permanent vs Deferred (see its own remarks for why this must not be re-derived at each call
/// site). These tests pin down every exception type EventApplier/SyncClient actually throw for
/// this decision — including the two DIFFERENT UnauthorizedAccessException uses in EventApplier
/// ("not in whitelist" vs "known peer, not superadmin"), which is exactly the pair a naive
/// "classify by BCL exception type" rule would get wrong.
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

    [Fact]
    public void PlainUnauthorizedAccessException_KnownPeerNotSuperadmin_IsPermanent() =>
        // The OTHER UnauthorizedAccessException EventApplier throws (requiresSuperadmin gate) —
        // a fully-resolved peer that is not authorized, as opposed to OriginatorNotWhitelistedException's
        // "we don't know this peer at all yet". Must NOT be swept into Deferred just because it
        // shares a BCL base type with the one that should be.
        SyncFailureClassifier.Classify(new UnauthorizedAccessException("requires superadmin privilege"))
            .Should().Be(SyncFailureKind.Permanent);

    [Fact]
    public void NotSupportedException_UnknownProtocolVersion_IsPermanent() =>
        SyncFailureClassifier.Classify(new NotSupportedException("Unknown protocol version: 99"))
            .Should().Be(SyncFailureKind.Permanent);
}
