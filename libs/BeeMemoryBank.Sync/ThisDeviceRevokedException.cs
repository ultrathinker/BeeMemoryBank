using System.Net;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// A peer proved that this node's whitelist row is revoked on it (release 2.12): an administrator disconnected this device from the bank.
/// It is a 401 like every refusal, so every caller that already treats a refused door keeps doing so; the apps that know this type say the calm
/// sentence of <see cref="Sentence"/> instead of a generic sync error and stop asking every minute. What is already on the device stays readable;
/// it no longer receives changes. The message is a fixed sentence: nothing the peer sent is in it.
/// </summary>
public sealed class ThisDeviceRevokedException(Guid peerNodeId)
    : HttpRequestException(Sentence, null, HttpStatusCode.Unauthorized)
{
    /// <summary>The words of every app (the phones have their own translations of it).</summary>
    public const string Sentence =
        "An administrator disconnected this device from the bank. What is already on it stays readable here; it no longer receives changes.";

    /// <summary>The peer that said so.</summary>
    public Guid PeerNodeId { get; } = peerNodeId;
}

/// <summary>
/// The refusal <c>POST /api/sync/authenticate</c> gives a caller whose whitelist row is revoked, after the caller's challenge signature verified
/// under that row's key (so only the device itself learns it; everybody else gets the plain 401 of any refusal). The node signs it with its own
/// identity key, bound to the challenge the caller signed, so neither a relay nor a man in the middle of a plain-HTTP LAN call can make a device
/// believe it was disconnected. A node that cannot sign (its vault is locked) answers the plain 401.
/// </summary>
public static class DeviceRevokedProof
{
    /// <summary>The <c>refusal</c> member of the 401 body.</summary>
    public const string Refusal = "device_revoked";

    private static readonly byte[] DomainTag = "BMB-DEVICE-REVOKED-V1\0"u8.ToArray();

    /// <summary>What the node signs: the tag, its own node id, the caller's node id, and the challenge the caller signed.</summary>
    public static byte[] Payload(Guid serverNodeId, Guid callerNodeId, byte[] challenge) =>
        [.. DomainTag, .. serverNodeId.ToByteArray(), .. callerNodeId.ToByteArray(), .. challenge];

    /// <summary>
    /// True when <paramref name="signature"/> is the node's signature of this refusal under the key this device knows the node by. Through the
    /// helper every node signature goes through (plan WS-D D3): the current key only, never throwing for a key or signature of the wrong size. A node
    /// that re-keyed and has not been followed here yet is not believed: the refusal stays a plain 401.
    /// </summary>
    public static bool Verify(byte[]? serverPublicKey, Guid serverNodeId, Guid callerNodeId, byte[] challenge, byte[]? signature) =>
        NodeEventVerifier.SignatureVerifies(serverPublicKey, Payload(serverNodeId, callerNodeId, challenge), signature);
}
