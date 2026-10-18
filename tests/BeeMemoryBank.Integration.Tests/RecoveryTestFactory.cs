using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// An Api node for the blind-node recovery tests: a chosen machine kind and free memory (which strong
/// box preset it builds), and optionally the blind role.
/// </summary>
/// <remarks>
/// <paramref name="superadmin"/>: this node's standing in the network (its peers' my-standing). <paramref name="allowOpenCopies"/>: BMB-306 - no restore
/// reads a copy that holds the vault in the open, in any host a person can start. The recovery engine's own tests (key chain, anchors, clocks) run on
/// fixtures made before metadata encryption was switched on, so this test host replaces the product's restore service and the end of its restore with the
/// subclasses of this assembly (<see cref="OpenCopyRecoveryRestoreService"/>, <see cref="OpenCopyFreshNodeSealing"/>); the tests of the rule itself pass
/// false and see what a product host does.
/// </remarks>
public sealed class RecoveryTestFactory(
    RecoveryHostKind kind = RecoveryHostKind.Pc, long availableBytes = 1_900L << 20, bool blind = false, bool superadmin = true,
    Action<IServiceCollection>? services = null, bool allowOpenCopies = true)
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
#if !BLIND_NODE_HOST // the blind host has no recovery host (the strong box is built by a node that holds the master key)
            s.AddSingleton<IRecoveryHost>(new FixedHost(kind, availableBytes));
            if (allowOpenCopies)
            {
                // The reader of open copies and the end of a restore that leaves the node open live in this assembly only (OpenCopyRecoveryRestoreService);
                // the product's own services refuse open copies and always encrypt what they restore.
                s.Replace(ServiceDescriptor.Singleton<RecoveryRestoreService>(sp => ActivatorUtilities.CreateInstance<OpenCopyRecoveryRestoreService>(sp)));
                s.Replace(ServiceDescriptor.Scoped<BeeMemoryBank.Vault.Sealed.FreshNodeSealing>(sp => ActivatorUtilities.CreateInstance<OpenCopyFreshNodeSealing>(sp)));
            }
#endif
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
#if BLIND_NODE_HOST // the BlindNode.Tests project has no full node to initialize
        if (!blind) throw new NotSupportedException("A full node is not part of the blind host.");
#else
        if (!blind) return base.InitializeNodeAsync(displayName, password);
#endif
        _ = Services;
        return Task.CompletedTask;
    }

    private sealed class FixedStanding(bool superadmin) : IOwnStandingProvider
    {
        public Task<bool> IsSuperadminAsync(CancellationToken ct = default) => Task.FromResult(superadmin);
    }

#if !BLIND_NODE_HOST
    private sealed class FixedHost(RecoveryHostKind kind, long available) : IRecoveryHost
    {
        public RecoveryHostKind Kind => kind;
        public long AvailableMemoryBytes() => available;
    }
#endif
}
