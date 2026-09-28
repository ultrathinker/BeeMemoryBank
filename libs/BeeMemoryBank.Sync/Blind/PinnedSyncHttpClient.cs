using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Blind;

public static class PinnedSyncHttpClient
{
    /// <summary>
    /// Makes this client's primary handler the pinned one (<see cref="SpkiPinRegistry.CreateHandler"/>):
    /// pinned peers on their key only, HTTPS only, no redirects. Every client a node syncs through
    /// uses it — the scheduler's (AddSync) and, on the phone, the default client its sync pages and
    /// background worker take from the factory (review L-stage1 #6).
    /// </summary>
    public static IHttpClientBuilder UsePinnedSyncHandler(this IHttpClientBuilder builder) =>
        builder.ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<SpkiPinRegistry>().CreateHandler());
}
