using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Media;

/// <summary>
/// DI helpers for image transcoding.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the ImageSharp-backed <see cref="IImageTranscoder"/>. Hosts that need to
    /// transcode / downscale uploaded images (Api, Mobile) call this; hosts that don't (Web
    /// proxy, plain CLI subcommands) skip it and <c>MediaService</c> rejects oversize uploads
    /// cleanly.
    /// </summary>
    public static IServiceCollection AddImageTranscoder(this IServiceCollection services)
    {
        services.TryAddSingleton<IImageTranscoder, ImageSharpImageTranscoder>();
        return services;
    }
}