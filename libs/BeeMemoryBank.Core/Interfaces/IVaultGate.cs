namespace BeeMemoryBank.Core.Interfaces;

/// <summary>Where a full node's database stands (docs/design/step-b-spec.md 5.1).</summary>
public enum VaultState
{
    /// <summary>Not sealed: a plain database, opened at start as before Step B. Every blind host and every unmigrated node.</summary>
    Plain,
    /// <summary>Sealed and closed: no database key in memory; only the boot store and the relay inbox are used.</summary>
    Closed,
    /// <summary>A door produced the master DEK; the database is being opened, migrated and checked. Requests still see Closed.</summary>
    Opening,
    /// <summary>Open; events received while closed are being replayed. Content routes work; sync still goes to the inbox.</summary>
    Importing,
    /// <summary>Open: everything works as on a plain node.</summary>
    Open,
    /// <summary>A lock is closing it: no new connection, waiting for the open ones.</summary>
    Closing,
}

/// <summary>What <see cref="IVaultGate.OpenAsync"/> did. <see cref="Refusal"/> is a code, never key material or a name.</summary>
public sealed record VaultOpenResult(bool Opened, string? Refusal = null)
{
    public static readonly VaultOpenResult Done = new(true);
    public static VaultOpenResult Refused(string code) => new(false, code);
}

/// <summary>Refusal codes of <see cref="VaultOpenResult"/> (spec 4.4).</summary>
public static class VaultOpenRefusals
{
    public const string NoWrapper = "no_wrapper";
    public const string KeyMismatch = "key_mismatch";
    public const string Broken = "store_broken";
    public const string DoorNotAuthorized = "door_not_authorized";
    /// <summary>A diagnosis, never a refusal on its own: the database is older than its boot store (spec 4.4 step 5).</summary>
    public const string StoreStale = "store_stale";
    /// <summary>The gate is held closed for a file copy (spec 5.10); the caller may try again in a few seconds.</summary>
    public const string Busy = "busy";
    /// <summary>A replaced plain file (<c>*.seal-old*</c>) of an "Encrypt this node" is still readable on the disk: the database is not opened until it is gone.</summary>
    public const string RepairRequired = "repair_required";
}

/// <summary>
/// The sealed database's lifecycle (spec 5). One per host. On a plain node every member answers as if the database were always open:
/// <see cref="State"/> is <see cref="VaultState.Plain"/>, <see cref="OpenAsync"/> returns <see cref="VaultOpenResult.Done"/>, nothing closes.
/// </summary>
public interface IVaultGate
{
    VaultState State { get; }

    /// <summary>True when this node's database is sealed (it was migrated), in any state.</summary>
    bool IsSealed { get; }

    /// <summary>True when the vault database can be used: <see cref="VaultState.Plain"/>, <see cref="VaultState.Importing"/> or <see cref="VaultState.Open"/>.</summary>
    bool IsUsable { get; }

    /// <summary>True while a <see cref="HoldClosedAsync"/> lease lives. The unlock routes check it first and answer 503 <c>vault_busy</c>.</summary>
    bool IsHeld { get; }

    /// <summary>
    /// Opens a sealed database with a master DEK one door produced (spec 4.4 steps 3-6). The caller keeps <paramref name="masterDek"/>;
    /// the gate copies what it needs and clears its copies. Already open: returns <see cref="VaultOpenResult.Done"/>.
    /// </summary>
    Task<VaultOpenResult> OpenAsync(byte[] masterDek, CancellationToken ct = default);

    /// <summary>
    /// Opens the database for one maintenance action without unlocking the session (recover-access, a CLI verb, spec 5.6); disposing the
    /// handle closes it again only if this call opened it.
    /// </summary>
    Task<IAsyncDisposable> OpenTemporarilyAsync(byte[] masterDek, CancellationToken ct = default);

    /// <summary>After the session holds the DEK: replays the inbox and switches to <see cref="VaultState.Open"/> (spec 5.3 step 9).</summary>
    Task CompleteOpenAsync(CancellationToken ct = default);

    /// <summary>
    /// Keeps a <see cref="VaultState.Closed"/> database closed for the seconds a file copy takes (spec 5.10, 8.5): while the lease lives,
    /// <see cref="OpenAsync"/> returns <see cref="VaultOpenRefusals.Busy"/> and <see cref="OpenTemporarilyAsync"/> throws
    /// <see cref="VaultBusyException"/>. Waits up to 30 s for an unlock in progress; throws <see cref="InvalidOperationException"/> unless the
    /// state is then Closed (the caller takes a snapshot instead). On a plain node it always throws.
    /// </summary>
    Task<IAsyncDisposable> HoldClosedAsync(string reason, CancellationToken ct = default);

    /// <summary>Closes the database (spec 5.4). Called on SessionService.Locked and at shutdown. Plain: nothing.</summary>
    Task CloseAsync(string reason, CancellationToken ct = default);

    /// <summary>Completes when the database can be used; at once on a plain node.</summary>
    Task WaitUsableAsync(CancellationToken ct);

    event Action<VaultState>? StateChanged;
}

/// <summary>Thrown instead of opening a sealed database that is closed. Maps to 503 <c>vault_sealed</c>.</summary>
public sealed class VaultSealedException(string message = "This node's database is encrypted and closed: unlock it first.")
    : InvalidOperationException(message);

/// <summary>The gate is held closed for a file copy (spec 5.10). Maps to 503 <c>vault_busy</c> with Retry-After.</summary>
public sealed class VaultBusyException(string message = "This node is being backed up for a few seconds: try again shortly.")
    : InvalidOperationException(message);

/// <summary>The database key does not open the database file (wrong key, foreign boot store, damaged file). Never retried as plain.</summary>
public sealed class VaultKeyMismatchException(string message) : InvalidOperationException(message);

/// <summary>Work a host runs each time a sealed database opens, before requests may use it (spec 5.3).</summary>
public interface IVaultOpenStep
{
    /// <summary>Ascending; the steps of spec 5.3 table 2 use 100, 200, ...</summary>
    int Order { get; }

    /// <param name="firstOpenInProcess">True only for the first open of this process: crash sweeps run once.</param>
    Task RunAsync(bool firstOpenInProcess, CancellationToken ct);
}
