using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>Receives one authenticated, pull-only protocol-3 page for a blind phone.</summary>
public interface IBlindPhonePullClient
{
    Task SyncOnceAsync(HttpClient http, BlindCallCode target, CancellationToken ct);
}
