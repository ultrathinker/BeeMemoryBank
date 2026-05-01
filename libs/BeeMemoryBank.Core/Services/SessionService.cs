using System.Security;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Services;

public class SessionService(IKeySlotRepository keySlotRepo, IServiceScopeFactory? scopeFactory = null)
{
    private byte[]? _masterDek;
    private byte[]? _pendingClearDek;
    private readonly object _lock = new();

    // Retired DEK cache for bug #1 (DEK rotation race). When SwapMasterDek runs, the
    // outgoing DEK goes here for a bounded window. If a peer-applied event was wrapped
    // with the old DEK during the rotation window, EventApplier's wrap/unwrap path can
    // ask GetCandidateDeks() and try them in order until one succeeds. This is the
    // "tolerate the race" approach (gemini brainstorm A) — the alternative (write-fence
    // lock around every wrap, claude-B) is more invasive and has no upside in practice
    // because AES-GCM unwrap with a wrong key fails fast (~microseconds). Cap is small:
    // 3 retired DEKs ≈ at most 3 rotations within the retention window. Older entries
    // are evicted from the front so memory exposure stays bounded. All retired DEKs are
    // wiped on Lock() so a stolen process snapshot after explicit lock yields no keys.
    private readonly LinkedList<byte[]> _retiredDeks = new();
    private const int MaxRetiredDeks = 3;

    // Serialize concurrent UnlockAsync calls. Without this, two parallel attempts (browser
    // auto-retry, mobile + web simultaneously) both reach the lazy-rewrap branch and both
    // call UpdateSlotKeyAsync — wasted Argon2 work + last-writer-wins UPDATE on tbl_key_slot.
    // (Claude R2 prod review HIGH-5.)
    private readonly SemaphoreSlim _unlockSemaphore = new(1, 1);

    public LegacyPasswordSlotMigrationService.MigrationResult? LastMigrationResult { get; private set; }

    public bool IsUnlocked
    {
        get { lock (_lock) { return _masterDek != null; } }
    }

    public async Task<bool> UnlockAsync(string password)
    {
        await _unlockSemaphore.WaitAsync();
        try
        {
            return await UnlockCoreAsync(password);
        }
        finally
        {
            _unlockSemaphore.Release();
        }
    }

    private async Task<bool> UnlockCoreAsync(string password)
    {
        LastMigrationResult = null;

        var slots = await keySlotRepo.GetAllAsync();
        var trySlots = slots.Where(s => s.Salt != null && s.ArgonMemory.HasValue).ToList();

        foreach (var slot in trySlots)
        {
            const int MinArgonMemory = 32768; // 32 MiB
            const int MinArgonIterations = 2;
            if (slot.ArgonMemory < MinArgonMemory || slot.ArgonIterations < MinArgonIterations)
                throw new SecurityException($"Key slot has weakened KDF params (memory={slot.ArgonMemory}, iter={slot.ArgonIterations}); refusing to unlock.");

            const int MaxArgonMemory = 1_048_576;
            const int MaxArgonIterations = 20;
            const int MaxArgonParallelism = 16;
            if (slot.ArgonMemory > MaxArgonMemory || slot.ArgonIterations > MaxArgonIterations || slot.ArgonParallelism > MaxArgonParallelism)
                throw new SecurityException($"Key slot has unreasonable KDF params (memory={slot.ArgonMemory}, iter={slot.ArgonIterations}, parallelism={slot.ArgonParallelism}); refusing to unlock.");

            byte[]? kek = null;
            byte[]? unwrappedDek = null;
            try
            {
                kek = KeyDerivation.DeriveKek(
                    password,
                    slot.Salt!,
                    slot.ArgonMemory!.Value,
                    slot.ArgonIterations!.Value,
                    slot.ArgonParallelism!.Value);

                unwrappedDek = MasterKeyManager.UnwrapMasterDek(slot.EncryptedMasterDek, slot.IV, kek);
            }
            catch
            {
                // Wrong password for this slot. Wipe the derived KEK before moving on so failed
                // login attempts don't accumulate key material on the heap until GC runs.
                if (kek != null) Array.Clear(kek);
                continue;
            }

            byte[] currentCandidate = unwrappedDek!;
            bool sentinelMatch = false;

            try
            {
                if (scopeFactory != null)
                {
                    using var sentinelScope = scopeFactory.CreateScope();
                    var nodeIdentityRepo = sentinelScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
                    var sentinel = await nodeIdentityRepo.GetSentinelAsync();

                    if (sentinel != null)
                    {
                        // VerifySentinel decrypts the stored sentinel using the candidate DEK —
                        // ComputeSentinel can't be byte-compared because it generates a fresh random
                        // IV every call. (Found by Gemini reviewer at p3.3.)
                        sentinelMatch = MasterKeyManager.VerifySentinel(sentinel, currentCandidate);

                        if (!sentinelMatch)
                        {
                            var rewrapService = sentinelScope.ServiceProvider.GetService<ILazySlotRewrapService>();
                            if (rewrapService != null)
                            {
                                var result = await rewrapService.TryRewrapAsync(slot, kek, currentCandidate, sentinel);
                                if (result.Success && result.RewrappedDek != null)
                                {
                                    Array.Clear(currentCandidate, 0, currentCandidate.Length);
                                    currentCandidate = result.RewrappedDek;
                                    sentinelMatch = true;
                                }
                            }
                        }
                    }
                    else
                    {
                        sentinelMatch = true;
                    }
                }
                else
                {
                    sentinelMatch = true;
                }

                if (!sentinelMatch)
                {
                    Array.Clear(currentCandidate, 0, currentCandidate.Length);
                    Array.Clear(kek, 0, kek.Length);
                    continue;
                }

                lock (_lock)
                {
                    if (_masterDek != null) Array.Clear(_masterDek);
                    _masterDek = currentCandidate;
                }

                if (scopeFactory != null)
                {
                    using var scope = scopeFactory.CreateScope();
                    var migration = scope.ServiceProvider.GetRequiredService<LegacyPasswordSlotMigrationService>();
                    LastMigrationResult = await migration.MigrateIfNeededAsync();

                    // Fire-and-forget retries: capture scopeFactory (singleton-stable) and
                    // resolve services in a FRESH scope inside Task.Run. Resolving from the
                    // outer scope would risk ObjectDisposedException once `using var scope`
                    // disposes when UnlockCoreAsync returns. Today the relevant services are
                    // Singletons so the practical risk is nil, but a future Scoped registration
                    // would silently break this — defensive resolution stops the trap. (Gemini
                    // post-brainstorm review MED #4.)
                    var capturedScopeFactory = scopeFactory;

                    // Retry any deferred auto-accept DEK rotations whose COMMIT arrived while the
                    // session was locked. (Claude R2 prod review CRIT-1.)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var s = capturedScopeFactory.CreateScope();
                            var dekApplier = s.ServiceProvider.GetService<IDekRotationApplier>();
                            if (dekApplier != null) await dekApplier.RetryPendingAutoAcceptsAsync();
                        }
                        catch { /* logged inside the applier */ }
                    });

                    // Same pattern for stuck network-restore events. EventApplier auto-accepts
                    // restore via fire-and-forget Task.Run — if that Task throws (network blip
                    // mid-download, locked session at apply time, process crash before startup
                    // sweep), state stays Pending/Downloading/Applying with no automatic retry.
                    // Brainstorm consensus (kilo, claude, gemini): bug #5 restore-retry mirrors
                    // DEK rotation retry. AcceptRestoreAsync is idempotent.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var s = capturedScopeFactory.CreateScope();
                            var restoreRetrier = s.ServiceProvider.GetService<IRestoreRetrier>();
                            if (restoreRetrier != null) await restoreRetrier.RetryPendingRestoresAsync();
                        }
                        catch { /* logged inside the retrier */ }
                    });

                    // Lazy migration of legacy v=0 plaintext private key in tbl_node_identity.
                    // Existing nodes (created before the v=1 flip) have a plaintext seed in
                    // ed25519_private_key. On first successful unlock we re-encrypt under the
                    // master DEK and bump v=1. New nodes are already created at v=1 in
                    // InitializationService / InitEndpoints / JoinCommand. Idempotent: subsequent
                    // unlocks find v=1 and do nothing. AAD = "bmb-node-pk" || nodeId, matching
                    // the encrypt-on-init binding.
                    var migrationDek = (byte[])_masterDek!.Clone();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var s = capturedScopeFactory.CreateScope();
                            var nodeRepo = s.ServiceProvider.GetService<INodeIdentityRepository>();
                            if (nodeRepo == null) return;
                            var current = await nodeRepo.GetAsync();
                            if (current == null || current.Ed25519PrivateKeyV != 0) return;

                            var (wrapped, iv) = NodeIdentityCrypto.EncryptPrivateKey(
                                current.Ed25519PrivateKey, migrationDek, current.NodeId);
                            await nodeRepo.UpgradePrivateKeyToV1Async(current.NodeId, wrapped, iv);
                        }
                        catch { /* best-effort — next unlock retries */ }
                        finally
                        {
                            Array.Clear(migrationDek);
                        }
                    });
                }

                Array.Clear(kek, 0, kek.Length);
                return true;
            }
            catch
            {
                Array.Clear(currentCandidate, 0, currentCandidate.Length);
                Array.Clear(kek, 0, kek.Length);
                throw;
            }
        }
        return false;
    }

    public void UnlockWithDek(byte[] masterDek)
    {
        lock (_lock)
        {
            if (_masterDek != null) Array.Clear(_masterDek);
            _masterDek = masterDek;
        }
    }

    public void SwapMasterDek(byte[] newMasterDek)
    {
        // Bug #1 (DEK rotation race): the old approach was Task.Delay(2s) then Array.Clear —
        // a heuristic drain window for in-flight wrap operations holding a Clone() of the old
        // DEK. The drain was racy under load (slow IO could exceed 2s) and worse, didn't help
        // peers receiving an event encrypted with the old DEK after they'd rotated. Now: push
        // the outgoing DEK to a small retired cache (capped, evicted FIFO). EventApplier's
        // unwrap path queries GetCandidateDeks() and tries them on CryptographicException, so
        // late-arriving cross-DEK events decrypt naturally. Local in-flight writes still
        // complete with their captured Clone (no semantic change there). ClearPendingDek
        // remains as a no-op for callers (kept for ABI compat) but the timed-clear is gone.
        byte[]? oldDek;
        lock (_lock)
        {
            oldDek = _masterDek;
            _masterDek = newMasterDek;
            _pendingClearDek = null; // legacy field — we don't use a single drained slot anymore.

            if (oldDek != null)
            {
                _retiredDeks.AddLast(oldDek);
                while (_retiredDeks.Count > MaxRetiredDeks)
                {
                    var evicted = _retiredDeks.First!.Value;
                    _retiredDeks.RemoveFirst();
                    Array.Clear(evicted);
                }
            }
        }
    }

    /// <summary>
    /// Returns clones of every DEK that could plausibly decrypt a recently-arrived event:
    /// the current master DEK first, then up to MaxRetiredDeks previous ones in
    /// most-recently-retired order. Caller MUST Array.Clear each returned buffer in a
    /// finally block. Used by EventApplier on CryptographicException to walk the rotation
    /// chain. Returns empty if locked. (Bug #1 retired-DEK cache.)
    /// </summary>
    public byte[][] GetCandidateDeks()
    {
        lock (_lock)
        {
            if (_masterDek == null) return Array.Empty<byte[]>();
            var result = new byte[1 + _retiredDeks.Count][];
            result[0] = (byte[])_masterDek.Clone();
            int i = 1;
            // Iterate from most-recent retired (Last) to oldest (First) — the most recent
            // retirement is the most likely match for an in-flight event.
            for (var node = _retiredDeks.Last; node != null; node = node.Previous)
                result[i++] = (byte[])node.Value.Clone();
            return result;
        }
    }

    /// <summary>
    /// Wipes any oldDek pending the 2s drain window. ONLY for the host shutdown hook —
    /// any other caller can race with rotation by clearing before in-flight ops finish
    /// reading their `GetMasterDek().Clone()` snapshot. Kept internal-by-convention via
    /// the doc comment until/unless we add InternalsVisibleTo. (Claude security review.)
    /// </summary>
    public void ClearPendingDek()
    {
        byte[]? toClear;
        lock (_lock)
        {
            toClear = _pendingClearDek;
            _pendingClearDek = null;
        }
        if (toClear != null)
            Array.Clear(toClear);
    }

    public void Lock()
    {
        lock (_lock)
        {
            if (_masterDek != null)
            {
                Array.Clear(_masterDek);
                _masterDek = null;
            }
            // Wipe retired-DEK cache: explicit lock means "evict ALL key material" so a
            // process memory dump after lock yields no usable keys. (Bug #1 cache.)
            foreach (var retired in _retiredDeks)
                Array.Clear(retired);
            _retiredDeks.Clear();
            if (_pendingClearDek != null)
            {
                Array.Clear(_pendingClearDek);
                _pendingClearDek = null;
            }
        }
    }

    public byte[] GetMasterDek()
    {
        lock (_lock)
        {
            if (_masterDek == null)
                throw new InvalidOperationException("Session is locked. Call UnlockAsync first.");
            return (byte[])_masterDek.Clone();
        }
    }

    /// <summary>
    /// Tries each candidate DEK (current master first, then retired DEKs in MRU order) until
    /// the supplied unwrap function succeeds or all are exhausted. Used by EventApplier on
    /// CryptographicException during cross-node article-body decryption: a peer that just
    /// rotated may receive an event wrapped with the local node's old master DEK during the
    /// rotation window. Returns the unwrap result, or throws the LAST exception if none worked.
    /// Each candidate DEK is wiped after use. (Bug #1 retired-DEK cache.)
    /// </summary>
    public T TryUnwrapWithCandidates<T>(Func<byte[], T> unwrap)
    {
        var candidates = GetCandidateDeks();
        if (candidates.Length == 0)
            throw new InvalidOperationException("Session is locked.");

        Exception? lastError = null;
        try
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    return unwrap(candidate);
                }
                catch (System.Security.Cryptography.CryptographicException ex)
                {
                    lastError = ex;
                    // try next candidate
                }
            }
        }
        finally
        {
            foreach (var c in candidates) Array.Clear(c);
        }
        throw lastError ?? new System.Security.Cryptography.CryptographicException(
            "Could not unwrap with any candidate DEK.");
    }
}
