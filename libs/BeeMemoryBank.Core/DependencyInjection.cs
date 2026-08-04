using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton<SessionService>();
        services.AddSingleton<InvisibleModeService>();
        services.AddSingleton<MaintenanceModeService>();

        // Null implementations are replaced with real ones when BeeMemoryBank.Sync / Api / Cli is registered
        services.TryAddSingleton<ILamportClock, NullLamportClock>();
        services.TryAddScoped<IEventLogger, NullEventLogger>();
        services.TryAddSingleton<IActorProvider>(new NullActorProvider());

        services.AddScoped<InitializationService>();
        services.AddScoped<ArticleService>();
        services.AddScoped<ArticleDiffService>();
        services.AddScoped<KeyManagementService>();
        services.AddScoped<TreeService>();
        services.AddScoped<SearchService>();
        services.AddScoped<EmbeddingProjectionService>();
        services.AddScoped<FolderService>();
        services.AddScoped<CopyService>();
        services.AddScoped<CommentService>();
        services.AddScoped<MediaService>();
        services.AddScoped<UserService>();
        services.AddScoped<FolderAccessService>();
        services.AddScoped<ConceptTagService>();
        services.AddScoped<ObsidianImportService>();
        services.AddScoped<BeeImportService>();
        services.AddScoped<RestoreService>();
        services.AddScoped<LegacyPasswordSlotMigrationService>();
        services.AddScoped<RemoteAccountService>();
        services.AddScoped<RemoteEventApplier>();
        return services;
    }

    public static IServiceCollection AddOnnxEmbeddings(this IServiceCollection services, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        // Resolve + verify the model up front via ModelManager (its SHA-256 result is cached on disk
        // after the first run). Only a Valid resolution hands the real path to OnnxEmbeddingGenerator;
        // Corrupt and NotFound both yield a non-existent sentinel path so the generator degrades to
        // ModelUnavailableException exactly as it already does for a missing model, and a corrupt file
        // is never loaded into the ONNX runtime. See EmbeddingModelWiring for the placeholder hash.
        var manager = new ModelManager(EmbeddingModelWiring.DefaultManifest, dataDirectory);
        var generatorPath =
            EmbeddingModelWiring.ResolveGeneratorPathAsync(manager).GetAwaiter().GetResult();
        services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(generatorPath));
        return services;
    }

    public static IServiceCollection AddOnnxEmbeddings(this IServiceCollection services, byte[] modelBytes)
    {
        services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(modelBytes));
        return services;
    }

    // ── mDNS / DNS-SD LAN discovery (TASK_BRIEF §5 Этап 5) ──────────────────────
    // mDNS services live in Core (not Sync) so BOTH the API host (which runs the MdnsAnnouncer
    // alongside SyncScheduler, next to the authoritative InvisibleModeService + node identity)
    // and the Web host (which needs the MdnsBrowser for the Setup join wizard) can use them via the
    // Core project reference they already carry — no new project-graph edges required.

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
    /// mDNS. Call this from the host that owns the authoritative
    /// <see cref="InvisibleModeService"/> and the node identity (the API).
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
