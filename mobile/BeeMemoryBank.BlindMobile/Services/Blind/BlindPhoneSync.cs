using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>Runs the phone's pull-only protocol-3 sync through the call-code pinned client.</summary>
public sealed class BlindPhoneSync(BlindPhonePullClient client, BlindHttpClientProvider http) : IBlindPhoneSync
{
    public Task SyncOnceAsync(BlindCallCode target, CancellationToken ct) =>
        client.SyncOnceAsync(http.GetClient(target.SpkiPin), target, ct);
}
