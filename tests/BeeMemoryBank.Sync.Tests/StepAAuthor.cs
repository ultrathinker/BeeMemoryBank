using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A peer that authors Step A events by hand (docs/design/step-a-spec.md 3, 4): a legacy v=0 identity (its seed is known,
/// so no Argon2), a MEK wrapped under a DEK of the test's choosing, and protocol-3 or sealed events signed as a real author
/// signs them. The receiving node only ever sees what a peer could send.
/// </summary>
public sealed class StepAAuthor
{
    public Guid NodeId { get; } = Guid.NewGuid();
    public byte[] PublicKey { get; }
    private readonly byte[] _seed;
    private long _lamport = 1000;

    public string KeyId { get; private set; } = "";
    public byte[] Mek { get; private set; } = [];
    public MetadataKeyRow? KeyRow { get; private set; }

    public StepAAuthor()
    {
        (PublicKey, _seed) = Ed25519Signer.GenerateKeyPair();
    }

    /// <summary>A new MEK of <paramref name="generation"/>, wrapped under <paramref name="dek"/>, and its signed metadata_key_set.</summary>
    public SyncEvent NewKey(byte[] dek, int generation = 1)
    {
        var (mek, row) = MetadataKeyCrypto.Create(generation, dek);
        (Mek, KeyId, KeyRow) = (mek, row.KeyId, row);
        return KeySetFor(row);
    }

    /// <summary>Seals with a MEK another author created (a second writer of the same network).</summary>
    public void UseKey(byte[] mek, MetadataKeyRow row) => (Mek, KeyId, KeyRow) = (mek, row.KeyId, row);

    public SyncEvent KeySetFor(MetadataKeyRow row) => Sign(new SyncEvent
    {
        EventType = EventTypes.MetadataKeySet,
        Payload = JsonSerializer.Serialize(new MetadataKeySetPayload(row.KeyId, row.Generation, row.DekFingerprint, 0,
            Convert.ToBase64String(row.Wrapped), Convert.ToBase64String(row.Iv), DateTime.UtcNow.ToString("O"))),
        ProtocolVersion = 3,
    });

    public SyncEvent Activation(Guid? activationId = null) => Sign(new SyncEvent
    {
        EventType = EventTypes.MetadataActivation,
        Payload = JsonSerializer.Serialize(new MetadataActivationPayload((activationId ?? Guid.NewGuid()).ToString(), KeyId,
            KeyRow!.Generation, DateTime.UtcNow.ToString("O"))),
        ProtocolVersion = 3,
    });

    /// <summary>A content or control event in clear, signed.</summary>
    public SyncEvent Clear(string type, Guid? articleId, string payload, int protocol = 3) => Sign(new SyncEvent
    {
        EventType = type, ArticleId = articleId, Payload = payload, ProtocolVersion = protocol,
    });

    /// <summary>A content event sealed under this author's current MEK, signed as the outer event.</summary>
    public SyncEvent Sealed(string type, Guid? articleId, string payload, Action<SyncEvent>? tamperOuter = null)
    {
        var inner = Stamp(new SyncEvent { EventType = type, ArticleId = articleId, Payload = payload, ProtocolVersion = 3 });
        var eventKey = MetadataKeyCrypto.DeriveEventKey(Mek);
        try
        {
            var outer = MetadataEnvelope.Seal(inner, KeyId, eventKey);
            tamperOuter?.Invoke(outer);
            outer.Signature = Ed25519Signer.Sign(_seed, EventSignature.BuildPayload(outer));
            return outer;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(eventKey);
        }
    }

    /// <summary>Signs an event exactly as it stands (after a test changed it).</summary>
    public SyncEvent Resign(SyncEvent evt)
    {
        evt.Signature = Ed25519Signer.Sign(_seed, EventSignature.BuildPayload(evt));
        return evt;
    }

    public WhitelistEntry Entry(string name, bool superadmin) => new()
    {
        NodeId = NodeId, DisplayName = name, Ed25519PublicKey = PublicKey, Status = "A", IsSuperadmin = superadmin,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private SyncEvent Stamp(SyncEvent evt)
    {
        evt.EventId = Guid.NewGuid();
        evt.NodeId = NodeId;
        evt.LamportTs = ++_lamport;
        evt.CreatedAt = DateTime.UtcNow;
        return evt;
    }

    private SyncEvent Sign(SyncEvent evt) => Resign(Stamp(evt));

    // ─── Payloads ───────────────────────────────────────────────────────────

    public static string FolderCreate(Guid folderId, string path, string name, string parent = "/") =>
        JsonSerializer.Serialize(new FolderCreatePayload(folderId, path, name, parent, DateTime.UtcNow, DateTime.UtcNow));

    public static string ArticleCreate(string title, string path, string blobHash) =>
        JsonSerializer.Serialize(new ArticleEventPayload(title, path, ["tag"], null,
            Convert.ToBase64String(new byte[12]), Convert.ToBase64String(new byte[60]), Convert.ToBase64String(new byte[12]),
            "A", DateTime.UtcNow, DateTime.UtcNow, CiphertextSha256: blobHash));
}
