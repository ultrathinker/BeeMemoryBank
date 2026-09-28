namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Where the node's Ed25519 seed lives when its identity row is v=2 ("key outside the DEK",
/// plan 3.5): a node that never holds the master DEK — a blind node — still has to sign sync
/// challenges and packages unattended. Registered only in the blind role; a full node keeps its
/// seed in the row, wrapped under the DEK.
/// </summary>
public interface IExternalNodeKey
{
    /// <summary>The raw 32-byte seed. The caller clears it after use.</summary>
    byte[] ReadSeed();
}
