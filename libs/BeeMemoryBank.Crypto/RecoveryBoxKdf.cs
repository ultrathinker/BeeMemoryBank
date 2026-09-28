using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto;

/// <summary>An Argon2id parameter set a recovery box may name. Memory is in KiB, like Argon2 itself.</summary>
public sealed record RecoveryBoxPreset(string Name, int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>
    /// Heavier than an ordinary key slot: runs only on <see cref="HeavyDerivationQueue"/>, outside the
    /// shared KDF budget in <see cref="KeyDerivation"/>.
    /// </summary>
    public bool IsHeavy => MemoryKiB > CryptoConstants.DefaultArgonMemory;
}

/// <summary>
/// The validator for recovery-box parameters (BMB-43, plan 6.6). Deliberately separate from
/// <see cref="KeyDerivation.ValidateUntrustedParameters"/>: that one caps a peer's key slot at 256 MiB
/// and must stay that way, while a strong box legitimately needs 512 MiB or 1 GiB. A box names a
/// preset, never raw numbers, so a crafted box cannot make a restoring PC commit to an arbitrarily
/// expensive derivation — anything outside this closed list is refused before any memory is spent.
/// </summary>
public static class RecoveryBoxKdf
{
    public const string KindStrong = "strong";
    public const string KindDevice = "device";

    /// <summary>A copy of an ordinary device key slot: the app's default 64 MiB, t=3, p=4.</summary>
    public const string Device64 = "d64t3";
    /// <summary>Strong box fallback where 1 GiB is not available (a small hub).</summary>
    public const string Strong512 = "s512t6";
    /// <summary>Strong box built by a PC.</summary>
    public const string Strong1024 = "s1024t4";

    public const int SaltSize = CryptoConstants.SaltSize;
    public const int IvSize = CryptoConstants.IvSize;

    // A box carries the DEK in MasterKeyManager's wrap format: v0 (key + tag) or v1 (version byte +
    // key + tag). Device boxes are byte copies of slots, and slots written before v1 still exist.
    private const int WrappedV0Size = CryptoConstants.KeySize + CryptoConstants.TagSize;
    private const int WrappedV1Size = WrappedV0Size + 1;
    private const byte WrappedV1 = 0x01;

    private static readonly Dictionary<string, RecoveryBoxPreset> Presets = new(StringComparer.Ordinal)
    {
        [Device64] = new(Device64, CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism),
        [Strong512] = new(Strong512, 524_288, 6, 4),
        [Strong1024] = new(Strong1024, 1_048_576, 4, 4),
    };

    /// <summary>The parameters behind <paramref name="preset"/>; throws for anything not on the list.</summary>
    public static RecoveryBoxPreset Resolve(string? preset) =>
        preset != null && Presets.TryGetValue(preset, out var p)
            ? p
            : throw new CryptographicException($"'{preset}' is not an allowed recovery box KDF preset.");

    /// <summary>
    /// A device box may only be the 64 MiB preset and a strong box only one of the heavy ones. The
    /// pairing is what keeps a phone (any peer may publish a box about itself) from filling the
    /// "strong" place with a cheap box, and a strong box from pretending to be a light one.
    /// </summary>
    public static bool IsAllowed(string? kind, string? preset) => kind switch
    {
        KindDevice => preset == Device64,
        KindStrong => preset is Strong512 or Strong1024,
        _ => false,
    };

    /// <summary>
    /// Shape check of a box's stored material, done before anything is derived. Used by the event
    /// applier (which cannot open a box) as well as by anything that is about to try a password: a
    /// value the unwrap would refuse anyway — wrong length, unknown version byte — must be refused
    /// BEFORE an Argon2 derivation is spent on it, or a peer can make a restore burn gigabytes of work
    /// on random bytes.
    /// </summary>
    public static bool IsWellFormed(byte[]? salt, byte[]? wrapped, byte[]? iv) =>
        salt is { Length: SaltSize }
        && iv is { Length: IvSize }
        && (wrapped is { Length: WrappedV0Size } || wrapped is { Length: WrappedV1Size } && wrapped[0] == WrappedV1);

    /// <summary>
    /// <see cref="IsWellFormed(byte[], byte[], byte[])"/> for a box of <paramref name="kind"/>. A strong box
    /// is only ever written by this code, always in the versioned format; only a device box — a byte
    /// copy of a key slot — may still carry the legacy unversioned wrap.
    /// </summary>
    public static bool IsWellFormed(string? kind, byte[]? salt, byte[]? wrapped, byte[]? iv) =>
        IsWellFormed(salt, wrapped, iv) && (kind != KindStrong || wrapped!.Length == WrappedV1Size);

    /// <summary>
    /// The preset a key slot's own parameters correspond to, or null when they match none — such a
    /// slot is not copied into a device box, because a receiver would (rightly) refuse it.
    /// </summary>
    public static string? PresetForSlot(int memoryKiB, int iterations, int parallelism)
    {
        var device = Presets[Device64];
        return memoryKiB == device.MemoryKiB && iterations == device.Iterations && parallelism == device.Parallelism
            ? Device64
            : null;
    }
}
