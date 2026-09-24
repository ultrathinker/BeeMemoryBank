using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Infrastructure.Mdns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Infrastructure;

/// <summary>
/// DI helpers for the Infrastructure project: mDNS / DNS-SD LAN discovery, image transcoding.
/// Wave 2 A2 carved these out of Core so hosts that only need the kernel (Core + Crypto + Search)
/// no longer pay for ACME, mDNS, ImageSharp, DPAPI or UPnP through Core. Each helper below was
/// previously an extension on BeeMemoryBank.Core.DependencyInjection and moved here verbatim.
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

    /// <summary>
    /// Registers the ImageSharp-backed <see cref="IImageTranscoder"/>. Hosts that need to
    /// transcode / downscale uploaded images (Api, Mobile) call this; hosts that don't (Web
    /// proxy, plain CLI subcommands) skip it and <c>MediaService</c> rejects oversize uploads
    /// cleanly.
    /// </summary>
    public static IServiceCollection AddImageTranscoder(this IServiceCollection services)
    {
        services.TryAddSingleton<IImageTranscoder, Media.ImageSharpImageTranscoder>();
        return services;
    }
}