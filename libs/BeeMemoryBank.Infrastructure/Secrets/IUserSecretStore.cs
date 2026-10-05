namespace BeeMemoryBank.Infrastructure.Secrets;

/// <summary>Stores small user-bound secrets outside application data files.</summary>
public interface IUserSecretStore
{
    /// <summary>Whether this host has a usable OS-backed secret store.</summary>
    bool IsSupported { get; }

    /// <summary>Returns null only when the named secret does not exist.</summary>
    byte[]? Read(string purpose, string account);

    /// <summary>Writes a secret bound to both its purpose and account.</summary>
    void Write(string purpose, string account, ReadOnlySpan<byte> value);

    /// <summary>Deletes a named secret. A missing secret is not an error.</summary>
    void Delete(string purpose, string account);
}
