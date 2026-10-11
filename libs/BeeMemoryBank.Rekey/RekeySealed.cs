using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey;

/// <summary>
/// What a re-key of a sealed vault (docs/design/step-b-spec.md 13.2) needs besides the owner's password. The verb gets these from the
/// node's services after the owner's master DEK opened the source through the boot doors (<c>IVaultGate.OpenTemporarilyAsync</c>, 5.7);
/// the runner itself never reads the old boot store and never sees a door.
/// </summary>
public sealed class RekeySealedOptions
{
    /// <summary>The page keys of the SOURCE vault, open (the gate's key source while the temporary open lives). The runner only reads copies.</summary>
    public required IVaultKeySource SourceKeys { get; init; }

    /// <summary>The generation of the source's database key; the new vault gets this plus one (spec 3.1).</summary>
    public required int SourceDbkGeneration { get; init; }

    /// <summary>Creates the new database key and derives page keys; <c>TryUnwrapAsync</c> reads the OLD boot store, which the check of the new vault asks.</summary>
    public required IDatabaseKeyService KeyService { get; init; }

    /// <summary>Writes the new vault's boot store (<c>WriteNewBootStoreAsync</c>, under the new master DEK only).</summary>
    public required IUnlockMaterialMirror Mirror { get; init; }
}

/// <summary>A re-key step or pre-flight that opens the SOURCE vault's files itself and so needs the source's page keys (spec 13.2).</summary>
public interface IRekeySealedSourceConsumer
{
    /// <summary>Called by the runner before the work of a sealed vault and with <c>null</c> when it ends.</summary>
    void SetSealedSource(IVaultKeySource? source);
}

/// <summary>
/// The sealed half of one re-key run: the new database key and page keys, keyed opens of the source and of the copy, the new boot store,
/// and the checks that nothing of the old vault opens the new one. Every page key handed out is a copy the caller clears.
/// </summary>
internal sealed class SealedRekeyRun : IDisposable
{
    private readonly RekeySealedOptions _options;
    private readonly byte[] _newDbk;

    public SealedRekeyRun(RekeySealedOptions options)
    {
        _options = options;
        _newDbk = options.KeyService.Create();
        var vault = options.KeyService.DerivePageKey(_newDbk, DbPurposes.Vault);
        var chat = options.KeyService.DerivePageKey(_newDbk, DbPurposes.Chat);
        try { NewKeys = new InMemoryVaultKeySource(vault, chat); }
        finally
        {
            CryptographicOperations.ZeroMemory(vault);
            CryptographicOperations.ZeroMemory(chat);
        }
    }

    /// <summary>The page keys of the new vault (an open key source, so a factory over the copy can read the doors for the boot store).</summary>
    public InMemoryVaultKeySource NewKeys { get; }

    public IVaultKeySource Source => _options.SourceKeys;

    public int NewGeneration => _options.SourceDbkGeneration + 1;

    public byte[] NewPageKey(string purpose) => NewKeys.GetPageKey(purpose);

    public SqliteConnection OpenSource(string path, string purpose) => WithKey(Source.GetPageKey(purpose), key => RekeyRunner.OpenLive(path, key));

    public SqliteConnection OpenNew(string path, string purpose) => WithKey(NewKeys.GetPageKey(purpose), key => RekeyRunner.OpenCopy(path, key));

    private static SqliteConnection WithKey(byte[] key, Func<byte[], SqliteConnection> open)
    {
        try { return open(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>
    /// Writes the new vault's boot store into <paramref name="newDir"/> (identity, whitelist and doors read from the re-keyed copy, the new
    /// database key wrapped under the new master DEK only), and closes every connection to the copy again so the directory can be renamed.
    /// </summary>
    public async Task WriteBootStoreAsync(string newDir, byte[] campaignDek, CancellationToken ct)
    {
        var bootPath = Path.Combine(newDir, VaultStoreFiles.Boot);
        using (var factory = new DbConnectionFactory(Path.Combine(newDir, VaultStoreFiles.Database), NewKeys))
            await _options.Mirror.WriteNewBootStoreAsync(bootPath, factory, _newDbk, NewGeneration, [campaignDek], ct).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        if (!File.Exists(bootPath) || VaultStoreFiles.ReadApplicationId(bootPath) != VaultStoreFiles.BootApplicationId)
            throw new InvalidOperationException("The new vault's boot store was not written.");
    }

    /// <summary>What the D1 check of the new vault asks of this run (spec 13.2): the new and the old page keys, and the key service bound to the OLD boot store.</summary>
    public SealedRekeyCheckInput D1Input => new(NewKeys, Source, _options.KeyService);

    /// <summary>The run's own check: the new files open with the new vault's page keys. Run with every connection to the new files closed.</summary>
    public IReadOnlyList<RekeyProblem> VerifyNewFilesOpen(string newDir)
    {
        var problems = new List<RekeyProblem>();
        foreach (var (file, purpose) in new[] { (VaultStoreFiles.Database, DbPurposes.Vault), (VaultStoreFiles.Chat, DbPurposes.Chat) })
        {
            if (!File.Exists(Path.Combine(newDir, file))) continue;
            var key = NewKeys.GetPageKey(purpose);
            try
            {
                if (!Opens(Path.Combine(newDir, file), key)) problems.Add(new(file, "*", "does not open with the new vault's page key"));
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        return problems;
    }

    internal static bool Opens(string path, byte[] key)
    {
        try
        {
            using var conn = SealedSqlite.OpenKeyed(path, key, readOnly: true, busyTimeoutSeconds: 1);
            return true;
        }
        catch (VaultKeyMismatchException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_newDbk);
        NewKeys.Close();
    }
}
