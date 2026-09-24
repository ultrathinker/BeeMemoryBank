using BeeMemoryBank.Infrastructure.Mdns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Infrastructure;

/// <summary>
/// DI helpers for the Infrastructure project: mDNS / DNS-SD LAN discovery.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the <see cref="MdnsBrowser"/> singleton used by the join wizard to discover peer
    /// nodes on the LAN. Call this from the Web host.
    /// </summary>
    public static IServiceCollection AddMdnsBrowser(this IServiceCollection services)
    {
        services.TryAddSingleton<MdnsBrowser>();
        return services;
    }

    /// <summary>
    /// Registers the <see cref="MdnsAnnouncer"/> background service that advertises this node via
    /// mDNS. Call this from the host that owns the authoritative invisible-mode service and node
    /// identity (the API).
    /// </summary>
    public static IServiceCollection AddMdnsAnnouncer(
        this IServiceCollection services, Action<MdnsAnnouncerOptions>? configure = null)
    {
        var options = new MdnsAnnouncerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddHostedService<MdnsAnnouncer>();
        return services;
    }
}