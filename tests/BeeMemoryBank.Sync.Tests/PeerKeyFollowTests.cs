using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using FluentAssertions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The handshake's key transition chain (release 2.12, plan WS-D D4): a caller that pinned an older key of a peer follows the peer's signed chain to
/// the key it answers with, and nothing else moves the pin.
/// </summary>
public class PeerKeyFollowTests
{
    private static readonly Guid Node = Guid.NewGuid();

    private sealed record Key(byte[] Public, byte[] Seed);

    private static Key NewKey()
    {
        var (pk, seed) = Ed25519Signer.GenerateKeyPair();
        return new Key(pk, seed);
    }

    private static NodeKeyLinkDto Link(Guid node, Key from, Key to, long counter, Key? signerOfOld = null)
    {
        var statement = NodeKeyTransition.Statement(node, from.Public, to.Public, counter);
        return NodeKeyLinkDto.Of(new NodeKeyLink(from.Public, to.Public, counter,
            Ed25519Signer.Sign((signerOfOld ?? from).Seed, statement), Ed25519Signer.Sign(to.Seed, statement)));
    }

    private static string B64(Key k) => Convert.ToBase64String(k.Public);

    [Fact]
    public void The_pinned_key_itself_matches_without_a_chain()
    {
        var k = NewKey();
        PeerKey.Follow(Node, k.Public, B64(k), null).Should().Equal(k.Public);
    }

    [Fact]
    public void A_signed_chain_from_the_pin_to_the_declared_key_is_followed_across_several_rekeys()
    {
        var (k0, k1, k2) = (NewKey(), NewKey(), NewKey());
        var chain = new[] { Link(Node, k0, k1, 1), Link(Node, k1, k2, 2) };

        PeerKey.Follow(Node, k0.Public, B64(k2), chain).Should().Equal(k2.Public);
        PeerKey.Follow(Node, k1.Public, B64(k2), chain).Should().Equal(k2.Public, "a pin in the middle of the chain follows the rest");
        PeerKey.MatchesOrFollows(Node, k0.Public, new RemoteIdentity(Node, "n", B64(k2), KeyChain: chain)).Should().BeTrue();
    }

    [Fact]
    public void Without_a_chain_or_with_a_chain_that_does_not_reach_the_declared_key_nothing_is_followed()
    {
        var (k0, k1, k2) = (NewKey(), NewKey(), NewKey());
        PeerKey.Follow(Node, k0.Public, B64(k1), null).Should().BeNull("a new key without a chain is another node");
        PeerKey.Follow(Node, k0.Public, B64(k2), [Link(Node, k0, k1, 1)]).Should().BeNull("the chain ends at another key");
    }

    [Fact]
    public void A_link_not_signed_by_the_pinned_key_or_of_another_node_or_leading_back_is_never_followed()
    {
        var (k0, k1, k2, attacker) = (NewKey(), NewKey(), NewKey(), NewKey());
        PeerKey.Follow(Node, k0.Public, B64(k1), [Link(Node, k0, k1, 1, signerOfOld: attacker)]).Should().BeNull("the old key did not sign it");
        PeerKey.Follow(Node, k0.Public, B64(k1), [Link(Guid.NewGuid(), k0, k1, 1)]).Should().BeNull("the statement names another node");
        PeerKey.Follow(Node, k0.Public, B64(k2), [Link(Node, k0, k1, 2), Link(Node, k1, k2, 1)]).Should().BeNull("counters must grow along the walk");
    }

    [Fact]
    public void A_pin_older_than_the_sixteen_links_the_handshake_carries_is_not_followed()
    {
        var keys = Enumerable.Range(0, NodeKeyTransition.MaxChain + 2).Select(_ => NewKey()).ToList();
        var chain = Enumerable.Range(0, keys.Count - 1).Select(i => Link(Node, keys[i], keys[i + 1], i + 1)).ToList();

        PeerKey.Follow(Node, keys[0].Public, B64(keys[^1]), chain).Should().BeNull("only the first 16 links are read: the device pairs again");
        PeerKey.Follow(Node, keys[^(NodeKeyTransition.MaxChain + 1)].Public, B64(keys[^1]), chain.TakeLast(NodeKeyTransition.MaxChain).ToList())
            .Should().Equal(keys[^1].Public, "16 links are followed");
    }

    [Fact]
    public void A_declared_key_or_a_link_that_is_not_base64_or_not_32_bytes_is_refused_without_throwing()
    {
        var (k0, k1) = (NewKey(), NewKey());
        PeerKey.Follow(Node, k0.Public, "not base64!", [Link(Node, k0, k1, 1)]).Should().BeNull();
        PeerKey.Follow(Node, k0.Public, Convert.ToBase64String(new byte[31]), null).Should().BeNull();
        PeerKey.Follow(Node, k0.Public, B64(k1), [new NodeKeyLinkDto("??", "??", 1, "??", "??")]).Should().BeNull();
        PeerKey.Follow(Node, null, B64(k1), null).Should().BeNull();
    }
}
