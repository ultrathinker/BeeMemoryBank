using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ILazySlotRewrapService
{
    Task<LazyRewrapResult> TryRewrapAsync(MasterKeyStore slot, byte[] kek, byte[] unwrappedDek, byte[] currentSentinel);
}

public record LazyRewrapResult(bool Success, byte[]? RewrappedDek);
