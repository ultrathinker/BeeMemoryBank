using Android.Security.Keystore;
using BeeMemoryBank.Core.Services.BlindPhone;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>
/// The Android blind node's secrets in the AndroidKeyStore (plan 3.5 "key outside the DEK").
/// The identity seed, backup key and pairing secret each have their own non-exportable Keystore key.
/// </summary>
public sealed class KeystoreBlindPhoneKeys : IBlindPhoneKeys
{
    private readonly KeystoreBlob _seed = new("bmb_blind_seed_v1", "bmb_blind_seed.bin", "bmb-blind-identity-seed-v1"u8.ToArray());
    private readonly KeystoreBlob _pairing = new("bmb_blind_pairing_v1", "bmb_blind_pairing.bin", "bmb-blind-pairing-v1"u8.ToArray());
    private readonly KeystoreBlob _backup = new("bmb_blind_backup_v1", "bmb_blind_backup.bin", "bmb-blind-backup-key-v1"u8.ToArray());

    public void SaveIdentitySeed(byte[] seed) => _seed.Save(seed);
    public byte[]? LoadIdentitySeed() => _seed.Load();
    public void SaveBackupKey(byte[] key) => _backup.Save(key);
    public byte[]? LoadBackupKey() => _backup.Load();
    public void SavePairingSecret(byte[] secret) => _pairing.Save(secret);
    public byte[]? LoadPairingSecret() => _pairing.Load();
    public void ClearPairingSecret() => _pairing.Clear();

    public void Clear()
    {
        _seed.Clear();
        _pairing.Clear();
        _backup.Clear();
    }
}

/// <summary>
/// One secret, AES-256-GCM-wrapped under a non-exportable AndroidKeyStore key that needs no screen
/// unlock (background work uses it). File = IV(12) || ciphertext, written atomically; a fixed AAD
/// binds the blob to its purpose.
/// </summary>
internal sealed class KeystoreBlob(string alias, string fileName, byte[] aad)
{
    private const int IvLength = 12;

    private string FilePath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), fileName);

    public void Save(byte[] secret)
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        cipher.UpdateAAD(aad);
        var encrypted = cipher.DoFinal(secret)!;
        var iv = cipher.GetIV()!;
        var blob = new byte[iv.Length + encrypted.Length];
        iv.CopyTo(blob, 0);
        encrypted.CopyTo(blob, iv.Length);

        var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public byte[]? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var blob = File.ReadAllBytes(FilePath);
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, blob[..IvLength]));
        cipher.UpdateAAD(aad);
        return cipher.DoFinal(blob, IvLength, blob.Length - IvLength);
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        try
        {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);
            if (ks.ContainsAlias(alias)) ks.DeleteEntry(alias);
        }
        catch { /* best effort: the file is gone, the key alone opens nothing */ }
    }

    private IKey GetOrCreateKey()
    {
        var ks = KeyStore.GetInstance("AndroidKeyStore")!;
        ks.Load(null);
        if (!ks.ContainsAlias(alias))
        {
            var kg = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
            kg.Init(new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes("GCM")
                .SetEncryptionPaddings("NoPadding")
                .SetKeySize(256)
                .SetUserAuthenticationRequired(false)
                .Build());
            kg.GenerateKey();
        }
        return ks.GetKey(alias, null)!;
    }
}
