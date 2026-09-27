using BeeMemoryBank.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A test node in the blind role. The role comes through DI, not BMB_ROLE: the environment is
/// global to the test process and would turn every other test class's node blind too.
/// </summary>
public class BlindNodeFactory : BmbWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<INodeRole>(new EnvironmentNodeRole("blind")));
    }
}
