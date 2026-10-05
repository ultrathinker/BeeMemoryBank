using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Infrastructure.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Where the Api registers the OS secret store and the two services that put the master key back without a password
/// (<see cref="OsAutoUnlockService"/>, <see cref="UpdateUnlockHandoff"/>): exactly where the platform default store is supported and the
/// node is not blind - Windows (DPAPI) and macOS (Keychain), nowhere else. Only the container is inspected: the host is neither built nor
/// started and no secret-store method is called, so on a Mac the user's own Keychain is never reached.
/// </summary>
public class OsSecretStackRegistrationTests : IDisposable
{
    private readonly List<string> _dirs = [];

    /// <summary>Removes only the folders this test class made for itself.</summary>
    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private IServiceCollection Register(string? role)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_secretstack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Testing";
        if (role is not null) builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["BMB_ROLE"] = role });
        builder.AddBeeApiServices(dir);
        return builder.Services;
    }

    private static ServiceDescriptor? Descriptor(IServiceCollection services, Type type) => services.SingleOrDefault(d => d.ServiceType == type);

    [Fact]
    public void AFullNode_RegistersTheStackExactlyWhereTheOsHasASecretStore()
    {
        var services = Register(role: null);

        var store = Descriptor(services, typeof(IUserSecretStore));
        var autoUnlock = Descriptor(services, typeof(OsAutoUnlockService));
        var handoff = Descriptor(services, typeof(UpdateUnlockHandoff));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            store.Should().NotBeNull();
            autoUnlock.Should().NotBeNull();
            handoff.Should().NotBeNull();
            store!.Lifetime.Should().Be(ServiceLifetime.Singleton);
            // The factory ignores its provider; calling it builds the store object and makes no Keychain/DPAPI call.
            var instance = (IUserSecretStore)store.ImplementationFactory!(null!);
            instance.IsSupported.Should().BeTrue();
            if (OperatingSystem.IsMacOS()) instance.Should().BeOfType<MacOsKeychainUserSecretStore>();
            else instance.Should().BeOfType<WindowsDpapiUserSecretStore>();
            (instance as IDisposable)?.Dispose();
        }
        else
        {
            store.Should().BeNull();
            autoUnlock.Should().BeNull();
            handoff.Should().BeNull();
        }
    }

    [Fact]
    public void OnAMac_EveryTestHost_GetsAnInMemorySecretStore_SoNoTestNodeReachesTheLoginKeychain()
    {
        if (!OperatingSystem.IsMacOS())
        {
            BmbWebApplicationFactory.HostSafety.Should().BeNull("the hook is installed on a Mac only");
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IUserSecretStore>(_ => throw new InvalidOperationException("the login keychain must not be reached"));

        BmbWebApplicationFactory.HostSafety.Should().NotBeNull();
        BmbWebApplicationFactory.HostSafety!(services);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IUserSecretStore>().Should().BeOfType<InMemoryUserSecretStore>();
    }

    [Fact]
    public void ABlindNode_NeverRegistersTheStack_OnAnyOperatingSystem()
    {
        var services = Register(role: "blind");

        Descriptor(services, typeof(IUserSecretStore)).Should().BeNull();
        Descriptor(services, typeof(OsAutoUnlockService)).Should().BeNull();
        Descriptor(services, typeof(UpdateUnlockHandoff)).Should().BeNull();
    }
}
