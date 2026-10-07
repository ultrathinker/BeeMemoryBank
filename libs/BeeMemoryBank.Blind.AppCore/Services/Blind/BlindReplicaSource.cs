using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Android adapter for the verified replica client. The only client it supplies is the strict
/// client the call code asks for (SPKI-pinned, or the system certificate chain for a public-CA node); the generic sync
/// implementation never decides TLS trust itself.
/// </summary>
public sealed class BlindReplicaSource(BlindPhoneReplicaClient client, BlindHttpClientProvider http) : IBlindReplicaSource
{
    public Task FetchAndInstallAsync(
        BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct) =>
        client.FetchAndInstallAsync(http.GetClient(target), target, workDirectory, progress, ct);
}
