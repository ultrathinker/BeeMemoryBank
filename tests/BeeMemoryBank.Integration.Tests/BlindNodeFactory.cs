using System.Globalization;
using BeeMemoryBank.Api.Services;
using Microsoft.AspNetCore.Hosting;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// An Api host in the blind role (BMB_ROLE=blind, plan 3.4). Set through configuration rather than
/// the process environment so a blind and a full node can run side by side in one test process —
/// and rather than by replacing the INodeRole registration, because the role is read while the
/// services are registered (what a blind node leaves out is decided there).
/// </summary>
public class BlindNodeFactory : BmbWebApplicationFactory
{
    /// <summary>What the pair code tells a PC. Tests route the PC's outbound calls into this host.</summary>
    public const string PublicAddress = "https://blind.test:5610";

    private string? _nodeName;
    private readonly int? _drainWaitSeconds;

    /// <summary>Creates the host.</summary>
    /// <param name="drainWaitSeconds">
    /// Overrides how long a seed's switch waits for the live database's connections to close
    /// (<see cref="BlindSeedService.DefaultCutoverDrainWait"/> is a minute). A test that provokes the
    /// timeout should cost seconds, not a minute of wall clock; the default stays production's.
    /// </param>
    public BlindNodeFactory(int? drainWaitSeconds = null)
    {
        _drainWaitSeconds = drainWaitSeconds;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("BMB_ROLE", "blind");
        builder.UseSetting("BMB_PUBLIC_ADDRESS", PublicAddress);
        if (_nodeName is not null) builder.UseSetting("BMB_NODE_NAME", _nodeName);
        if (_drainWaitSeconds is { } seconds)
            builder.UseSetting(BlindSeedService.CutoverDrainWaitSecondsKey, seconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A blind node initializes itself at its first start, from its key (plan 3.4): there is no vault,
    /// user or password to create, and the ordinary initialization refuses it as already done. Starting
    /// the host is the whole setup. The name is given the way an operator gives it (BMB_NODE_NAME), so
    /// it has to come before the host starts; the password has nothing to protect on a blind node.
    /// </summary>
    public new Task InitializeNodeAsync(string displayName = "TestNode", string password = "testPassword")
    {
        _nodeName = displayName;
        _ = Services;
        return Task.CompletedTask;
    }
}
