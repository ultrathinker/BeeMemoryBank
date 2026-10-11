using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// One sealed file of a node as the gate closes it: the factory that holds its connections (it quiesces, drains and flushes them), a way to
/// open one connection for the checkpoint and the test query, and the file itself (docs/design/step-b-spec.md 5.4, 7.2).
/// </summary>
/// <param name="Purpose"><see cref="DbPurposes.Vault"/> or <see cref="DbPurposes.Chat"/>.</param>
/// <param name="FilePath">The database file; a file that does not exist (a node without chat history) is not checkpointed or tested.</param>
public sealed record SealedFile(string Purpose, string FilePath, ISealableConnections Connections, Func<IDbConnection> Open);

/// <summary>
/// The sealed database's lifecycle (docs/design/step-b-spec.md 5): states, open, close, temporary opens, the hold for a file copy. It is also
/// the page-key source of the keyed connection factories (<see cref="IVaultKeySource"/>) and the holder of the database key the mirror's
/// wrappers are made from (<see cref="ISealedVaultKeys"/>). On a plain node (<see cref="VaultStoreMode.Plain"/>) every member answers as if the
/// database were always open; the migration of section 10 turns a plain gate into a sealed one with <see cref="SwitchToSealed"/>.
/// <para>
/// A locked node holds no key: the database key, both page keys and every copy of them exist only between <see cref="OpenAsync"/> and the end of
/// <see cref="CloseAsync"/>. The state is published as Closed only after the connections of the generation are disposed, the pools flushed and
/// the keys zeroed (spec 3.3, 5.4).
/// </para>
/// </summary>
public sealed class VaultGate : IVaultGate, IVaultKeySource, ISealedVaultKeys, ISealedUnlockDiagnostics, IDisposable
{
    /// <summary>The longest a close waits for the connections still open (spec 5.4 step 3).</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    public const string ShutdownReason = "shutdown";

    /// <summary>The close of a node reset (spec 13.1): the boot store is about to be deleted, so it is not rewritten.</summary>
    public const string ResetReason = "reset";

    private readonly BootStore _boot;
    private readonly IDatabaseKeyService _keyService;
    private readonly Func<IReadOnlyList<SealedFile>> _files;
    private readonly Func<IReadOnlyList<IVaultOpenStep>> _steps;
    private readonly Func<ISealedRelayLifecycle?> _relay;
    private readonly Func<IUnlockMaterialMirror?> _mirror;
    private readonly ILogger? _logger;
    private readonly TimeSpan _drainTimeout;

    // The state fields all live under _stateLock. _transition serialises open, close and hold (one at a time, spec 5.3).
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private VaultStoreMode _mode;
    private VaultState _state;
    private byte[]? _dbk;
    private byte[]? _vaultKey;
    private byte[]? _chatKey;
    private long _generation;
    private int _dbkGeneration;
    private bool _held;
    private int _tempLeases;
    private bool _sessionClaim;
    private bool _openedOnce;
    private volatile string? _lastRefusal;
    private TaskCompletionSource _usable = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public VaultGate(BootStore boot, IDatabaseKeyService keyService, Func<IReadOnlyList<SealedFile>> files, Func<IReadOnlyList<IVaultOpenStep>> steps,
        Func<ISealedRelayLifecycle?>? relay = null, Func<IUnlockMaterialMirror?>? mirror = null, ILogger<VaultGate>? logger = null,
        VaultStoreMode mode = VaultStoreMode.Sealed, TimeSpan? drainTimeout = null)
    {
        _boot = boot;
        _keyService = keyService;
        _files = files;
        _steps = steps;
        _relay = relay ?? (() => null);
        _mirror = mirror ?? (() => null);
        _logger = logger;
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        _mode = mode == VaultStoreMode.Broken ? throw new ArgumentException("A broken directory has no gate.", nameof(mode)) : mode;
        _state = mode == VaultStoreMode.Plain ? VaultState.Plain : VaultState.Closed;
        if (IsUsableState(_state)) _usable.TrySetResult();
    }

    /// <summary>A gate for a node whose database is plain (a blind host never has one). It can be switched to sealed by the migration.</summary>
    public static VaultGate ForPlainNode(BootStore boot, IDatabaseKeyService keyService, Func<IReadOnlyList<SealedFile>> files,
        Func<IReadOnlyList<IVaultOpenStep>> steps, Func<ISealedRelayLifecycle?>? relay = null, Func<IUnlockMaterialMirror?>? mirror = null,
        ILogger<VaultGate>? logger = null) =>
        new(boot, keyService, files, steps, relay, mirror, logger, VaultStoreMode.Plain);

    /// <summary>Test seam: runs on every key buffer after it was zeroed (docs/design/step-b-spec.md 3.3, red test 15.3.5).</summary>
    internal Action<byte[]>? Cleared { get; set; }

    /// <summary>
    /// Asks, before every open that would start one, for the plain files of an unfinished "Encrypt this node" that are still readable on the disk
    /// (it tries to erase them first and returns what is left). An open is refused with <see cref="VaultOpenRefusals.RepairRequired"/> while the list
    /// is not empty (review round 1, H1). Not set on a node that has no migration.
    /// </summary>
    public Func<IReadOnlyList<string>>? PlainLeftovers { get; set; }

    public event Action<VaultState>? StateChanged;

    // ---- IVaultKeySource ------------------------------------------------------------------------------------------------------------

    public VaultStoreMode Mode
    {
        get { lock (_stateLock) return _mode; }
    }

    public bool IsOpen
    {
        get { lock (_stateLock) return _vaultKey is not null; }
    }

    public long OpenGeneration
    {
        get { lock (_stateLock) return _generation; }
    }

    public byte[] GetPageKey(string purpose)
    {
        lock (_stateLock)
        {
            var key = purpose switch
            {
                DbPurposes.Vault => _vaultKey,
                DbPurposes.Chat => _chatKey,
                _ => throw new ArgumentException("Unknown purpose.", nameof(purpose)),
            };
            if (key is null) throw new VaultSealedException();
            return (byte[])key.Clone();
        }
    }

    // ---- ISealedVaultKeys -------------------------------------------------------------------------------------------------------------

    public byte[] GetDatabaseKey()
    {
        lock (_stateLock)
        {
            if (_dbk is null) throw new VaultSealedException();
            return (byte[])_dbk.Clone();
        }
    }

    public int DbkGeneration
    {
        get { lock (_stateLock) return _dbkGeneration; }
    }

    // ---- IVaultGate -------------------------------------------------------------------------------------------------------------------

    public VaultState State
    {
        get { lock (_stateLock) return _mode == VaultStoreMode.Plain ? VaultState.Plain : _state; }
    }

    public bool IsSealed
    {
        get { lock (_stateLock) return _mode == VaultStoreMode.Sealed; }
    }

    public bool IsUsable
    {
        get { lock (_stateLock) return _mode == VaultStoreMode.Plain || IsUsableState(_state); }
    }

    public bool IsHeld
    {
        get { lock (_stateLock) return _held; }
    }

    /// <summary>The code of the last refused unlock (spec 4.4, 6.7), or null. Never a name or a key.</summary>
    public string? LastRefusal => _lastRefusal;

    private static bool IsUsableState(VaultState state) => state is VaultState.Plain or VaultState.Importing or VaultState.Open;

    public async Task<VaultOpenResult> OpenAsync(byte[] masterDek, CancellationToken ct = default) =>
        await OpenCoreAsync(masterDek, temporary: false, ct).ConfigureAwait(false);

    public async Task<IAsyncDisposable> OpenTemporarilyAsync(byte[] masterDek, CancellationToken ct = default)
    {
        if (!IsSealed || IsUsable) return NoLease.Instance;   // already open for the session (or nothing to open): there is nothing to close afterwards
        if (IsHeld) throw new VaultBusyException();

        var result = await OpenCoreAsync(masterDek, temporary: true, ct).ConfigureAwait(false);
        if (!result.Opened)
        {
            if (result.Refusal == VaultOpenRefusals.Busy) throw new VaultBusyException();
            if (result.Refusal == VaultOpenRefusals.KeyMismatch) throw new VaultKeyMismatchException("The database key does not open this node's database.");
            throw new VaultSealedException("The database could not be opened with this key (" + result.Refusal + ").");
        }
        return new TemporaryLease(this);
    }

    private async Task<VaultOpenResult> OpenCoreAsync(byte[] masterDek, bool temporary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(masterDek);
        if (!IsSealed) return VaultOpenResult.Done;
        if (IsHeld) return VaultOpenResult.Refused(VaultOpenRefusals.Busy);

        await _transition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsHeld) return VaultOpenResult.Refused(VaultOpenRefusals.Busy);

            VaultState state;
            lock (_stateLock) state = _state;

            if (state is VaultState.Importing or VaultState.Open)
                return VaultOpenResult.Done;

            if (state == VaultState.Closing)
            {
                // A close that threw before it could publish Closed (a generation that could not be ended, review round 1 H4): it is finished first,
                // so that no open ever starts over a generation that is still alive. A second failure reaches the caller and the state stays Closing.
                await CloseCoreAsync("retry", rewrite: false, relayClosing: false, ct).ConfigureAwait(false);
            }

            if (state == VaultState.Opening)
            {
                // Already open for a temporary action or for an unlock in progress: this key must still be one the boot store accepts.
                var other = await _keyService.TryUnwrapAsync(masterDek, ct).ConfigureAwait(false);
                if (other is null) return VaultOpenResult.Refused(VaultOpenRefusals.NoWrapper);
                Zero(other);
                lock (_stateLock)
                {
                    if (temporary) _tempLeases++;
                    else _sessionClaim = true;
                }
                return VaultOpenResult.Done;
            }

            // A replaced plain file of an "Encrypt this node" that is still readable: nothing is unwrapped, nothing opens (review round 1, H1).
            if (PlainLeftovers?.Invoke() is { Count: > 0 } leftovers)
            {
                _logger?.LogWarning("The database is not opened: a replaced unencrypted file is still readable on the disk ({Files}).", string.Join(", ", leftovers));
                return VaultOpenResult.Refused(VaultOpenRefusals.RepairRequired);
            }

            SetState(VaultState.Opening);
            byte[]? dbk = null;
            try
            {
                dbk = await _keyService.TryUnwrapAsync(masterDek, ct).ConfigureAwait(false);
                if (dbk is null)
                {
                    SetState(VaultState.Closed);
                    return VaultOpenResult.Refused(VaultOpenRefusals.NoWrapper);
                }

                var generation = int.TryParse(await _boot.GetMetaAsync(BootMetaKeys.DbkGeneration, ct).ConfigureAwait(false), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var g) ? g : 1;
                var vaultKey = _keyService.DerivePageKey(dbk, DbPurposes.Vault);
                var chatKey = _keyService.DerivePageKey(dbk, DbPurposes.Chat);
                lock (_stateLock)
                {
                    _dbk = dbk;
                    dbk = null;   // owned by the gate now
                    _vaultKey = vaultKey;
                    _chatKey = chatKey;
                    _dbkGeneration = generation;
                    _generation++;
                    _sessionClaim = !temporary;
                    _tempLeases = temporary ? 1 : 0;
                }

                try
                {
                    TestQuery();
                }
                catch (VaultKeyMismatchException)
                {
                    _logger?.LogError("The boot store does not belong to this database: the database key from the boot store does not open it.");
                    await CloseCoreAsync("key_mismatch", rewrite: false, relayClosing: false, ct).ConfigureAwait(false);
                    return VaultOpenResult.Refused(VaultOpenRefusals.KeyMismatch);
                }

                await RunOpenStepsAsync(ct).ConfigureAwait(false);
                if (_relay() is { } relay) await relay.OnOpenedAsync(ct).ConfigureAwait(false);
                return VaultOpenResult.Done;   // still Opening: no request is served until the unlock accepted this door
            }
            catch
            {
                // A failing open step, a cancelled open: nothing stays open, no key stays in memory.
                if (dbk is not null) Zero(dbk);
                await CloseCoreAsync("open_failed", rewrite: false, relayClosing: false, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Test query of spec 5.3 step 3 on every sealed file that exists: the key opens it, or <see cref="VaultKeyMismatchException"/>.</summary>
    private void TestQuery()
    {
        foreach (var file in _files())
        {
            if (!File.Exists(file.FilePath) || new FileInfo(file.FilePath).Length == 0) continue;
            using var connection = file.Open();
            connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_schema");
        }
    }

    private async Task RunOpenStepsAsync(CancellationToken ct)
    {
        bool first;
        lock (_stateLock)
        {
            first = !_openedOnce;
        }
        foreach (var step in _steps().OrderBy(s => s.Order))
        {
            ct.ThrowIfCancellationRequested();
            await step.RunAsync(first, ct).ConfigureAwait(false);
        }
        lock (_stateLock) _openedOnce = true;
    }

    /// <summary>
    /// Switches Opening to Importing at once (the caller's unlock accepted the door), then replays the inbox, rewrites the boot store and
    /// switches to Open (spec 4.4 step 8, 6.6). The state change happens before the first await, so a caller that does not await the task still
    /// leaves the database usable for requests while the import runs.
    /// </summary>
    public async Task CompleteOpenAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_mode != VaultStoreMode.Sealed) return;
            if (_state is VaultState.Importing or VaultState.Open) return;
            if (_state != VaultState.Opening) throw new InvalidOperationException("The database is not open: unlock it first.");
            _sessionClaim = false;
        }
        SetState(VaultState.Importing);

        try
        {
            if (_relay() is { } relay)
            {
                try
                {
                    var imported = await relay.ImportAsync(ct).ConfigureAwait(false);
                    if (imported.Waiting > 0) _logger?.LogInformation("{Count} received change(s) are still waiting in the inbox; the import runs again shortly.", imported.Waiting);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The inbox keeps what was not applied; the maintenance timer repeats the import. The node is usable meanwhile.
                    _logger?.LogWarning(ex, "Replaying the received changes failed; they stay in the inbox and are tried again.");
                }
            }
            if (_mirror() is { } mirror)
            {
                try { await mirror.RewriteAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger?.LogWarning(ex, "The boot store could not be rewritten after the unlock."); }
            }
        }
        finally
        {
            // A lock during the import already moved the state on; only a node that is still importing is opened.
            SetStateIf(VaultState.Importing, VaultState.Open);
        }
    }

    public async Task<IAsyncDisposable> HoldClosedAsync(string reason, CancellationToken ct = default)
    {
        if (!IsSealed) throw new InvalidOperationException("Only a sealed node's closed database can be held.");

        // An unlock that is in progress ends in Open (the caller then takes a snapshot instead) or back in Closed (the hold is granted).
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            VaultState state;
            lock (_stateLock) state = _state;
            if (state is VaultState.Closed or VaultState.Open || DateTime.UtcNow >= until) break;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        await _transition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_state != VaultState.Closed)
                    throw new InvalidOperationException("The database is not closed (it is " + _state + "): take a snapshot instead of copying the files.");
                if (_held) throw new InvalidOperationException("The database is already held closed.");
                _held = true;
            }
        }
        finally
        {
            _transition.Release();
        }
        _logger?.LogInformation("The closed database is held for a file copy ({Reason}).", reason);
        return new HoldLease(this);
    }

    public Task CloseAsync(string reason, CancellationToken ct = default) => CloseUnderTransitionAsync(reason, ct);

    private async Task CloseUnderTransitionAsync(string reason, CancellationToken ct)
    {
        if (!IsSealed) return;
        await _transition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            VaultState state;
            lock (_stateLock) state = _state;
            // A refused door must not rewrite the boot store from a database that may be the older of the pair (spec 4.4 step 5): the rewrite
            // belongs to the end of a successful unlock and to a normal close. The relay still records the close.
            if (state != VaultState.Closed) await CloseCoreAsync(reason, rewrite: reason is not ("refused" or ResetReason), relayClosing: true, ct).ConfigureAwait(false);
            if (reason == ShutdownReason) await CheckpointBootStoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// The close of spec 5.4, under the transition lock: Closing; the boot store is rewritten and the relay told while the database can still be
    /// read; every file is quiesced, drained, checkpointed and its connections and pool flushed; the keys are cleared; and only then Closed.
    /// </summary>
    private async Task CloseCoreAsync(string reason, bool rewrite, bool relayClosing, CancellationToken ct)
    {
        SetState(VaultState.Closing);
        IReadOnlyList<SealedFile> files = [];
        GenerationEnded[] ended = [];
        try
        {
            if (rewrite)
            {
                if (_mirror() is { } mirror)
                    try { await mirror.RewriteAsync(ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _logger?.LogWarning(ex, "The boot store could not be rewritten at close."); }
            }
            if (relayClosing && _relay() is { } relay)
                try { await relay.OnClosingAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger?.LogWarning(ex, "The relay inbox could not record the close."); }

            files = _files();
            ended = await Task.WhenAll(files.Select(f => Task.Run(() => CloseFileAsync(f), CancellationToken.None))).ConfigureAwait(false);
            if (ended.Any(e => !e.Drained))
                _logger?.LogWarning("A connection was still in use after {Seconds} s when the database closed; it is closed when its owner returns it.",
                    (int)_drainTimeout.TotalSeconds);
        }
        finally
        {
            // Whatever happened above, no key outlives the close.
            ClearKeys();
            lock (_stateLock)
            {
                _sessionClaim = false;
                _tempLeases = 0;
            }
        }

        // Closed is published here and nowhere else, and only with the proof that every file's key-bearing generation was ended (review round 1, H4):
        // a close that threw before this line leaves the state at Closing with the keys cleared, and the next close or open finishes it.
        PublishClosed(files, ended);
        _logger?.LogInformation("The database is closed ({Reason}).", reason);
    }

    /// <summary>The proof that a file's connection generation was ended: it can only be made as the result of <see cref="ISealableConnections.CloseForSealAsync"/>.</summary>
    private sealed class GenerationEnded
    {
        private GenerationEnded(string purpose, bool drained)
        {
            Purpose = purpose;
            Drained = drained;
        }

        public string Purpose { get; }

        /// <summary>False when a connection was still in use at the limit (a straggler, closed when its owner returns it).</summary>
        public bool Drained { get; }

        public static async Task<GenerationEnded> EndAsync(SealedFile file, TimeSpan timeout, bool alreadyDrained)
        {
            var closed = await file.Connections.CloseForSealAsync(timeout).ConfigureAwait(false);
            return new GenerationEnded(file.Purpose, alreadyDrained && closed);
        }
    }

    private void PublishClosed(IReadOnlyList<SealedFile> files, IReadOnlyList<GenerationEnded> ended)
    {
        if (ended.Count != files.Count || files.Any(f => ended.All(e => e.Purpose != f.Purpose)))
            throw new InvalidOperationException("The database cannot be published as closed: a file's connection generation was not ended.");
        SetState(VaultState.Closed);
    }

    /// <summary>
    /// Quiesce, drain, checkpoint and flush one file's connections (spec 5.4 step 3), and end its key-bearing generation. Every path ends in
    /// <see cref="ISealableConnections.CloseForSealAsync"/>: a file that another flow has been switching for longer than the limit is not waited for any
    /// more, its generation is ended without the barrier and the checkpoint is skipped.
    /// </summary>
    private async Task<GenerationEnded> CloseFileAsync(SealedFile file)
    {
        IDisposable? quiesce = null;
        var deadline = DateTime.UtcNow + _drainTimeout;
        while (quiesce is null)
        {
            try
            {
                quiesce = file.Connections.BeginQuiesce();
            }
            catch (InvalidOperationException) when (DateTime.UtcNow < deadline)
            {
                // Another flow is switching this file just now (a restore); it ends, or the limit does.
                await Task.Delay(100).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                _logger?.LogWarning("The {Purpose} database was being switched by another flow for {Seconds} s; its key-bearing connections are ended without waiting.",
                    file.Purpose, (int)_drainTimeout.TotalSeconds);
                return await GenerationEnded.EndAsync(file, _drainTimeout, alreadyDrained: false).ConfigureAwait(false);
            }
        }

        using (quiesce)
        {
            var drained = true;
            try
            {
                await file.Connections.WaitDrainedAsync(_drainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                drained = false;
            }

            if (File.Exists(file.FilePath))
            {
                try
                {
                    using var connection = file.Open();
                    connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "The {Purpose} database was not checkpointed at close (best effort).", file.Purpose);
                }
            }

            return await GenerationEnded.EndAsync(file, drained ? _drainTimeout : TimeSpan.FromSeconds(1), drained).ConfigureAwait(false);
        }
    }

    private void ClearKeys()
    {
        byte[]?[] buffers;
        lock (_stateLock)
        {
            buffers = [_dbk, _vaultKey, _chatKey];
            _dbk = null;
            _vaultKey = null;
            _chatKey = null;
        }
        foreach (var buffer in buffers)
            if (buffer is not null) Zero(buffer);
    }

    private void Zero(byte[] buffer)
    {
        CryptographicOperations.ZeroMemory(buffer);
        Cleared?.Invoke(buffer);
    }

    public async Task WaitUsableAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_stateLock)
            {
                if (_mode == VaultStoreMode.Plain || IsUsableState(_state)) return;
                wait = _usable.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void SetState(VaultState next)
    {
        lock (_stateLock)
        {
            if (_state == next) return;
            _state = next;
            if (IsUsableState(next)) _usable.TrySetResult();
            else if (_usable.Task.IsCompleted) _usable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        StateChanged?.Invoke(next);
    }

    private void SetStateIf(VaultState expected, VaultState next)
    {
        lock (_stateLock)
        {
            if (_state != expected) return;
            _state = next;
            if (IsUsableState(next)) _usable.TrySetResult();
            else if (_usable.Task.IsCompleted) _usable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        StateChanged?.Invoke(next);
    }

    // ---- what the migration and the hosts add ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The migration of section 10 has swapped the files: this gate now guards a sealed node, closed. The caller then opens it with the session's
    /// master DEK (<see cref="OpenAsync"/>, the boot store it just wrote holds the wrapper). The once-per-process sweeps of the plain start
    /// have already run, so the first open does not repeat them. All connections of both factories are gone (the caller quiesced them).
    /// </summary>
    public void SwitchToSealed()
    {
        lock (_stateLock)
        {
            if (_mode == VaultStoreMode.Sealed) return;
            _mode = VaultStoreMode.Sealed;
            _state = VaultState.Closed;
            _openedOnce = true;
            _usable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        StateChanged?.Invoke(VaultState.Closed);
    }

    /// <summary>Node reset (spec 13.1): the node is plain again; there is no key and no file of the sealed node left to open.</summary>
    public void SwitchToPlain()
    {
        ClearKeys();
        lock (_stateLock)
        {
            if (_mode == VaultStoreMode.Plain) return;
            _mode = VaultStoreMode.Plain;
            _state = VaultState.Plain;
            _held = false;
            _sessionClaim = false;
            _tempLeases = 0;
            _usable.TrySetResult();
        }
        StateChanged?.Invoke(VaultState.Plain);
    }

    /// <summary>Records the code of a refused unlock for the status (spec 6.7); null clears it. Best effort: the boot store may be unavailable.</summary>
    public async Task RecordRefusalAsync(string? code, CancellationToken ct = default)
    {
        _lastRefusal = code;
        if (!IsSealed || !_boot.Exists) return;
        try
        {
            await _boot.SetMetaAsync(BootMetaKeys.LastRefusal, code ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "The last refusal could not be stored in the boot store.");
        }
    }

    /// <summary>The persisted <c>last_refusal</c> (survives a restart), or null.</summary>
    public async Task<string?> ReadLastRefusalAsync(CancellationToken ct = default)
    {
        if (_lastRefusal is { Length: > 0 } cached) return cached;
        if (!_boot.Exists) return null;
        var stored = await _boot.GetMetaAsync(BootMetaKeys.LastRefusal, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(stored) ? null : stored;
    }

    /// <summary>
    /// The pair diagnosis of spec 4.4 step 5: the open database's <c>unlock_material_version</c> is lower than the boot store's, which means a
    /// database restored without the boot store of the same moment, or a lost last write. Never a refusal by itself; the caller uses it to name
    /// the reason (<c>store_stale</c>) when the unlock then fails. Needs the database open.
    /// </summary>
    public async Task<bool> IsDatabaseOlderThanBootStoreAsync(CancellationToken ct = default)
    {
        try
        {
            var bootText = await _boot.GetMetaAsync(BootMetaKeys.UnlockMaterialVersion, ct).ConfigureAwait(false);
            if (!long.TryParse(bootText, NumberStyles.None, CultureInfo.InvariantCulture, out var bootVersion)) return false;
            var vault = _files().FirstOrDefault(f => f.Purpose == DbPurposes.Vault);
            if (vault is null || !IsOpen) return false;
            using var connection = vault.Open();
            var text = await connection.ExecuteScalarAsync<string?>("SELECT value FROM tbl_migration_marker WHERE key = 'unlock_material_version'").ConfigureAwait(false);
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var sealedVersion) && sealedVersion < bootVersion;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "The pair diagnosis could not be made.");
            return false;
        }
    }

    private async Task CheckpointBootStoreAsync()
    {
        try
        {
            if (_boot.Exists) await _boot.CheckpointAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "The boot store was not checkpointed at shutdown.");
        }
    }

    public void Dispose()
    {
        ClearKeys();
        _transition.Dispose();
    }

    // ---- leases -------------------------------------------------------------------------------------------------------------------------

    private async ValueTask ReleaseTemporaryAsync()
    {
        bool close;
        lock (_stateLock)
        {
            _tempLeases = Math.Max(0, _tempLeases - 1);
            close = _tempLeases == 0 && !_sessionClaim && _state == VaultState.Opening;
        }
        if (close) await CloseUnderTransitionAsync("temporary", CancellationToken.None).ConfigureAwait(false);
    }

    private void ReleaseHold()
    {
        lock (_stateLock) _held = false;
        _logger?.LogInformation("The hold on the closed database is lifted.");
    }

    private sealed class NoLease : IAsyncDisposable
    {
        public static readonly NoLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryLease : IAsyncDisposable
    {
        private readonly VaultGate _gate;
        private int _disposed;

        public TemporaryLease(VaultGate gate) => _gate = gate;

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? _gate.ReleaseTemporaryAsync() : ValueTask.CompletedTask;
    }

    private sealed class HoldLease(VaultGate gate) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.ReleaseHold();
            return ValueTask.CompletedTask;
        }
    }
}
