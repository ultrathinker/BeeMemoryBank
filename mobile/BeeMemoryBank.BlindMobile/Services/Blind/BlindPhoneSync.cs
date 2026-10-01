using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>Runs the phone's pull-only protocol-3 sync through the call-code pinned client.</summary>
public sealed class BlindPhoneSync(
    BlindPhonePullClient client, BlindHttpClientProvider http, BlindPhoneState state) : IBlindPhoneSync
{
    public async Task SyncOnceAsync(BlindCallCode target, CancellationToken ct)
    {
        try
        {
            await client.SyncOnceAsync(http.GetClient(target.SpkiPin), target, ct);
        }
        catch (SnapshotRequiredException)
        {
            // The hourly heavy worker owns replica download and its Wi-Fi/charger gate. Marking
            // this stale position incomplete routes the next permitted heavy pass through it.
            state.InitialLoadDone = false;
            throw;
        }
    }
}
