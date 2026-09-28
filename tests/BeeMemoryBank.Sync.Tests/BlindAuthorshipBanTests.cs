using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Plan 3.2: an event of ANY type whose originator carries the blind-node mark is refused, before
/// anything else is looked at. A blind node never authors events, so a validly signed one means
/// its file key was stolen — and the whitelist row is no defence (it may even say superadmin).
/// </summary>
public class BlindAuthorshipBanTests : IAsyncLifetime
{
    private SyncTestFixture _node = null!;

    // No InitializeAsync of the node itself: applying needs no identity and no DEK, and skipping
    // them keeps this class out of the process-wide Argon2 gate the other classes queue on.
    public async Task InitializeAsync()
    {
        _node = new ConcreteFixture();
        await _node.InitializeAsync();
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    [Theory]
    [InlineData(EventTypes.ArticleCreate)]
    [InlineData(EventTypes.WhitelistAdd)]
    [InlineData(EventTypes.WhitelistRevoke)]
    [InlineData(EventTypes.HardDelete)]
    [InlineData(EventTypes.SnapshotCheckpoint)]
    [InlineData(EventTypes.RecoveryBoxSet)]
    [InlineData("some_type_from_a_future_build")]
    public async Task EventFromBlindOriginator_IsRejected_WhateverItsType(string eventType)
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var blindId = BlindNodeId.NewId();
        // Worst case on purpose: an active, superadmin row with the right key. Only the mark in the
        // id may decide.
        await WhitelistAsync(blindId, pub, isSuperadmin: true);
        var evt = SignedEvent(blindId, priv, eventType);

        var act = () => _node.EventApplier.ApplyAsync(evt);

        var ex = (await act.Should().ThrowAsync<UnauthorizedAccessException>()).Which;
        ex.Message.Should().Contain("blind");
        SyncFailureClassifier.Classify(ex).Should().Be(SyncFailureKind.Permanent,
            "a stolen blind key is not a precondition that will ever resolve");
        (await _node.EventLogRepo.ExistsAsync(evt.EventId)).Should().BeFalse(
            "a rejected event must not enter the log, or this node would relay it onward");
    }

    /// <summary>
    /// Review L-stage0 round 2 #1: an event a pre-fix build already stored is refused on replay too,
    /// not waved through by the "already applied" shortcut.
    /// </summary>
    [Fact]
    public async Task ABlindAuthoredEventAlreadyInTheLog_IsRefusedOnReplay()
    {
        var (_, priv) = Ed25519Signer.GenerateKeyPair();
        var evt = SignedEvent(BlindNodeId.NewId(), priv, EventTypes.ArticleCreate);
        await _node.EventLogRepo.AppendAsync(evt);

        var act = () => _node.EventApplier.ApplyAsync(evt);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    /// <summary>
    /// An unknown blind id is refused for good, not parked as "whitelist_add still in flight": that
    /// deferred answer would keep the event retried for hours and then relayed if a row appeared.
    /// </summary>
    [Fact]
    public async Task EventFromAnUnknownBlindOriginator_IsRefusedPermanently_NotDeferred()
    {
        var (_, priv) = Ed25519Signer.GenerateKeyPair();
        var evt = SignedEvent(BlindNodeId.NewId(), priv, EventTypes.ArticleCreate);

        var act = () => _node.EventApplier.ApplyAsync(evt);

        var ex = (await act.Should().ThrowAsync<UnauthorizedAccessException>()).Which;
        SyncFailureClassifier.Classify(ex).Should().Be(SyncFailureKind.Permanent);
    }

    /// <summary>
    /// Control for the theory above: the same unknown-type event from an ordinary id with the same
    /// kind of row is stored (forward compatibility), so what rejected it there was the mark.
    /// </summary>
    [Fact]
    public async Task SameEventFromOrdinaryOriginator_IsStored()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var ordinaryId = Guid.NewGuid();
        await WhitelistAsync(ordinaryId, pub, isSuperadmin: true);
        var evt = SignedEvent(ordinaryId, priv, "some_type_from_a_future_build");

        (await _node.EventApplier.ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
        (await _node.EventLogRepo.ExistsAsync(evt.EventId)).Should().BeTrue();
    }

    private async Task WhitelistAsync(Guid nodeId, byte[] publicKey, bool isSuperadmin)
    {
        var now = DateTime.UtcNow;
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = "peer",
            Ed25519PublicKey = publicKey,
            Status = "A",
            IsSuperadmin = isSuperadmin,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private static SyncEvent SignedEvent(Guid originator, byte[] privateKey, string eventType)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = originator,
            LamportTs = 10,
            EventType = eventType,
            Payload = "{}",
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(privateKey, EventSignature.BuildPayload(evt));
        return evt;
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}

/// <summary>
/// The source half of plan 3.2: a blind node's own EventLogger refuses to author anything, so no
/// stray local write can put an event into its log for every peer to reject.
/// </summary>
public class BlindNodeAuthorsNothingTests : SyncTestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await NodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = BlindNodeId.NewId(),
            DisplayName = "Blind",
            Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion,
            CreatedAt = DateTime.UtcNow
        });
    }

    [Fact]
    public async Task EventLogger_OnABlindNode_RefusesAndWritesNothing()
    {
        var act = () => EventLogger.LogWhitelistRevokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*blind*");
        (await EventLogRepo.GetAfterSequenceAsync(0)).Should().BeEmpty();
    }
}
