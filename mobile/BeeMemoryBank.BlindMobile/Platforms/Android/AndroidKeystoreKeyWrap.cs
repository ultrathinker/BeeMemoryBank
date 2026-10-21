using Android.Security.Keystore;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>
/// The Android blind node's secrets are wrapped by non-exportable AES-256 keys of the AndroidKeyStore (plan 3.5 "key outside the DEK"): one key per alias, no screen unlock needed
/// (background work uses them). The blob is IV(12) || ciphertext; the associated data binds it to its purpose and to its account. Which alias, which data and which file belong
/// to which account is <see cref="KeystoreBlindSecretStoreFactory"/> and <see cref="BlindAccountNames"/>; this class is only the Keystore calls.
/// </summary>
public sealed class AndroidKeystoreKeyWrap : IBlindKeyWrap
{
    public byte[] Wrap(string alias, byte[] aad, byte[] secret)
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey(alias));
        cipher.UpdateAAD(aad);
        var encrypted = cipher.DoFinal(secret)!;
        var iv = cipher.GetIV()!;
        return BlindKeystoreBlobLayout.Join(iv, encrypted);
    }

    public byte[] Unwrap(string alias, byte[] aad, byte[] blob)
    {
        // A cut-off or damaged file is an error with a sentence (the screen reports it as "key storage unavailable"), not a slicing exception.
        var (iv, offset, length) = BlindKeystoreBlobLayout.Split(blob);
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(alias), new GCMParameterSpec(128, iv));
        cipher.UpdateAAD(aad);
        return cipher.DoFinal(blob, offset, length)!;
    }

    public void DeleteKey(string alias)
    {
        var ks = KeyStore.GetInstance("AndroidKeyStore")!;
        ks.Load(null);
        if (!ks.ContainsAlias(alias)) return;
        ks.DeleteEntry(alias);
        // "Absent" is confirmed, not assumed: a removal that is reported done must not leave a key behind (the host retries while this throws).
        if (ks.ContainsAlias(alias)) throw new InvalidOperationException("The Keystore still holds the entry after it was deleted.");
    }

    private static IKey GetOrCreateKey(string alias)
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
