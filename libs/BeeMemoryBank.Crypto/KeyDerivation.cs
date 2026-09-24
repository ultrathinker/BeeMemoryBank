using Konscious.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Key Encryption Key (KEK) derivation from password via Argon2id.
/// </summary>
public static class KeyDerivation
{
    // Every Argon2id derivation allocates its full memory cost (64 MiB by default) and burns CPU for
    // a noticeable fraction of a second. Several of the paths that trigger one are reachable by
    // unauthenticated or low-privilege callers (join, login, remote-token, protected-article
    // passphrases), so without a bound a burst of requests turns into a memory/CPU exhaustion of the
    // whole node. One process-wide gate caps how many run at once and how many may wait; beyond
    // that the caller gets KdfBusyException immediately instead of piling up blocked threads.
    private static readonly int MaxConcurrent = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    private const int MaxQueued = 16;
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);
    private static int _queued;

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
        AcquireGate();
        try
        {
            return DeriveKekCore(password, salt, memory, iterations, parallelism);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Test hook: occupies every derivation slot and the whole wait queue until disposed, so a test
    /// can observe the saturated behaviour without running dozens of real derivations.
    /// </summary>
    internal static IDisposable SaturateForTests()
    {
        for (var i = 0; i < MaxConcurrent; i++) Gate.Wait();
        Interlocked.Add(ref _queued, MaxQueued);
        return new SaturationRelease();
    }

    private sealed class SaturationRelease : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Interlocked.Add(ref _queued, -MaxQueued);
            Gate.Release(MaxConcurrent);
        }
    }

    private static void AcquireGate()
    {
        if (Gate.Wait(0)) return;

        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            throw new KdfBusyException();
        }
        try
        {
            if (!Gate.Wait(MaxWait))
                throw new KdfBusyException();
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
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
