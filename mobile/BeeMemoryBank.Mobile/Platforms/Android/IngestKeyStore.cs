using Android.Security.Keystore;
using BeeMemoryBank.Mobile.Services;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// Android Keystore implementation of <see cref="IIngestKeyStore"/>. The node's Ed25519 seed is
/// AES-256-GCM-encrypted under a non-exportable Keystore key created WITHOUT
/// <c>SetUserAuthenticationRequired</c>, so the background foreground-service can decrypt it
/// unattended (locked screen, post-reboot) to authenticate sync. The wrapping key never leaves
/// the TEE; a copied database/file is useless without the device.
///
/// Storage is a single file holding IV(12) || ciphertext, written atomically (tmp + rename) so a
/// crash/kill between writes can never leave a desynchronised IV and ciphertext (which would make
/// GCM decryption fail permanently). A constant AAD binds the blob to this purpose so a ciphertext
/// from another store/installation cannot be swapped in without a GCM tag failure.
///
/// NOTE (platform limitation): Cipher.DoFinal returns a Java byte[]; the MAUI interop copies it to
/// a C# array. We zero the C# copy, but the Java-heap plaintext lingers until ART GC — unavoidable
/// in pure C#. Same limitation already applies to BiometricService. Acceptable: the seed only
/// authorises sync transport and cannot decrypt content.
/// </summary>
public sealed class IngestKeyStore : IIngestKeyStore
{
    private const string KeyAlias = "bmb_ingest_key_v1";
    private const string BlobFile = "bmb_ingest.bin";
    private const int IvLength = 12; // AES-GCM IV from AndroidKeyStore is always 12 bytes
    private static readonly byte[] Aad = "bmb-ingest-key-v1"u8.ToArray();

    private static string FilePath(string name) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);

    public bool HasEnrolledKey()
    {
        if (!File.Exists(FilePath(BlobFile))) return false;
        try
        {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);
            return ks.ContainsAlias(KeyAlias);
        }
        catch { return false; }
    }

    private static IKey GetOrCreateKey()
    {
        var ks = KeyStore.GetInstance("AndroidKeyStore")!;
        ks.Load(null);

        if (!ks.ContainsAlias(KeyAlias))
        {
            var kg = KeyGenerator.GetInstance(
                KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;

            var spec = new KeyGenParameterSpec.Builder(KeyAlias,
                    KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes("GCM")
                .SetEncryptionPaddings("NoPadding")
                .SetKeySize(256)
                .SetUserAuthenticationRequired(false) // background ingest must work while locked
                .Build();

            kg.Init(spec);
            kg.GenerateKey();
        }

        return ks.GetKey(KeyAlias, null)!;
    }

    public void Enroll(byte[] rawNodePrivateKey)
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        cipher.UpdateAAD(Aad);
        var encrypted = cipher.DoFinal(rawNodePrivateKey)!;
        var iv = cipher.GetIV()!;

        // Single blob: IV || ciphertext. Atomic write (tmp + rename) — never leave IV/ct desynced.
        var blob = new byte[iv.Length + encrypted.Length];
        iv.CopyTo(blob, 0);
        encrypted.CopyTo(blob, iv.Length);

        var path = FilePath(BlobFile);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    public byte[] UnwrapNodePrivateKey()
    {
        var blob = File.ReadAllBytes(FilePath(BlobFile));
        var iv = blob[..IvLength];

        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, iv));
        cipher.UpdateAAD(Aad);
        return cipher.DoFinal(blob, IvLength, blob.Length - IvLength)!;
    }

    public void Clear()
    {
        var path = FilePath(BlobFile);
        if (File.Exists(path)) File.Delete(path);
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        try
        {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);
            if (ks.ContainsAlias(KeyAlias)) ks.DeleteEntry(KeyAlias);
        }
        catch { }
    }
}
