using System.Globalization;
using System.Security.Cryptography;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>The database key of a sealed node (docs/design/step-b-spec.md 3): created, found under a master DEK, turned into page keys.</summary>
public sealed class DatabaseKeyService(IBootStore bootStore, IBootDbKeyStore wrappers, ILogger<DatabaseKeyService>? logger = null) : IDatabaseKeyService
{
    public byte[] Create() => RandomNumberGenerator.GetBytes(DatabaseKeyCrypto.KeyLength);

    public async Task<byte[]?> TryUnwrapAsync(byte[] masterDek, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(masterDek);
        if (masterDek.Length != DatabaseKeyCrypto.KeyLength) throw new ArgumentException("The master DEK must be exactly 32 bytes.", nameof(masterDek));
        if (!bootStore.Exists) return null;

        // The node id and the generation come from the boot meta: they bind a wrapper to this store (spec 3.3).
        if (!Guid.TryParseExact(await bootStore.GetMetaAsync(BootMetaKeys.NodeId, ct) ?? "", "D", out var nodeId)
            || !int.TryParse(await bootStore.GetMetaAsync(BootMetaKeys.DbkGeneration, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation < 1)
        {
            logger?.LogWarning("The boot store carries no usable node id or database key generation.");
            return null;
        }

        var row = await wrappers.FindAsync(DekFingerprint.Of(masterDek), generation, ct);
        return row is null ? null : DatabaseKeyCrypto.TryUnwrap(row, masterDek, nodeId, logger);
    }

    public byte[] DerivePageKey(ReadOnlySpan<byte> databaseKey, string purpose) => DatabaseKeyCrypto.DerivePageKey(databaseKey, purpose);
}
