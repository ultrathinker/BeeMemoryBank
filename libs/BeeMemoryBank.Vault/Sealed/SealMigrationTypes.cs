using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>One reason "Encrypt this node" cannot start (docs/design/step-b-spec.md 10.2): a stable code and a sentence for the owner.</summary>
public sealed record SealBlocker(string Code, string Message);

/// <summary>A non-blocking remark of the preflight (spec 10.2, 11.2 item 1).</summary>
public sealed record SealWarning(string Code, string Message);

/// <summary>The codes of <see cref="SealBlocker"/> (spec 10.2).</summary>
public static class SealBlockers
{
    public const string NotFullNode = "not_full_node";
    public const string AlreadySealed = "already_sealed";
    public const string VaultLocked = "vault_locked";
    public const string NotSuperadmin = "not_superadmin";
    public const string NotActivated = "not_activated";
    public const string EventsHeld = "events_held";
    public const string RotationPending = "rotation_pending";
    public const string RestoreRunning = "restore_running";
    public const string IdentityV0 = "identity_v0";
    public const string Integrity = "integrity";
    public const string FreeSpace = "free_space";
    public const string SnapshotLimit = "snapshot_limit";
    public const string Engine = "engine";
    public const string UncPath = "unc_path";
    public const string PlainCopies = "plain_copies";
    public const string MobileForeground = "mobile_foreground";
    public const string PhoneNoCopy = "phone_no_copy";
    public const string MigrationRunning = "migration_running";
}

/// <summary>The failure codes of a migration (spec 10.9).</summary>
public static class SealFailures
{
    public const string Snapshot = "snapshot";
    public const string PlainCopies = "plain_copies";
    public const string VerifyIntegrity = "verify_integrity";
    public const string VerifyCounts = "verify_counts";
    public const string VerifySentinel = "verify_sentinel";
    public const string VerifyBodies = "verify_bodies";
    public const string VerifyTree = "verify_tree";
    public const string VerifyScan = "verify_scan";
    public const string VerifyBoot = "verify_boot";
    public const string Wipe = "wipe";
    public const string Io = "io";
    public const string Cancelled = "cancelled";
}

/// <summary>A migration step failed with a code of <see cref="SealFailures"/>; the node is unchanged.</summary>
public sealed class SealMigrationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <param name="Blockers">Empty when the migration may start.</param>
/// <param name="Warnings">Non-blocking.</param>
/// <param name="PlainCopies">The findings of <see cref="PlainVaultCopyScanner"/> in mode Preflight (the blocker <c>plain_copies</c> lists them).</param>
/// <param name="DatabaseBytes">Size of the vault database and its WAL, for the page.</param>
public sealed record SealPreflightResult(
    IReadOnlyList<SealBlocker> Blockers,
    IReadOnlyList<SealWarning> Warnings,
    IReadOnlyList<PlainCopyFinding> PlainCopies,
    long DatabaseBytes)
{
    public bool CanStart => Blockers.Count == 0;
}

/// <summary>What the owner ticked for a start (spec 10.2 <c>snapshot_limit</c>, 10.8 <c>phone_no_copy</c>).</summary>
/// <param name="AcceptNoSnapshot">"I keep my own offline backup of this node; encrypting cannot be undone from the node itself": the way-back snapshot is skipped.</param>
public sealed record SealOptions(bool CallerIsSuperadmin = true, bool AcceptNoSnapshot = false);

/// <summary>The way-back snapshot of a node (spec 10.7): its name inside the data folder, size and date. Never in the unauthenticated status.</summary>
public sealed record WayBackSnapshot(string RelativeName, long Bytes, DateTime CreatedUtc);

/// <summary>What the page polls (spec 10.9).</summary>
public sealed record SealProgress(
    string Phase,
    int Percent,
    string Step,
    string? ErrorCode = null,
    string? Error = null,
    WayBackSnapshot? Snapshot = null,
    IReadOnlyList<string>? PlainCopiesFound = null)
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>
/// What a host adds to the migration: the way-back snapshot (the snapshot service is the Api's, a phone has none) and the blockers only the host
/// knows (a phone's foreground and battery).
/// </summary>
public interface ISealMigrationHost
{
    /// <summary>True when this host can make the way-back snapshot (spec 10.4 step 3); a phone cannot (10.8).</summary>
    bool CanMakeWayBackSnapshot { get; }

    /// <summary>True when a database of this size fits what the way-back snapshot can hold (spec 10.2 <c>snapshot_limit</c>).</summary>
    bool FitsWayBackSnapshot(long databaseBytes);

    /// <summary>
    /// The pre-encryption snapshot with its key envelope, written to <c>&lt;data&gt;/pre-encryption/</c>, its staging copy wiped, and self-checked
    /// with <paramref name="masterPassword"/> (spec 10.4 step 3). Throws <see cref="SealMigrationException"/> with code <c>snapshot</c> and deletes
    /// the file on failure.
    /// </summary>
    Task<WayBackSnapshot> CreateWayBackSnapshotAsync(string masterPassword, CancellationToken ct);

    /// <summary>Blockers that depend on the host (spec 10.2 <c>mobile_foreground</c>, 10.8 <c>phone_no_copy</c>).</summary>
    Task<IReadOnlyList<SealBlocker>> HostBlockersAsync(SealOptions options, CancellationToken ct);

    /// <summary>Non-blocking remarks only the host knows (spec 10.2 <c>auto_unlock_on</c>: the owner had "start unlocked after a restart" on).</summary>
    Task<IReadOnlyList<SealWarning>> HostWarningsAsync(CancellationToken ct);
}
