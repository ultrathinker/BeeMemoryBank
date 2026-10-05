using System.Runtime.CompilerServices;
using BeeMemoryBank.Infrastructure.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// On a Mac the node's default secret store is the user's OWN login Keychain, and a started node reads it (the update handoff is tried first
/// at startup). A test node never reaches it: on a Mac every test host gets an in-memory store in its place. Nothing changes on Windows or
/// Linux. Installed once, when the test assembly loads, because <see cref="BmbWebApplicationFactory"/> is shared with projects that do not
/// reference Infrastructure.
/// </summary>
internal static class MacHostSafety
{
    [ModuleInitializer]
    internal static void Install()
    {
        if (!OperatingSystem.IsMacOS()) return;
        BmbWebApplicationFactory.HostSafety = services =>
            services.Replace(ServiceDescriptor.Singleton<IUserSecretStore>(new InMemoryUserSecretStore()));
    }
}
