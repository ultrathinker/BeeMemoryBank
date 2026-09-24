namespace BeeMemoryBank.Core.Models;

public class Agent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string KeyPrefix { get; set; } = "";
    public string KeyHash { get; set; } = "";

    /// <summary>
    /// Wrapped master DEK. SECURITY: populated ONLY for agents owned by a superadmin
    /// (AgentEndpoints/AgentCommand). A wrapped DEK makes the agent key, together with any copy
    /// of the database file (a backup, a decommissioned disk), cryptographically a key to the
    /// WHOLE vault, whatever the owner's folder ACL allows in software. Superadmins can already
    /// unlock the vault through the web UI, so their agents gain no new capability. An ordinary
    /// (folder-restricted, self-service) user's agent gets null: it works whenever the vault is
    /// already unlocked, it just can't unlock it, and a stolen database file yields nothing from
    /// its row alone. See <see cref="CanAutoUnlock"/>,
    /// AgentAuthMiddleware, and migration 014_agent_dek_optional.sql (which strips this from
    /// every pre-existing non-superadmin agent).
    /// </summary>
    public byte[]? EncryptedDek { get; set; }
    public byte[]? DekIV { get; set; }

    /// <summary>V0 = SHA256(key + "bmb-encrypt"), V1 = HKDF-SHA256(key, salt, info)</summary>
    public int KdfVersion { get; set; }
    public byte[]? Salt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public int RequestCount { get; set; }
    public string Status { get; set; } = "A";

    public int OwnerUserId { get; set; }

    /// <summary>
    /// True when this row carries wrapped master-DEK key material and can therefore auto-unlock
    /// a locked vault (AgentAuthMiddleware). False for every agent owned by a non-superadmin, by
    /// construction -- see the comment on <see cref="EncryptedDek"/>. Treats a present-but-empty
    /// blob the same as a missing one; EncryptDekV1 never actually produces one, but treating
    /// "no usable key material" uniformly is one less way to get this wrong.
    /// </summary>
    public bool CanAutoUnlock => EncryptedDek is { Length: > 0 } && DekIV is { Length: > 0 };
}
