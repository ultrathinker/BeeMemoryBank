using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Core.Tests;

public sealed class UserSecretStoresTests
{
    [Fact]
    public void DefaultStore_MatchesTheCurrentPlatform()
    {
        var store = UserSecretStores.CreateDefault(Path.GetTempPath());

        if (OperatingSystem.IsWindows())
        {
            store.Should().BeOfType<WindowsDpapiUserSecretStore>();
            store.IsSupported.Should().BeTrue();
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // Only the type and the flag: no call is made on this store, because the default store of a Mac is the user's own Keychain
            // (the behavior of the Keychain store is tested against a throwaway keychain file, never this object).
            store.Should().BeOfType<MacOsKeychainUserSecretStore>();
            store.IsSupported.Should().BeTrue();
            return;
        }

        store.Should().BeOfType<UnsupportedUserSecretStore>();
        store.IsSupported.Should().BeFalse();
        var unavailable = new[]
        {
            () => store.Read("test", "default"),
            () => { store.Write("test", "default", new byte[] { 1 }); return (byte[]?)null; },
            () => { store.Delete("test", "default"); return (byte[]?)null; },
        };
        foreach (var action in unavailable)
            action.Should().Throw<UserSecretStoreException>()
                .Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
    }
}
