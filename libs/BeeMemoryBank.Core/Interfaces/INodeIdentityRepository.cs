using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface INodeIdentityRepository
{
    Task<NodeIdentity?> GetAsync();
    Task CreateAsync(NodeIdentity identity);
    Task StoreSentinelAsync(byte[] sentinelValue);
    /// <summary>
    /// Stores the sentinel only while the row has none (NULL or empty) and says whether it did. A vault older than the sentinel has none;
    /// the unlock that heals it must not overwrite one a rotation wrote in the meantime.
    /// </summary>
    Task<bool> StoreSentinelIfMissingAsync(byte[] sentinelValue);
    Task<byte[]?> GetSentinelAsync();
    Task MarkInitialSyncCompletedAsync();

    /// <summary>
    /// Migrates a legacy v=0 (plaintext) ed25519_private_key row to v=1 (encrypted under master DEK).
    /// Idempotent: only updates rows where v=0; no-op if v=1 already.
    /// </summary>
    Task UpgradePrivateKeyToV1Async(Guid nodeId, byte[] wrappedPrivateKey, byte[] iv);

    /// <summary>
    /// Admin-configurable web login cookie lifetime. Falls back to (48, true) if the node
    /// identity row doesn't exist yet (pre-init).
    /// </summary>
    Task<(int ExpireHours, bool SlidingExpiration)> GetSessionSettingsAsync();
    Task SetSessionSettingsAsync(int expireHours, bool slidingExpiration);

    /// <summary>
    /// The pending "the master password was changed somewhere else" notice, or null when this node
    /// is in step with the mesh. Key slots are node-local, so a password change on one node leaves
    /// every other node still accepting the old password — see migration 018.
    /// </summary>
    Task<(DateTime ChangedAt, string ByNode)?> GetMasterPasswordNoticeAsync();

    /// <summary>Records the notice. <paramref name="byNode"/> is a display name, for the operator.</summary>
    Task SetMasterPasswordNoticeAsync(DateTime changedAt, string byNode);

    /// <summary>Clears the notice — called when this node's own password is changed.</summary>
    Task ClearMasterPasswordNoticeAsync();

    /// <summary>
    /// When this node last changed its own master password, or null if it never has (or the node
    /// predates migration 019). Read by the applier to drop a peer notice that describes a change
    /// this node has already moved past.
    /// </summary>
    Task<DateTime?> GetMasterPasswordChangedLocallyAtAsync();

    /// <summary>Records that this node changed its own master password at <paramref name="at"/>.</summary>
    Task SetMasterPasswordChangedLocallyAtAsync(DateTime at);

    /// <summary>
    /// Admin-configurable product name for the web header / tab title, or null when the node
    /// has never set one (the caller then falls back to <see cref="Models.Branding.DefaultName"/>).
    /// Node-local: never synced, so each installation can brand itself independently.
    /// </summary>
    Task<string?> GetBrandNameAsync();

    /// <summary>Stores a custom name; null or blank clears the override and restores the default.</summary>
    Task SetBrandNameAsync(string? brandName);

    /// <summary>
    /// Admin-configurable toggle for whether THIS node generates its own embeddings
    /// (gates <see cref="BeeMemoryBank.Sync.PendingEmbeddingProcessor"/>), changeable after
    /// node init -- see <see cref="Models.NodeIdentity.CanGenerateEmbeddings"/>.
    /// </summary>
    Task SetCanGenerateEmbeddingsAsync(bool enabled);

    /// <summary>
    /// Whether this node still owes its first administrator the blocking recovery-key step: set by
    /// /api/init/standalone and /api/init/join, cleared only by an acknowledgement. A node that existed
    /// before release 2.7.5 answers false, so an upgrade never gates anybody. Node-local, never synced.
    /// </summary>
    Task<bool> GetRecoveryKeyRequiredAsync();

    /// <summary>Records that a new node owes the recovery-key step (one-way; there is no reason to unset it).</summary>
    Task SetRecoveryKeyRequiredAsync(bool required);

    /// <summary>When a superadmin confirmed they saved the recovery key this node showed, or null.</summary>
    Task<DateTime?> GetRecoveryKeyAcknowledgedAtAsync();

    /// <summary>Records the confirmation; a second call keeps the first instant.</summary>
    Task SetRecoveryKeyAcknowledgedAtAsync(DateTime acknowledgedAt);

    /// <summary>When an operator dismissed the "no recovery key has been issued" notice, or null.</summary>
    Task<DateTime?> GetRecoveryKeyNoticeDismissedAtAsync();

    /// <summary>Records the dismissal of the no-recovery-key notice.</summary>
    Task SetRecoveryKeyNoticeDismissedAtAsync(DateTime dismissedAt);
}
