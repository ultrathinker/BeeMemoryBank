using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>
/// An <see cref="IVaultKeySource"/> over page keys it was given, for tests and tools that open a sealed file without the vault gate (the
/// gate, track 3, is the key source of a running node). Mode is Sealed whether or not the keys are loaded; <see cref="Close"/> zeroes them
/// the way the gate clears the page keys at close (spec 5.4 step 4) and <see cref="Open"/> loads keys and starts a new generation.
/// </summary>
public sealed class InMemoryVaultKeySource : IVaultKeySource
{
    private readonly object _lock = new();
    private byte[]? _vault;
    private byte[]? _chat;
    private long _generation;

    /// <summary>Open, generation 1, with random page keys.</summary>
    public InMemoryVaultKeySource() : this(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)) { }

    /// <summary>Open, generation 1, with copies of the given page keys (the caller keeps and clears its own).</summary>
    public InMemoryVaultKeySource(byte[] vaultKey, byte[] chatKey) => Open(vaultKey, chatKey);

    public VaultStoreMode Mode => VaultStoreMode.Sealed;

    public bool IsOpen
    {
        get { lock (_lock) return _vault is not null; }
    }

    public long OpenGeneration
    {
        get { lock (_lock) return _generation; }
    }

    /// <summary>Loads copies of the page keys and moves to the next generation.</summary>
    public void Open(byte[] vaultKey, byte[] chatKey)
    {
        if (vaultKey.Length != 32 || chatKey.Length != 32) throw new ArgumentException("A page key is exactly 32 bytes.");
        lock (_lock)
        {
            Clear();
            _vault = (byte[])vaultKey.Clone();
            _chat = (byte[])chatKey.Clone();
            _generation++;
        }
    }

    /// <summary>Zeroes and drops the page keys. The generation stays.</summary>
    public void Close()
    {
        lock (_lock) Clear();
    }

    public byte[] GetPageKey(string purpose)
    {
        lock (_lock)
        {
            var key = purpose switch
            {
                DbPurposes.Vault => _vault,
                DbPurposes.Chat => _chat,
                _ => throw new ArgumentException("Unknown purpose.", nameof(purpose)),
            };
            if (key is null) throw new VaultSealedException();
            return (byte[])key.Clone();
        }
    }

    private void Clear()
    {
        if (_vault is not null) CryptographicOperations.ZeroMemory(_vault);
        if (_chat is not null) CryptographicOperations.ZeroMemory(_chat);
        _vault = null;
        _chat = null;
    }

    /// <summary>Never shows a key.</summary>
    public override string ToString() => $"InMemoryVaultKeySource(open={IsOpen}, generation={OpenGeneration})";
}
