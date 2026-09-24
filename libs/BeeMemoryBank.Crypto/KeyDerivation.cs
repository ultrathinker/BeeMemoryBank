using Konscious.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Key Encryption Key (KEK) derivation from password via Argon2id.
/// </summary>
public static class KeyDerivation
{
    // Every Argon2id derivation allocates its full memory cost and burns CPU for a noticeable
    // fraction of a second. Several paths that trigger one are reachable by unauthenticated or
    // low-privilege callers (join, login, remote-token, protected-article passphrases), and some
    // callers choose the memory cost themselves (a planted protected blob, a peer's key slot). So the
    // process-wide gate budgets MEMORY, not just a count: capacity is expressed in 64 MiB units,
    // a derivation takes ceil(memory / 64 MiB) of them, and the total never exceeds the budget no
    // matter which parameters callers pick. A bounded wait queue sits in front; beyond it the caller
    // gets KdfBusyException immediately instead of piling up blocked threads.
    private const int UnitKiB = 65_536; // 64 MiB, Argon2 memory is expressed in KiB
    private static readonly int CapacityUnits = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    private const int MaxQueued = 16;
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    private static readonly object GateLock = new();
    private static int _availableUnits = CapacityUnits;
    private static int _queued;

    /// <summary>Total Argon2 memory, in KiB, the gate lets run at the same time.</summary>
    internal static long MemoryBudgetKiB => (long)CapacityUnits * UnitKiB;

    /// <summary>
    /// Derives a KEK (Key Encryption Key) from password and salt.
    /// Throws <see cref="KdfBusyException"/> when the node is already saturated with derivations.
    /// </summary>
    public static byte[] DeriveKek(
        string password,
        byte[] salt,
        int memory = CryptoConstants.DefaultArgonMemory,
        int iterations = CryptoConstants.DefaultArgonIterations,
        int parallelism = CryptoConstants.DefaultArgonParallelism)
    {
        var units = UnitsFor(memory);
        AcquireUnits(units);
        var inFlight = Interlocked.Add(ref _inFlightKiB, memory);
        long peak;
        while (inFlight > (peak = Interlocked.Read(ref _peakInFlightKiB))
               && Interlocked.CompareExchange(ref _peakInFlightKiB, inFlight, peak) != peak) { }
        try
        {
            return DeriveKekCore(password, salt, memory, iterations, parallelism);
        }
        finally
        {
            Interlocked.Add(ref _inFlightKiB, -memory);
            ReleaseUnits(units);
        }
    }

    // Diagnostics: Argon2 memory currently committed, and the highest it has been.
    private static long _inFlightKiB;
    private static long _peakInFlightKiB;
    internal static long PeakInFlightKiB => Interlocked.Read(ref _peakInFlightKiB);
    internal static void ResetPeakForTests() => Interlocked.Exchange(ref _peakInFlightKiB, 0);

    /// <summary>
    /// Budget units a derivation of <paramref name="memoryKiB"/> takes. A request larger than the
    /// whole budget is clamped to the whole budget: it then runs alone, never next to others.
    /// </summary>
    internal static int UnitsFor(int memoryKiB) =>
        (int)Math.Clamp(((long)Math.Max(memoryKiB, 1) + UnitKiB - 1) / UnitKiB, 1, CapacityUnits);

    /// <summary>
    /// Test hook: occupies the whole budget and the whole wait queue until disposed, so a test can
    /// observe the saturated behaviour without running dozens of real derivations.
    /// </summary>
    internal static IDisposable SaturateForTests()
    {
        AcquireUnits(CapacityUnits);
        lock (GateLock) _queued += MaxQueued;
        return new SaturationRelease();
    }

    private sealed class SaturationRelease : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (GateLock) _queued -= MaxQueued;
            ReleaseUnits(CapacityUnits);
        }
    }

    private static void AcquireUnits(int units)
    {
        lock (GateLock)
        {
            if (_availableUnits >= units)
            {
                _availableUnits -= units;
                return;
            }

            if (_queued >= MaxQueued)
                throw new KdfBusyException();

            _queued++;
            try
            {
                var deadline = DateTime.UtcNow + MaxWait;
                while (_availableUnits < units)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(GateLock, remaining))
                    {
                        if (_availableUnits >= units) break;
                        throw new KdfBusyException();
                    }
                }
                _availableUnits -= units;
            }
            finally
            {
                _queued--;
            }
        }
    }

    private static void ReleaseUnits(int units)
    {
        lock (GateLock)
        {
            _availableUnits += units;
            Monitor.PulseAll(GateLock);
        }
    }

    private static byte[] DeriveKekCore(string password, byte[] salt, int memory, int iterations, int parallelism)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes);
            argon2.Salt = salt;
            argon2.MemorySize = memory;
            argon2.Iterations = iterations;
            argon2.DegreeOfParallelism = parallelism;

            return argon2.GetBytes(CryptoConstants.KeySize);
        }
        finally
        {
            // Zero the UTF-8 password bytes so the plaintext doesn't linger on the heap until
            // GC. Argon2id does NOT take ownership; we own the buffer. (Found by Kilo R1
            // security review HIGH-3.)
            Array.Clear(passwordBytes);
        }
    }

    /// <summary>
    /// Rejects Argon2id parameters that did not come from this node itself (a joining node's view of
    /// a peer's key slot, a protected-article blob anyone with write access can plant) before any
    /// memory is committed to them. The app only ever writes the defaults (64 MiB, t=3, p=4); the
    /// floor stops a weakened record, the ceiling stops one that is weaponised to exhaust memory.
    /// </summary>
    public static void ValidateUntrustedParameters(int memory, int iterations, int parallelism)
    {
        const int MinMemory = 32_768, MaxMemory = 262_144; // 32 MiB .. 256 MiB
        const int MinIterations = 2, MaxIterations = 10;
        const int MinParallelism = 1, MaxParallelism = 8;
        if (memory < MinMemory || iterations < MinIterations)
            throw new System.Security.Cryptography.CryptographicException(
                $"Refusing weakened Argon2id parameters (memory={memory}, iterations={iterations}).");
        if (memory > MaxMemory || iterations > MaxIterations || parallelism < MinParallelism || parallelism > MaxParallelism)
            throw new System.Security.Cryptography.CryptographicException(
                $"Refusing unreasonable Argon2id parameters (memory={memory}, iterations={iterations}, parallelism={parallelism}).");
    }

    public static byte[] GenerateSalt() => SecureRandom.GetBytes(CryptoConstants.SaltSize);
}
