using Android.Security.Keystore;
using AndroidX.Biometric;
using AndroidX.Fragment.App;
using BeeMemoryBank.Mobile.Services;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace BeeMemoryBank.Mobile.Platforms.Android;

public class BiometricService : IBiometricService
{
    private const string KeyAlias = "bmb_biometric_key_v1";
    private const string EncPassFile = "bmb_enc_pass.bin";
    private const string IvFile = "bmb_enc_iv.bin";

    private string FilePath(string name) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);

    public Task<bool> IsAvailableAsync()
    {
        var mgr = BiometricManager.From(Platform.AppContext);
        var result = mgr.CanAuthenticate(BiometricManager.Authenticators.BiometricStrong);
        return Task.FromResult(result == BiometricManager.BiometricSuccess);
    }

    public bool HasStoredPassword() =>
        File.Exists(FilePath(EncPassFile)) && File.Exists(FilePath(IvFile));

    // ── Keystore ──────────────────────────────────────────────────────────────

    private IKey GetOrCreateKey()
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
                .SetUserAuthenticationRequired(true)
                .SetInvalidatedByBiometricEnrollment(true)
                .Build();

            kg.Init(spec);
            kg.GenerateKey();
        }

        return ks.GetKey(KeyAlias, null)!;
    }

    private Cipher CreateEncryptCipher()
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        return cipher;
    }

    private Cipher CreateDecryptCipher()
    {
        var iv = File.ReadAllBytes(FilePath(IvFile));
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, iv));
        return cipher;
    }

    // ── BiometricPrompt ───────────────────────────────────────────────────────

    private Task<BiometricPrompt.AuthenticationResult?> ShowPromptAsync(
        Cipher cipher, string title, string subtitle)
    {
        var tcs = new TaskCompletionSource<BiometricPrompt.AuthenticationResult?>();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var activity = (FragmentActivity)Platform.CurrentActivity!;
            var executor = activity.MainExecutor!;

            var callback = new AuthCallback(
                onSuccess: r => tcs.TrySetResult(r),
                onFailed: () => { /* finger not recognised — let user retry, don't resolve yet */ },
                onError: (_, _) => tcs.TrySetResult(null));   // cancelled / lockout

            var prompt = new BiometricPrompt(activity, executor, callback);

            var info = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle(title)
                .SetSubtitle(subtitle)
                .SetNegativeButtonText("Use password")
                .Build();

            prompt.Authenticate(info, new BiometricPrompt.CryptoObject(cipher));
        });

        return tcs.Task;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<bool> StorePasswordAsync(string password)
    {
        try
        {
            var cipher = CreateEncryptCipher();
            var result = await ShowPromptAsync(
                cipher, "Enable fingerprint unlock", "Touch the sensor to confirm");

            if (result?.CryptoObject?.Cipher is not { } auth) return false;

            var encrypted = auth.DoFinal(System.Text.Encoding.UTF8.GetBytes(password))!;
            File.WriteAllBytes(FilePath(EncPassFile), encrypted);
            File.WriteAllBytes(FilePath(IvFile), auth.GetIV()!);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BiometricService.Store error: {ex.Message}");
            return false;
        }
    }

    public async Task<string?> GetPasswordAsync()
    {
        if (!HasStoredPassword()) return null;
        try
        {
            var cipher = CreateDecryptCipher();
            var result = await ShowPromptAsync(
                cipher, "BeeMemoryBank", "Use fingerprint to unlock");

            if (result?.CryptoObject?.Cipher is not { } auth) return null;

            var encrypted = File.ReadAllBytes(FilePath(EncPassFile));
            var decrypted = auth.DoFinal(encrypted)!;
            return System.Text.Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BiometricService.Get error: {ex.Message}");
            return null;
        }
    }

    public void Clear()
    {
        foreach (var name in new[] { EncPassFile, IvFile })
        {
            var path = FilePath(name);
            if (File.Exists(path)) File.Delete(path);
        }
        try
        {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);
            if (ks.ContainsAlias(KeyAlias)) ks.DeleteEntry(KeyAlias);
        }
        catch { }
    }

    // ── Callback helper ───────────────────────────────────────────────────────

    private sealed class AuthCallback(
        Action<BiometricPrompt.AuthenticationResult> onSuccess,
        Action onFailed,
        Action<int, string?> onError)
        : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
            => onSuccess(result);

        public override void OnAuthenticationFailed()
            => onFailed();

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence? errString)
            => onError(errorCode, errString?.ToString());
    }
}
