using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The blind node's side of pairing (plan 4.1): issues the pair code and checks the one-time secret
/// a seed presents. The code is shown by the local console only; the secret is what makes the
/// first seed — the moment the blind node learns whom to trust — something only the person reading
/// that console can authorize.
/// </summary>
public sealed class BlindPairing(
    BlindState state,
    BlindJoinStore joinStore,
    INodeIdentityRepository nodeRepo,
    BlindTlsIdentity tls,
    IConfiguration config,
    TimeProvider time)
{
    /// <summary>
    /// What issuing a code grants, shown with the code by the console and the CLI: the device whose
    /// first seed uses it becomes this node's superadmin (review r1-merge #3).
    /// </summary>
    public const string AuthorityNotice =
        "The device that uses this code will manage this blind node (reseed, anchors, recovery).";

    /// <summary>
    /// The current code, or a new one when there is none or it expired. <paramref name="renew"/>
    /// always issues a new secret, which also invalidates the previous one.
    /// </summary>
    public async Task<BlindPairCode> GetCodeAsync(bool renew)
    {
        // One way of being attached at a time: while this node waits for a full node's answer to its join, nobody may read or renew the secret it gave that node.
        if (await BlindJoinRecord.InProgressAsync(joinStore, state, time) is { } join)
            throw new BlindJoinInProgressException(join);
        // A join that says it waits, but has no secret behind it any more (the node was wiped, the secret ran out) is over: a new secret made below must not bring it back to life.
        if (joinStore.Read() is { State: BlindJoinStates.Awaiting or BlindJoinStates.Redeeming } stale) await joinStore.WriteAsync(stale with { State = BlindJoinStates.Expired });
        var address = PublicAddress();
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");
        var now = time.GetUtcNow().UtcDateTime;
        var current = await state.GetPairingSecretAsync();
        string secret;
        DateTime expiresAt;
        if (!renew && current is { } c && c.ExpiresAt > now)
        {
            (secret, expiresAt) = c;
        }
        else
        {
            secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            expiresAt = now + BlindPairCode.Lifetime;
            await state.SetPairingSecretAsync(secret, expiresAt);
        }

        return new BlindPairCode(identity.NodeId, Convert.ToBase64String(identity.Ed25519PublicKey), address, tls.Spki, secret, expiresAt);
    }

    /// <summary>
    /// The pair code of a JOIN (<c>bmb blind join</c>): the same code, but its secret stays valid for <paramref name="lifetime"/> (the full node's owner has days to answer: a join lives
    /// about 73 hours, <see cref="BlindJoinService.PairingLifetime"/>; a shown code lives <see cref="BlindPairCode.Lifetime"/>, 15 minutes) and the join keeps it from being read by the old way. <paramref name="reuseCurrent"/> keeps the secret of an earlier call of the same join (a retry sends the same
    /// code); otherwise a new secret replaces whatever was there.
    /// </summary>
    public async Task<BlindPairCode> IssueJoinCodeAsync(TimeSpan lifetime, bool reuseCurrent)
    {
        var address = PublicAddress();
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");
        var now = time.GetUtcNow().UtcDateTime;
        string secret;
        DateTime expiresAt;
        if (reuseCurrent && await state.GetPairingSecretAsync() is { } c && c.ExpiresAt > now)
        {
            (secret, expiresAt) = c;
        }
        else
        {
            secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            expiresAt = now + lifetime;
            await state.SetPairingSecretAsync(secret, expiresAt);
        }
        return new BlindPairCode(identity.NodeId, Convert.ToBase64String(identity.Ed25519PublicKey), address, tls.Spki, secret, expiresAt);
    }

    /// <summary>A join ended without a seed (refused, run out, cancelled): the secret it gave the full node is no longer valid.</summary>
    public Task ForgetJoinSecretAsync() => state.ClearPairingSecretAsync();

    private string PublicAddress()
    {
        var address = config["BMB_PUBLIC_ADDRESS"];
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException(
                "BMB_PUBLIC_ADDRESS is not set: the pair code must tell the PC where to reach this node (https://host:port).");
        return address.Trim().TrimEnd('/');
    }

    /// <summary>For the status page: is there a code someone could still use.</summary>
    public async Task<bool> IsCodeActiveAsync() =>
        await state.GetPairingSecretAsync() is { } c && c.ExpiresAt > time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The id of the code (<see cref="CodeIdOf"/>) if the current, unexpired secret vouches for this
    /// seeder and key for this seed (<see cref="BlindSeederProof"/>); otherwise null.
    /// </summary>
    public async Task<string?> VerifySeederAsync(Guid seedId, Guid seederNodeId, string seederKeyB64, string macB64)
    {
        if (await state.GetPairingSecretAsync() is not { } current) return null;
        if (current.ExpiresAt <= time.GetUtcNow().UtcDateTime) return null;
        return BlindSeederProof.Verify(current.Secret, seedId, seederNodeId, seederKeyB64, macB64) ? CodeIdOf(current.Secret) : null;
    }

    /// <summary>
    /// Whether the code a proof was checked against is still the current, unexpired, unspent one. The
    /// seed service asks again under its upload gate (review l-root2 #1): a proof checked before
    /// another upload with the same code spent it must not be accepted after that upload is done.
    /// </summary>
    public async Task<bool> IsCurrentCodeAsync(string codeId) =>
        await state.GetPairingSecretAsync() is { } current
        && current.ExpiresAt > time.GetUtcNow().UtcDateTime
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeIdOf(current.Secret)), Encoding.ASCII.GetBytes(codeId));

    /// <summary>Names a code without revealing its secret: the hash of the secret.</summary>
    public static string CodeIdOf(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>A first seed took: the code is spent.</summary>
    public Task ConsumeAsync() => state.ClearPairingSecretAsync();
}
