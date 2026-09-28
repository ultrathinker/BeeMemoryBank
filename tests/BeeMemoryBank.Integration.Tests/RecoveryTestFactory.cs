using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// An Api node for the blind-node recovery tests: a chosen machine kind and free memory (which strong
/// box preset it builds), and optionally the blind role.
/// </summary>
/// <remarks><paramref name="superadmin"/>: this node's standing in the network (its peers' my-standing).</remarks>
public sealed class RecoveryTestFactory(
    RecoveryHostKind kind = RecoveryHostKind.Pc, long availableBytes = 1_900L << 20, bool blind = false, bool superadmin = true,
    Action<IServiceCollection>? services = null)
    : BmbWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (blind)
        {
            // The blind role as an operator sets it (see BlindNodeFactory): read while services are registered.
            builder.UseSetting("BMB_ROLE", "blind");
            builder.UseSetting("BMB_PUBLIC_ADDRESS", BlindNodeFactory.PublicAddress);
        }
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<IRecoveryHost>(new FixedHost(kind, availableBytes));
            s.AddSingleton<IOwnStandingProvider>(new FixedStanding(superadmin));
            if (blind) s.AddSingleton<INodeRole>(new EnvironmentNodeRole("blind"));
            services?.Invoke(s);
        });
    }

    /// <summary>
    /// A blind node initializes itself at its first start (BlindRoleStartup) and never unlocks: starting the
    /// host is its whole setup. A full node goes through the ordinary initialization.
    /// </summary>
    public new Task InitializeNodeAsync(string displayName = "TestNode", string password = "testPassword")
    {
        if (!blind) return base.InitializeNodeAsync(displayName, password);
        _ = Services;
        return Task.CompletedTask;
    }

    private sealed class FixedStanding(bool superadmin) : IOwnStandingProvider
    {
        public Task<bool> IsSuperadminAsync(CancellationToken ct = default) => Task.FromResult(superadmin);
    }

    private sealed class FixedHost(RecoveryHostKind kind, long available) : IRecoveryHost
    {
        public RecoveryHostKind Kind => kind;
        public long AvailableMemoryBytes() => available;
    }
}
