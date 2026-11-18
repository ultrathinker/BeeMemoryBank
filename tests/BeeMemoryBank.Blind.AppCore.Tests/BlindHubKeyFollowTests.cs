using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// A copy's pin of its hub's key (release 2.12, plan WS-D D4): a hub that re-keyed answers with its new key and the signed chain that leads to it;
/// the copy moves its pin, every reader of the call code gets the moved key (the channel planner's row too), the code as paired stays for its MAC,
/// and a new pairing forgets the moved key.
/// </summary>
public sealed class BlindHubKeyFollowTests
{
    private sealed class MemoryStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }

    private static readonly byte[] Secret = Enumerable.Repeat((byte)7, 32).ToArray();

    private static (BlindPhoneState State, Guid Hub, byte[] OldKey, byte[] OldSeed) Paired()
    {
        var hub = Guid.NewGuid();
        var (pk, seed) = Ed25519Signer.GenerateKeyPair();
        var state = new BlindPhoneState(new MemoryStore())
        {
            CallCode = BlindCallCode.Create("https://hub.example.org:5300", hub, new string('A', 43), pk, Secret),
        };
        return (state, hub, pk, seed);
    }

    private static NodeKeyLinkDto Link(Guid hub, byte[] oldPk, byte[] oldSeed, byte[] newPk, byte[] newSeed, long counter)
    {
        var statement = NodeKeyTransition.Statement(hub, oldPk, newPk, counter);
        return NodeKeyLinkDto.Of(new NodeKeyLink(oldPk, newPk, counter, Ed25519Signer.Sign(oldSeed, statement), Ed25519Signer.Sign(newSeed, statement)));
    }

    [Fact]
    public void A_hub_that_rekeyed_moves_the_pin_and_every_reader_of_the_call_code_gets_the_new_key()
    {
        var (state, hub, oldPk, oldSeed) = Paired();
        var (newPk, newSeed) = Ed25519Signer.GenerateKeyPair();
        var identity = new RemoteIdentity(hub, "Hub", Convert.ToBase64String(newPk), KeyChain: [Link(hub, oldPk, oldSeed, newPk, newSeed, 1)]);

        state.FollowHubKey(identity).Should().BeTrue();

        state.CallCode!.PublicKey.Should().Equal(newPk, "the packages and events the hub signs now are checked by its new key");
        BlindHubChannels.HubRow(state.CallCode).Ed25519PublicKey.Should().Equal(newPk, "the channel planner holds every address to the moved key");
        state.PairedCallCode!.PublicKey.Should().Equal(oldPk, "the code as paired is kept as it was");
        state.PairedCallCode.IsAuthenticBy(Secret).Should().BeTrue("its MAC still checks against the pairing secret");
        state.FollowHubKey(identity).Should().BeFalse("a second answer with the same key moves nothing");

        // A second re-key follows on from the moved pin.
        var (newerPk, newerSeed) = Ed25519Signer.GenerateKeyPair();
        state.FollowHubKey(new RemoteIdentity(hub, "Hub", Convert.ToBase64String(newerPk),
            KeyChain: [Link(hub, oldPk, oldSeed, newPk, newSeed, 1), Link(hub, newPk, newSeed, newerPk, newerSeed, 2)])).Should().BeTrue();
        state.CallCode!.PublicKey.Should().Equal(newerPk);
    }

    [Fact]
    public void Another_node_a_key_without_a_chain_or_a_chain_the_old_key_did_not_sign_leaves_the_pin_where_it_is()
    {
        var (state, hub, oldPk, _) = Paired();
        var (newPk, newSeed) = Ed25519Signer.GenerateKeyPair();
        var (_, attackerSeed) = Ed25519Signer.GenerateKeyPair();

        state.FollowHubKey(new RemoteIdentity(Guid.NewGuid(), "Other", Convert.ToBase64String(newPk))).Should().BeFalse();
        state.FollowHubKey(new RemoteIdentity(hub, "Hub", Convert.ToBase64String(newPk))).Should().BeFalse();
        state.FollowHubKey(new RemoteIdentity(hub, "Hub", Convert.ToBase64String(newPk),
            KeyChain: [Link(hub, oldPk, attackerSeed, newPk, newSeed, 1)])).Should().BeFalse("only the holder of the pinned key can name its successor");

        state.CallCode!.PublicKey.Should().Equal(oldPk);
    }

    [Fact]
    public void A_new_pairing_forgets_the_moved_key()
    {
        var (state, hub, oldPk, oldSeed) = Paired();
        var (newPk, newSeed) = Ed25519Signer.GenerateKeyPair();
        state.FollowHubKey(new RemoteIdentity(hub, "Hub", Convert.ToBase64String(newPk), KeyChain: [Link(hub, oldPk, oldSeed, newPk, newSeed, 1)]))
            .Should().BeTrue();

        var (otherPk, _) = Ed25519Signer.GenerateKeyPair();
        state.CallCode = BlindCallCode.Create("https://hub.example.org:5300", hub, new string('A', 43), otherPk, Secret);

        state.CallCode!.PublicKey.Should().Equal(otherPk, "the pairing's key, not a key moved under an earlier pairing");
    }
}
