using System.Security.Cryptography;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.DekRotation;
using FluentAssertions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A rotation proposed before this node re-keyed (release 2.12, plan WS-D D4): its envelope was sealed for the node's previous identity key.
/// Opening it is "not yet" (a precondition: the rotation stays Committing, never Failed); the node asks the rotation's author for a fresh one,
/// keeps it, and the retried rotation opens that.
/// </summary>
public class DekEnvelopeRefreshTests : SyncTestFixture
{
    private static (NodeIdentity Identity, byte[] OldPub, byte[] NewPub) Rekeyed(byte[] currentDek)
    {
        var node = Guid.NewGuid();
        var (oldPub, _) = Ed25519Signer.GenerateKeyPair();
        var (newPub, newSeed) = Ed25519Signer.GenerateKeyPair();
        var (wrapped, iv) = NodeIdentityVault.EncryptPrivateKey(newSeed, currentDek, node);
        Array.Clear(newSeed);
        return (new NodeIdentity { NodeId = node, Ed25519PublicKey = newPub, Ed25519PrivateKey = wrapped, Ed25519PrivateKeyIV = iv, Ed25519PrivateKeyV = 1 },
            oldPub, newPub);
    }

    private static DekRotationCommitPayload Commit(DekEnvelope.Set set) => new(Guid.NewGuid().ToString("D"), 2, DateTime.UtcNow.ToString("O"),
        Guid.NewGuid().ToString("D"),
        DekEnvelopes: new DekEnvelopesPayload(set.EphemeralPublicKeyB64,
            set.Peers.ToDictionary(p => p.Key, p => new DekEnvelopeBox(p.Value.WrappedB64, p.Value.NonceB64))));

    [Fact]
    public void An_envelope_sealed_for_the_previous_key_is_a_precondition_not_a_failure_and_a_fresh_one_opens()
    {
        var oldDek = RandomNumberGenerator.GetBytes(32);
        var newDek = RandomNumberGenerator.GetBytes(32);
        var commitId = Guid.NewGuid();
        var (identity, oldPub, newPub) = Rekeyed(oldDek);
        var payload = Commit(DekEnvelope.Build(newDek, commitId, [new DekEnvelope.Recipient(identity.NodeId, oldPub)]));

        var stale = () => DekRotationMaterial.ResolveNewDek(payload, commitId, identity, oldDek, out _, out _);
        var thrown = stale.Should().Throw<DekEnvelopeStaleException>().Which;
        thrown.Should().BeAssignableTo<DekRotationPreconditionException>("a precondition keeps the rotation Committing, retryable");
        thrown.CommitEventId.Should().Be(commitId);

        var fresh = DekEnvelope.Build(newDek, commitId, [new DekEnvelope.Recipient(identity.NodeId, newPub)]);
        var box = fresh.Peers.Values.Single();
        var opened = DekRotationMaterial.ResolveNewDek(payload, commitId, identity, oldDek, out _, out _,
            new DekEnvelopeAnswer(fresh.EphemeralPublicKeyB64, box.WrappedB64, box.NonceB64));
        opened.Should().Equal(newDek);
    }

    [Fact]
    public async Task The_request_the_fetch_and_the_forget_are_node_local_markers_per_commit_and_author()
    {
        var commit = Guid.NewGuid();
        var author = Guid.NewGuid();
        var other = Guid.NewGuid();

        await DekEnvelopeRefresh.RequestAsync(Factory, commit, author);
        (await DekEnvelopeRefresh.RequestedFromAsync(Factory, author)).Should().Equal(commit);
        (await DekEnvelopeRefresh.RequestedFromAsync(Factory, other)).Should().BeEmpty("only the node that rotated is asked");

        var answer = new DekEnvelopeAnswer("ZXBo", "d3JhcA==", "bm9uY2U=");
        await DekEnvelopeRefresh.StoreAsync(Factory, commit, answer);
        (await DekEnvelopeRefresh.FetchedAsync(Factory, commit)).Should().Be(answer);
        (await DekEnvelopeRefresh.RequestedFromAsync(Factory, author)).Should().BeEmpty("a fetched envelope is not asked for again");

        await DekEnvelopeRefresh.ForgetAsync(Factory, commit);
        (await DekEnvelopeRefresh.FetchedAsync(Factory, commit)).Should().BeNull("forgotten once the rotation applied");
    }
}
