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

    /// <summary>
    /// Raised at the end of <see cref="Lock"/>, after every in-memory key has been wiped.
    /// Api-layer code subscribes to this (see SessionEndpoints.MapSessionEndpoints) to clear
    /// state that Core has no business knowing about but that must not outlive a lock either —
    /// e.g. ProtectedUnlockCache's cached per-article passphrases (finding M8) — without giving
    /// Core a dependency on Api-layer types. Fires for every caller of Lock(), not just the
    /// /api/session/lock endpoint: node reset, snapshot/network restore, and the process-shutdown
    /// hook all call it directly too, and each of those needs the same cleanup.
    /// </summary>
    public event Action? Locked;

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
            bool authorized = true;

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

                    // SECURITY: only a superadmin may unlock the shared, process-wide vault
                    // session — the same policy SessionEndpoints' /login already enforces (a
                    // non-superadmin gets 403 "Server is locked" there instead of ever reaching
                    // UnlockAsync). /unlock itself had no such gate: it accepted ANY slot whose
                    // password matched, including — in principle — an ordinary user's "user"
                    // slot. Checked HERE rather than via an X-User-Role header or any other
                    // caller-supplied claim, because this is the one point that cannot be lied
                    // to: the password has just been cryptographically proven (KEK unwrap +
                    // sentinel match) to belong to THIS row in tbl_key_slot, so asking "whose
                    // slot is this, really?" against tbl_user directly is authoritative
                    // regardless of what the caller claims about itself.
                    //
                    // Only "user" slots need this check. "recovery" and legacy pre-migration
                    // "password" slots are intentionally exempt: a recovery key is never tied to
                    // any user account (it's the user's sole break-glass path back into the
                    // vault — rejecting it here would be a self-inflicted lockout), and a legacy
                    // "password" slot predates the whole user table / role concept — it IS the
                    // superadmin-equivalent credential until LegacyPasswordSlotMigrationService
                    // (below) converts it to a "user" slot on a synthetic admin account. Under
                    // the current invariants (UserService only ever creates/keeps a "user" slot
                    // for a superadmin — see CreateUserAsync / RewrapOrProvisionKeySlotAsync /
                    // ProvisionMissingKeySlotAsync, and UpdateUserAsync deletes the slot the
                    // instant its owner is demoted) no non-superadmin should ever hold a "user"
                    // slot, so this is defence in depth against a future bug or hand-edited DB
                    // row, not a fix for a reachable path — but it's cheap enough to check
                    // unconditionally rather than rely on that invariant never being violated.
                    if (sentinelMatch && slot.SlotType == "user")
                    {
                        var userRepo = sentinelScope.ServiceProvider.GetService<IUserRepository>();
                        if (userRepo != null)
                        {
                            var owner = (await userRepo.ListActiveAsync())
                                .FirstOrDefault(u => u.KeySlotId == slot.SlotId);
                            authorized = owner != null && owner.Role == UserRoles.Superadmin;
                        }
                    }
                }
                else
                {
                    sentinelMatch = true;
                }

                // A wrong password and a correct-password-but-not-permitted slot must be
                // indistinguishable to the caller: both just fall through to the next candidate
                // slot here, and if nothing else matches, UnlockAsync returns false exactly like
                // a plain wrong-password attempt — no separate error path that would turn this
                // endpoint into an oracle for "is this someone else's valid password".
                if (!sentinelMatch || !authorized)
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
                }
                TriggerPostUnlockCatchUp();

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
        TriggerPostUnlockCatchUp();
    }

    /// <summary>
    /// Fires the same catch-up work UnlockCoreAsync always ran after a password unlock, but from
    /// a single shared place so <see cref="UnlockWithDek"/> callers (OS auto-unlock, agent-token
    /// unlock) get it too. Without this, a node that ONLY ever unlocks via one of those paths —
    /// exactly the auto-unlock server-mode use case — would never retry a DEK rotation
    /// auto-accept or network-restore that was pending while it was locked, and would never
    /// migrate a legacy v=0 plaintext node identity key. All three are already documented
    /// idempotent/safe-to-retry, so running them unconditionally on every unlock is safe.
    /// </summary>
    private void TriggerPostUnlockCatchUp()
    {
        if (scopeFactory == null) return;
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
        byte[]? migrationDek;
        lock (_lock)
        {
            if (_masterDek == null) return; // shouldn't happen — caller just set it — but be safe
            migrationDek = (byte[])_masterDek.Clone();
        }
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
        // Outside the lock: subscriber code (see Locked's doc comment) shouldn't run while we're
        // holding our own internal lock — it has no reason to touch _masterDek et al., and
        // invoking arbitrary handlers from inside a lock risks reentrancy/deadlock for no benefit.
        Locked?.Invoke();
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
