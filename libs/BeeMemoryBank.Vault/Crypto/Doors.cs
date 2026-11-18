using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// The one place that opens and writes a door, whatever its format (docs/adr/0012-sealed-doors.md). A door is a key slot row or an
/// auto-unlock agent row; its secret derives a KEK as it always has (Argon2id of a password or recovery key, the auto-unlock secret, the
/// agent key's HKDF). Format 1 wraps the master DEK under the KEK (<see cref="MasterKeyManager"/>); format 2 seals it with a
/// <see cref="DoorBox"/> to the public key the KEK and the door salt derive. Every new door is v2; a v1 door is opened for as long as it exists.
/// </summary>
public static class Doors
{
    public const int V1 = 1;
    public const int V2 = 2;

    /// <summary>The door kind of an auto-unlock agent (the slot kinds are the slot types).</summary>
    public const string AgentKind = "agent";

    /// <summary>A v2 door as it is stored: the box, a copy of its nonce for the NOT NULL iv column, the public key and the DEK's fingerprint.</summary>
    public sealed record Sealed(byte[] Box, byte[] Iv, byte[] PublicKey, string DekFingerprint);

    /// <summary>
    /// Seals <paramref name="dek"/> as a v2 door of <paramref name="kind"/> for the secret whose KEK is <paramref name="kek"/> (with the door's
    /// <paramref name="salt"/>, the Argon2 salt of a password door, the agent salt, or none).
    /// </summary>
    public static Sealed Seal(string kind, byte[] kek, byte[]? salt, byte[] dek)
    {
        using var pair = DoorBox.DeriveKeyPair(kek, salt);
        return SealTo(kind, pair.PublicKey, dek);
    }

    /// <summary>Seals <paramref name="dek"/> to an existing v2 door's public key (no secret needed).</summary>
    public static Sealed SealTo(string kind, byte[] doorPublicKey, byte[] dek)
    {
        var box = DoorBox.Seal(doorPublicKey, kind, dek);
        return new Sealed(box, box.AsSpan(1 + DoorBox.PublicKeySize, DoorBox.NonceSize).ToArray(), (byte[])doorPublicKey.Clone(), DekFingerprint.Of(dek));
    }

    /// <summary>
    /// Opens a door with its KEK. <paramref name="doorVersion"/> 2 requires a well-formed box (and, when the row names one, the public key the
    /// KEK derives); 1 is the wrap of every earlier release. Throws <see cref="CryptographicException"/> when the KEK is not this door's, as
    /// <see cref="MasterKeyManager.UnwrapMasterDek"/> always has. The caller owns and clears the DEK.
    /// </summary>
    public static byte[] Open(int doorVersion, string kind, byte[] content, byte[] iv, byte[] kek, byte[]? salt, byte[]? doorPublicKey)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(kek);
        if (doorVersion == V2)
        {
            if (!DoorBox.IsWellFormed(content)) throw new CryptographicException("The door is not a well-formed sealed box.");
            using var pair = DoorBox.DeriveKeyPair(kek, salt);
            if (doorPublicKey is not null
                && (doorPublicKey.Length != DoorBox.PublicKeySize || !CryptographicOperations.FixedTimeEquals(doorPublicKey, pair.PublicKey)))
                throw new CryptographicException("The door does not open with this key.");
            return DoorBox.TryOpen(pair.SecretKey, kind, content) ?? throw new CryptographicException("The door does not open with this key.");
        }
        if (doorVersion != V1) throw new CryptographicException("Unknown door format.");
        // A v1 row never holds a box: a 93-byte content with a v1 label is refused by the wrap's own length check.
        return MasterKeyManager.UnwrapMasterDek(content, iv, kek);
    }

    /// <summary>
    /// Opens a slot read from a database that may predate migration 046 (a snapshot, a backup, the database of an offline re-key): the format is
    /// told by the content's shape, which is unambiguous (a v1 wrap is 48 or 49 bytes, a v2 box 93 bytes starting with 0x02).
    /// </summary>
    public static byte[] OpenByShape(string slotType, byte[] content, byte[] iv, byte[] kek, byte[]? salt) =>
        Open(DoorBox.IsWellFormed(content) ? V2 : V1, slotType, content, iv, kek, salt, null);

    /// <summary><see cref="Open(int, string, byte[], byte[], byte[], byte[], byte[])"/> for a key slot row; the kind is its slot type.</summary>
    public static byte[] OpenSlot(MasterKeyStore slot, byte[] kek)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return Open(slot.DoorVersion, slot.SlotType, slot.EncryptedMasterDek, slot.IV, kek, slot.Salt, slot.DoorPublicKey);
    }

    /// <summary>Opens a door copy of the boot store with its KEK (a slot door: the kind is its slot type; an agent door: <see cref="AgentKind"/>).</summary>
    public static byte[] OpenBootDoor(BeeMemoryBank.Storage.Boot.BootDoor door, byte[] kek)
    {
        ArgumentNullException.ThrowIfNull(door);
        var kind = door.Kind == BeeMemoryBank.Storage.Boot.BootDoorKinds.Agent ? AgentKind : door.SlotType
            ?? throw new CryptographicException("A slot door without a type.");
        return Open(door.DoorVersion, kind, door.EncryptedMasterDek, door.Iv, kek, door.Salt, door.DoorPublicKey);
    }

    /// <summary>The slot row <paramref name="slot"/> with its door replaced by <paramref name="door"/> (type, owner, label and id kept).</summary>
    public static MasterKeyStore WithDoor(MasterKeyStore slot, Sealed door) => new()
    {
        SlotId = slot.SlotId,
        SlotType = slot.SlotType,
        EncryptedMasterDek = door.Box,
        IV = door.Iv,
        Salt = slot.Salt,
        ArgonMemory = slot.ArgonMemory,
        ArgonIterations = slot.ArgonIterations,
        ArgonParallelism = slot.ArgonParallelism,
        CreatedAt = slot.CreatedAt,
        DoorVersion = V2,
        DoorPublicKey = door.PublicKey,
        DekFingerprint = door.DekFingerprint,
        OwnerUserId = slot.OwnerUserId,
        CreatedByUserId = slot.CreatedByUserId,
        Label = slot.Label,
    };

    /// <summary>A new v2 slot of <paramref name="slotType"/> for an Argon2id-derived KEK (a password or a recovery key).</summary>
    public static MasterKeyStore NewPasswordSlot(string slotType, byte[] kek, byte[] salt, int argonMemory, int argonIterations, int argonParallelism,
        byte[] dek, int? ownerUserId = null, int? createdByUserId = null, string? label = null)
    {
        var door = Seal(slotType, kek, salt, dek);
        return new MasterKeyStore
        {
            SlotType = slotType,
            EncryptedMasterDek = door.Box,
            IV = door.Iv,
            Salt = salt,
            ArgonMemory = argonMemory,
            ArgonIterations = argonIterations,
            ArgonParallelism = argonParallelism,
            CreatedAt = DateTime.UtcNow,
            DoorVersion = V2,
            DoorPublicKey = door.PublicKey,
            DekFingerprint = door.DekFingerprint,
            OwnerUserId = ownerUserId,
            CreatedByUserId = createdByUserId,
            Label = label,
        };
    }

    /// <summary>A new v2 slot of a secret-keyed type (os/server auto-unlock, quick unlock): the 32-byte secret is the KEK, no salt.</summary>
    public static MasterKeyStore NewSecretSlot(string slotType, byte[] secret, byte[] dek, int? ownerUserId = null, int? createdByUserId = null,
        string? label = null)
    {
        var door = Seal(slotType, secret, null, dek);
        return new MasterKeyStore
        {
            SlotType = slotType,
            EncryptedMasterDek = door.Box,
            IV = door.Iv,
            CreatedAt = DateTime.UtcNow,
            DoorVersion = V2,
            DoorPublicKey = door.PublicKey,
            DekFingerprint = door.DekFingerprint,
            OwnerUserId = ownerUserId,
            CreatedByUserId = createdByUserId,
            Label = label,
        };
    }

    /// <summary>True when a slot row is a door the upgrade of ADR 0012 rewrites (v1 and not the legacy single slot).</summary>
    public static bool NeedsUpgrade(MasterKeyStore slot) => slot.DoorVersion != V2 && slot.SlotType != KeySlotTypes.Password;

    // --- agents -------------------------------------------------------------------------------------------------------------------------

    /// <summary>The KEK of an agent door: the HKDF of kdf_version 1 (kept for v2). The caller clears it.</summary>
    public static byte[] AgentKek(string apiKey, byte[] salt) => AgentKeyHelper.DeriveEncryptionKeyV1(apiKey, salt);

    /// <summary>
    /// Opens the wrapped or sealed DEK of an auto-unlock agent with its API key: kdf_version 0 and 1 as before, 2 a sealed door. Throws
    /// <see cref="CryptographicException"/> for a wrong key, a missing salt or an unknown version. The caller clears the DEK.
    /// </summary>
    public static byte[] OpenAgent(Agent agent, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(apiKey);
        if (!agent.CanAutoUnlock) throw new CryptographicException("The agent holds no key.");
        return agent.KdfVersion switch
        {
            0 => AgentKeyHelper.DecryptDek(apiKey, agent.EncryptedDek!, agent.DekIV!),
            1 when agent.Salt is not null => AgentKeyHelper.DecryptDekV1(apiKey, agent.EncryptedDek!, agent.DekIV!, agent.Salt),
            Agent.SealedDoorKdfVersion when agent.Salt is not null => OpenAgentV2(agent, apiKey),
            _ => throw new CryptographicException("The agent's key format is not known."),
        };
    }

    private static byte[] OpenAgentV2(Agent agent, string apiKey)
    {
        var kek = AgentKek(apiKey, agent.Salt!);
        try
        {
            return Open(V2, AgentKind, agent.EncryptedDek!, agent.DekIV!, kek, agent.Salt, agent.DoorPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>A new sealed agent door for <paramref name="apiKey"/>: a fresh 16-byte salt, the KEK of kdf_version 1, the box.</summary>
    public static (Sealed Door, byte[] Salt) SealAgent(string apiKey, byte[] dek)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (SealAgent(apiKey, salt, dek), salt);
    }

    /// <summary>A sealed agent door for <paramref name="apiKey"/> under an existing salt.</summary>
    public static Sealed SealAgent(string apiKey, byte[] salt, byte[] dek)
    {
        var kek = AgentKek(apiKey, salt);
        try
        {
            return Seal(AgentKind, kek, salt, dek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }
}
