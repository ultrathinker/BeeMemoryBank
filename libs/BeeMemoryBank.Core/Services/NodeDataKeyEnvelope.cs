using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// How a node data key (<c>tbl_node_data_key</c>, migration 026) is sealed under the master DEK.
/// A node data key encrypts data a host keeps outside the vault database; the vault database holds
/// only its wrapped form, which every DEK rotation re-wraps (<c>DekRewrapper</c>). Shared by the
/// rewrap and by whichever host-side provider creates and opens a given key, so the framing and
/// the AAD exist exactly once.
/// </summary>
public static class NodeDataKeyEnvelope
{
    /// <summary>The only table that holds wrapped node data keys. Node-local, never synced.</summary>
    public const string TableName = "tbl_node_data_key";

    private const int WrappedLength = 1 + CryptoConstants.KeySize + CryptoConstants.TagSize;

    /// <summary>Generates a fresh random 32-byte data key. The caller owns and must wipe it.</summary>
    public static byte[] Generate() => DekManager.GenerateArticleDek();

    public static (byte[] wrapped, byte[] iv) Wrap(string keyName, byte[] dataKey, byte[] masterDek)
        => DekManager.WrapDek(dataKey, masterDek, Aad(keyName));

    /// <summary>
    /// Opens a wrapped data key with <paramref name="masterDek"/>, or returns null when that is not
    /// the key it was sealed under. A tag mismatch is the normal answer for a wrong candidate, not an
    /// error, so it is not propagated.
    /// </summary>
    public static byte[]? TryUnwrap(string keyName, byte[] wrapped, byte[] iv, byte[] masterDek)
    {
        // Only the v1 framing Wrap produces is accepted. DekManager.UnwrapDek would also take a
        // 48-byte legacy v0 blob and open it with NO AAD — a node data key has never had that form,
        // so accepting it would only let a foreign wrapped DEK be substituted into a row.
        if (wrapped.Length != WrappedLength)
            return null;
        try
        {
            return DekManager.UnwrapVersioned(wrapped, iv, masterDek, Aad(keyName));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // Distinct from every other AAD in the codebase, and bound to the key's name: a wrapped article
    // or media DEK, or another row's wrapped data key, pasted into a row must not open as its key.
    private static byte[] Aad(string keyName) => Encoding.UTF8.GetBytes("bmb-node-data-key-v1:" + keyName);
}
