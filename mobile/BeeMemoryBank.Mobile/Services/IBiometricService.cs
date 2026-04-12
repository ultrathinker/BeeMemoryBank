namespace BeeMemoryBank.Mobile.Services;

public interface IBiometricService
{
    /// <summary>Returns true if the device has biometric hardware with enrolled fingerprints.</summary>
    Task<bool> IsAvailableAsync();

    /// <summary>Returns true if an encrypted password is already stored.</summary>
    bool HasStoredPassword();

    /// <summary>
    /// Shows biometric prompt. On success decrypts and returns stored password.
    /// Returns null if user cancelled or authentication failed.
    /// </summary>
    Task<string?> GetPasswordAsync();

    /// <summary>
    /// Shows biometric prompt to authorize encryption. On success encrypts and stores the password.
    /// Returns true on success.
    /// </summary>
    Task<bool> StorePasswordAsync(string password);

    /// <summary>Deletes the stored encrypted password and the Keystore key.</summary>
    void Clear();
}
