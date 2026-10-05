namespace BeeMemoryBank.Infrastructure.Secrets;

/// <summary>Creates the user secret store appropriate for the current platform.</summary>
public static class UserSecretStores
{
    /// <summary>
    /// Windows: the DPAPI store (files under <paramref name="dataPath"/>); macOS: the Keychain store, scoped to the vault in
    /// <paramref name="dataPath"/> by the random id kept in that folder (read or created at the first use, not here), so that two data
    /// folders (two vaults or profiles) never share an item while a renamed or moved folder keeps its secrets; every other platform: a
    /// store that says it is unavailable.
    /// </summary>
    public static IUserSecretStore CreateDefault(string dataPath)
    {
        if (OperatingSystem.IsWindows()) return new WindowsDpapiUserSecretStore(dataPath);
        if (OperatingSystem.IsMacOS()) return MacOsKeychainUserSecretStore.ForDataPath(dataPath);
        return new UnsupportedUserSecretStore();
    }
}

/// <summary>A typed unavailable store used until a platform supplies a native secret backend.</summary>
public sealed class UnsupportedUserSecretStore : IUserSecretStore
{
    public bool IsSupported => false;

    public byte[]? Read(string purpose, string account) => throw Unavailable();

    public void Write(string purpose, string account, ReadOnlySpan<byte> value) => throw Unavailable();

    public void Delete(string purpose, string account) => throw Unavailable();

    private static UserSecretStoreException Unavailable() =>
        new(UserSecretStoreFailureKind.Unavailable, "The user secret store is unavailable on this platform.");
}
