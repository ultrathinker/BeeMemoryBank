using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The platform key that wraps a secret: on Android a non-exportable AES-256 key of the AndroidKeyStore under an alias, used with AES-GCM and associated data.
/// This is the only part of the secret store that needs Android; everything else (which file, which alias, which associated data, what a removal deletes) is
/// <see cref="KeystoreBlindSecretStoreFactory"/>, which has no Android type in it and is tested on any OS against a software key.
/// </summary>
public interface IBlindKeyWrap
{
    /// <summary>Wraps <paramref name="secret"/> under the key of <paramref name="alias"/> (made when it does not exist) bound to <paramref name="aad"/>; the result is <see cref="BlindKeystoreBlobLayout"/>'s IV + ciphertext.</summary>
    byte[] Wrap(string alias, byte[] aad, byte[] secret);

    /// <summary>The secret inside <paramref name="blob"/>; throws (a <see cref="System.Security.Cryptography.CryptographicException"/> or a platform exception) when the key, the associated data or the content do not fit.</summary>
    byte[] Unwrap(string alias, byte[] aad, byte[] blob);

    /// <summary>Deletes the key of <paramref name="alias"/>; nothing happens when there is none. Throws when the platform did not delete it (or did not answer): the caller must not take the key as gone.</summary>
    void DeleteKey(string alias);
}

/// <summary>
/// The blind app's secrets on Android, one set per account. Each secret (identity seed, pairing secret, backup key) is wrapped by <see cref="IBlindKeyWrap"/> under an alias of its own and written as
/// IV + ciphertext to a file. The NAMES come from <see cref="BlindAccountNames"/> (the naming contract): the account of a 2.6 install keeps today's aliases, associated data and file names (the blobs
/// in the app's root), so an upgrade and a rollback find everything where it was; every other account's alias and associated data carry its id (<c>bmb_blind_seed_v1.&lt;id&gt;</c>,
/// <c>bmb-blind-identity-seed-v1/&lt;id&gt;</c>) and its blobs are in <c>accounts/&lt;id&gt;/keys/</c>, so a blob copied into another account's slot does not open there.
/// </summary>
public sealed class KeystoreBlindSecretStoreFactory(string root, IBlindKeyWrap wrap) : IBlindSecretStoreFactory, IBlindSecretBlobAccess
{
    /// <summary>The folder of a new account's blobs, inside its own folder.</summary>
    public const string KeysFolder = "keys";

    public IBlindSecretStore For(BlindAccountEntry account) => new Store(root, account, wrap);

    public byte[]? ReadBlob(BlindAccountEntry account, BlindAccountNames.Role role)
    {
        var path = BlobPath(root, account, role);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void WriteBlob(BlindAccountEntry account, BlindAccountNames.Role role, byte[] blob)
    {
        var path = BlobPath(root, account, role);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, blob);
    }

    /// <summary>Where the wrapped blob of <paramref name="role"/> is: the app's root for the account of a 2.6 install, <c>accounts/&lt;id&gt;/keys</c> otherwise.</summary>
    internal static string BlobPath(string root, BlindAccountEntry account, BlindAccountNames.Role role)
    {
        var folder = BlindAccountNames.DataDirectory(root, account);
        return Path.Combine(account.IsLegacy ? folder : Path.Combine(folder, KeysFolder), BlindAccountNames.AndroidBlobFile(role));
    }

    private sealed class Store(string root, BlindAccountEntry account, IBlindKeyWrap wrap) : IBlindSecretStore
    {
        public void SaveIdentitySeed(byte[] seed) => Save(BlindAccountNames.Role.IdentitySeed, seed);
        public byte[]? LoadIdentitySeed() => Load(BlindAccountNames.Role.IdentitySeed);
        public void SaveBackupKey(byte[] key) => Save(BlindAccountNames.Role.BackupKey, key);
        public byte[]? LoadBackupKey() => Load(BlindAccountNames.Role.BackupKey);
        public void SavePairingSecret(byte[] secret) => Save(BlindAccountNames.Role.PairingSecret, secret);
        public byte[]? LoadPairingSecret() => Load(BlindAccountNames.Role.PairingSecret);
        public void ClearPairingSecret() => Remove([BlindAccountNames.Role.PairingSecret]);

        public void Clear() => Remove(Enum.GetValues<BlindAccountNames.Role>());

        private string PathOf(BlindAccountNames.Role role) => BlobPath(root, account, role);

        private void Save(BlindAccountNames.Role role, byte[] secret)
        {
            var path = PathOf(role);
            var folder = Path.GetDirectoryName(path)!;
            // An account's folder is made by the host before its first key. A key saved after the account was removed must not bring its folder back.
            if (!account.IsLegacy && !Directory.Exists(BlindAccountNames.DataDirectory(root, account)))
                throw new DirectoryNotFoundException("The folder of this account is not there.");
            Directory.CreateDirectory(folder);

            var blob = wrap.Wrap(BlindAccountNames.AndroidAlias(account, role), BlindAccountNames.AndroidAad(account, role), secret);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);
        }

        private byte[]? Load(BlindAccountNames.Role role)
        {
            var path = PathOf(role);
            if (!File.Exists(path)) return null;
            // A cut-off or damaged file is an error with a sentence (the wrap splits it with BlindKeystoreBlobLayout, which names it), not a slicing exception.
            var blob = File.ReadAllBytes(path);
            return wrap.Unwrap(BlindAccountNames.AndroidAlias(account, role), BlindAccountNames.AndroidAad(account, role), blob);
        }

        /// <summary>
        /// Deletes the blob and the Keystore entry of every role in <paramref name="roles"/>, each role even when another failed, so that one stuck file or entry does not leave the others behind. A blob that cannot be
        /// deleted and an entry the platform did not delete are both FAILURES, thrown together after the last role: the host keeps the account in <c>deleting</c> and runs the removal again at the next start, so a removal is
        /// never reported done while a key is still there. Deleting an entry that does not exist is not a failure (it is the retry after a half-done removal). The messages say which role and which kind, never the platform's
        /// own text (an alias, a path).
        /// </summary>
        private void Remove(IEnumerable<BlindAccountNames.Role> roles)
        {
            List<Exception>? failures = null;
            foreach (var role in roles)
            {
                // The file first: a key that is left over opens nothing, a file that is left over is a secret at rest. The entry is deleted whether or not the file went: a blob under a deleted key is noise.
                try
                {
                    var path = PathOf(role);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    (failures ??= []).Add(new IOException($"the key file of the {Describe(role)} could not be deleted", ex));
                }
                try { wrap.DeleteKey(BlindAccountNames.AndroidAlias(account, role)); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    (failures ??= []).Add(new IOException($"the Keystore entry of the {Describe(role)} could not be deleted", ex));
                }
            }
            if (failures is not null) throw new AggregateException("The keys of this account were not all removed.", failures);
        }

        private static string Describe(BlindAccountNames.Role role) => role switch
        {
            BlindAccountNames.Role.IdentitySeed => "identity seed",
            BlindAccountNames.Role.PairingSecret => "pairing secret",
            BlindAccountNames.Role.BackupKey => "backup key",
            _ => "key",
        };
    }
}
