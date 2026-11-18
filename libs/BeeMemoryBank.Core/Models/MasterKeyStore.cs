namespace BeeMemoryBank.Core.Models;

public class MasterKeyStore
{
    public int SlotId { get; set; }
    public string SlotType { get; set; } = "password";

    /// <summary>
    /// Door format 1: the master DEK wrapped under the KEK (48 or 49 bytes). Door format 2: the 93-byte DoorBox sealed to
    /// <see cref="DoorPublicKey"/> (docs/adr/0012-sealed-doors.md).
    /// </summary>
    public byte[] EncryptedMasterDek { get; set; } = [];

    /// <summary>The GCM nonce of a v1 wrap; for a v2 door a copy of the box nonce, not used to open.</summary>
    public byte[] IV { get; set; } = [];
    public byte[]? Salt { get; set; }
    public int? ArgonMemory { get; set; }
    public int? ArgonIterations { get; set; }
    public int? ArgonParallelism { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>1 = a wrap under the KEK (every release before 2.12), 2 = a sealed box (migration 046).</summary>
    public int DoorVersion { get; set; } = 1;

    /// <summary>The X25519 public key of a v2 door; null for v1.</summary>
    public byte[]? DoorPublicKey { get; set; }

    /// <summary>DekFingerprint of the master DEK the door yields, or null when unknown (a v1 door that has not opened since 2.12).</summary>
    public string? DekFingerprint { get; set; }

    /// <summary>The person the door belongs to (a <c>user</c> slot's account); null for recovery and machine doors written before 2.12.</summary>
    public int? OwnerUserId { get; set; }

    public int? CreatedByUserId { get; set; }

    public string? Label { get; set; }
}

/// <summary>The values of <c>tbl_key_slot.slot_type</c>. Each is also the door kind a v2 box is bound to.</summary>
public static class KeySlotTypes
{
    public const string User = "user";
    public const string Recovery = "recovery";
    /// <summary>The legacy single slot of a node from before the password unification. Never written as a v2 door.</summary>
    public const string Password = "password";
    public const string OsAutoUnlock = "os_auto_unlock";
    public const string ServerAutoUnlock = "server_auto_unlock";
    /// <summary>A secret-keyed door for a phone's quick unlock (2.12): the device keeps only the 32-byte secret.</summary>
    public const string QuickUnlock = "quick_unlock";
}
