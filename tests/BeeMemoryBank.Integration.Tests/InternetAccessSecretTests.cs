using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Integration.Tests;

public sealed class InternetAccessSecretTests
{
    [Fact]
    public void DdnsStoreHelpers_RoundTripAndFailClosedWhenTheItemIsMissing()
    {
        var store = new InMemoryUserSecretStore();
        var locator = InternetAccessEndpoints.StoreDdnsSecret(store, "token", "ddns-token-value");

        locator.Should().Be("secret:token");
        InternetAccessEndpoints.ReadDdnsSecret(store, "token", locator).Should().Be("ddns-token-value");
        store.Delete("ddns-token", "token");

        var action = () => InternetAccessEndpoints.ReadDdnsSecret(store, "token", locator);
        action.Should().Throw<UserSecretStoreException>();
    }

    [Fact]
    public void DdnsLegacyWindowsValue_RemainsReadable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var plain = "legacy-ddns-token";
        var bytes = Encoding.UTF8.GetBytes(plain);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        var legacy = Convert.ToBase64String(protectedBytes);

        InternetAccessEndpoints.ReadDdnsSecret(new InMemoryUserSecretStore(), "token", legacy).Should().Be(plain);
    }
}
