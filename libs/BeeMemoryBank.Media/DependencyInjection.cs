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
    /// Registers the ImageSharp-backed <see cref="IImageTranscoder"/>. REQUIRED wherever
    /// <c>MediaService</c> can resolve: MediaService takes it as a non-optional constructor
    /// dependency, so a missing registration here fails at DI resolution rather than at first
    /// media upload. Hosts that do not resolve MediaService (Web proxy, plain CLI subcommands)
    /// can safely skip this; hosts that do (Api, Mobile) must call it.
    /// </summary>
    public static IServiceCollection AddImageTranscoder(this IServiceCollection services)
    {
        services.TryAddSingleton<IImageTranscoder, ImageSharpImageTranscoder>();
        return services;
    }
}