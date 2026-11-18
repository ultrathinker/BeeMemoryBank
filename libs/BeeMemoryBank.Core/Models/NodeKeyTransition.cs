using System.Buffers.Binary;
using System.Text;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Models;

/// <summary>
/// One row of <c>tbl_node_key_history</c> (migration 049): a <c>node_key_rotated</c> event this node applied. <see cref="PublicKey"/> is the key the
/// rotation RETIRED; it may sign the node's events up to <see cref="ValidUntilLamport"/> (the rotation event's own lamport), and on live sync only
/// those above <see cref="CheckpointLamportTs"/> whose id is in <see cref="OwnEventIds"/> (plan WS-D D3).
/// </summary>
public sealed record NodeKeyHistoryEntry(
    Guid NodeId,
    long Counter,
    byte[] PublicKey,
    byte[] NewPublicKey,
    long ValidUntilLamport,
    Guid RotationEventId,
    long CheckpointLamportTs,
    string OwnEventIds,
    byte[] OldKeyProof,
    byte[] NewKeyProof)
{
    /// <summary>The link of the key transition chain this row is (plan D4).</summary>
    public NodeKeyLink Link => new(PublicKey, NewPublicKey, Counter, OldKeyProof, NewKeyProof);
}

/// <summary>
/// One step of a node's key transition chain, as <c>/api/sync/identity</c> and the blind pull and pair endpoints return it: the retired key, the
/// key that replaced it, the rotation counter, and the signatures of both keys over <see cref="NodeKeyTransition.Statement"/>.
/// </summary>
public sealed record NodeKeyLink(byte[] OldPublicKey, byte[] NewPublicKey, long Counter, byte[] SigByOld, byte[] SigByNew);

/// <summary>
/// The transition statement both keys of a re-key sign, and the walk a caller that pinned an older key makes along the chain (release 2.12, plan
/// WS-D D1, D4). A link counts only when the OLD key signed the statement (the node that held it agreed) and the NEW key signed it (the new key
/// exists and belongs to the same node, rotation and counter); a chain is walked from the pinned key, link by link, never skipping one.
/// </summary>
public static class NodeKeyTransition
{
    /// <summary>The newest links a node returns, and the most rotations a copy that was offline can follow before it has to pair again.</summary>
    public const int MaxChain = 16;

    public const int KeySize = 32;
    public const int SignatureSize = 64;

    // Domain separation: the same Ed25519 key signs sync events, challenges and manifests; this prefix keeps a statement from ever being read
    // as one of those, and the reverse.
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("bmb/node-key-rotation/v1\0");

    /// <summary>The bytes both keys sign: the domain, the node id (RFC 4122 order), the old key, the new key, the counter (big-endian).</summary>
    public static byte[] Statement(Guid nodeId, byte[] oldPublicKey, byte[] newPublicKey, long counter)
    {
        ArgumentNullException.ThrowIfNull(oldPublicKey);
        ArgumentNullException.ThrowIfNull(newPublicKey);
        if (oldPublicKey.Length != KeySize || newPublicKey.Length != KeySize) throw new ArgumentException("An Ed25519 public key is 32 bytes.");
        var statement = new byte[Domain.Length + 16 + KeySize + KeySize + 8];
        var span = statement.AsSpan();
        Domain.CopyTo(span);
        var at = Domain.Length;
        nodeId.TryWriteBytes(span.Slice(at, 16), bigEndian: true, out _);
        at += 16;
        oldPublicKey.CopyTo(span[at..]);
        at += KeySize;
        newPublicKey.CopyTo(span[at..]);
        at += KeySize;
        BinaryPrimitives.WriteInt64BigEndian(span[at..], counter);
        return statement;
    }

    /// <summary>True when <paramref name="signature"/> is <paramref name="publicKey"/>'s signature over the statement. Never throws for a malformed input.</summary>
    public static bool VerifyProof(byte[]? publicKey, byte[]? signature, Guid nodeId, byte[] oldPublicKey, byte[] newPublicKey, long counter)
    {
        if (publicKey is not { Length: KeySize } || signature is not { Length: SignatureSize }
            || oldPublicKey is not { Length: KeySize } || newPublicKey is not { Length: KeySize })
            return false;
        try
        {
            return Ed25519Signer.Verify(publicKey, Statement(nodeId, oldPublicKey, newPublicKey, counter), signature);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A link is valid when both keys signed its statement, the counter is positive and the keys differ.</summary>
    public static bool VerifyLink(Guid nodeId, NodeKeyLink link) =>
        link.Counter > 0
        && link.OldPublicKey is { Length: KeySize } && link.NewPublicKey is { Length: KeySize }
        && !link.OldPublicKey.AsSpan().SequenceEqual(link.NewPublicKey)
        && VerifyProof(link.OldPublicKey, link.SigByOld, nodeId, link.OldPublicKey, link.NewPublicKey, link.Counter)
        && VerifyProof(link.NewPublicKey, link.SigByNew, nodeId, link.OldPublicKey, link.NewPublicKey, link.Counter);

    /// <summary>
    /// Walks <paramref name="chain"/> from <paramref name="pinned"/>: the link whose old key is the pinned one (and that verifies) moves the pin to
    /// its new key, until no further link starts at the pin. Returns the key reached (<paramref name="pinned"/> itself when nothing applies).
    /// Counters must grow along the walk, so a link cannot lead back. A link that does not verify is never followed.
    /// </summary>
    public static byte[] Advance(Guid nodeId, byte[] pinned, IEnumerable<NodeKeyLink>? chain)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        if (chain is null) return pinned;
        var links = chain.Where(l => l is not null).OrderBy(l => l.Counter).ToList();
        var current = pinned;
        var lastCounter = 0L;
        for (var step = 0; step < links.Count; step++)
        {
            var next = links.FirstOrDefault(l => l.Counter > lastCounter && l.OldPublicKey is { Length: KeySize }
                                                 && l.OldPublicKey.AsSpan().SequenceEqual(current) && VerifyLink(nodeId, l));
            if (next is null) break;
            current = next.NewPublicKey;
            lastCounter = next.Counter;
        }
        return current;
    }

    /// <summary>The SHA-256 fingerprint of a public key (hex, lower case), as the pages and <c>node_state</c> show it.</summary>
    public static string Fingerprint(byte[] publicKey) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(publicKey)).ToLowerInvariant();
}

/// <summary>
/// The packed list of a node's own event ids above its checkpoint (<c>own_event_ids</c> of <c>node_key_rotated</c>, plan D3): base64 of the ids'
/// 16-byte values (RFC 4122 order), sorted, without duplicates. At most <see cref="Cap"/> ids.
/// </summary>
public static class EventIdSet
{
    public const int Cap = 50_000;

    public static string Pack(IEnumerable<Guid> ids)
    {
        var sorted = ids.Select(ToBytes).Distinct(ByteArrayComparer.Instance).OrderBy(b => b, ByteArrayComparer.Instance).ToList();
        if (sorted.Count > Cap) throw new InvalidOperationException($"At most {Cap} event ids fit in a rotation.");
        var packed = new byte[sorted.Count * 16];
        for (var i = 0; i < sorted.Count; i++) sorted[i].CopyTo(packed, i * 16);
        return Convert.ToBase64String(packed);
    }

    /// <summary>The ids of a packed list; empty for an empty string. Throws <see cref="FormatException"/> for a malformed one.</summary>
    public static HashSet<Guid> Unpack(string? packed)
    {
        var set = new HashSet<Guid>();
        if (string.IsNullOrEmpty(packed)) return set;
        var bytes = Convert.FromBase64String(packed);
        if (bytes.Length % 16 != 0 || bytes.Length / 16 > Cap) throw new FormatException("A packed event id list is a multiple of 16 bytes and at most 50000 ids.");
        for (var i = 0; i < bytes.Length; i += 16) set.Add(new Guid(bytes.AsSpan(i, 16), bigEndian: true));
        return set;
    }

    private static byte[] ToBytes(Guid id)
    {
        var bytes = new byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x!.AsSpan().SequenceCompareTo(y);
        public bool Equals(byte[]? x, byte[]? y) => x!.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => HashCode.Combine(BitConverter.ToInt64(obj, 0), BitConverter.ToInt64(obj, 8));
    }
}
