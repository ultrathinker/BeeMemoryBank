using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

public class StrongBoxServiceTests
{
    [Fact]
    public async Task EnsureForCurrentKey_WhenTheSessionLocksDuringIdentityLookup_ReturnsWithoutThrowing()
    {
        var session = new SessionService(null!);
        session.UnlockWithDek(new byte[32]);
        using var services = new ServiceCollection()
            .AddSingleton<INodeIdentityRepository>(new LockingIdentityRepository(session))
            .BuildServiceProvider();
        var service = new StrongBoxService(
            services.GetRequiredService<IServiceScopeFactory>(), session, new NoBuildHost(),
            NullLogger<StrongBoxService>.Instance);

        await service.Invoking(s => s.EnsureForCurrentKeyAsync("irrelevant"))
            .Should().NotThrowAsync();
        session.IsUnlocked.Should().BeFalse();
    }

    private sealed class NoBuildHost : IRecoveryHost
    {
        public RecoveryHostKind Kind => RecoveryHostKind.Phone;
        public long AvailableMemoryBytes() => 0;
    }

    private sealed class LockingIdentityRepository(SessionService session) : INodeIdentityRepository
    {
        public Task<NodeIdentity?> GetAsync()
        {
            session.Lock();
            return Task.FromResult<NodeIdentity?>(new NodeIdentity());
        }

        public Task CreateAsync(NodeIdentity identity) => throw new NotSupportedException();
        public Task StoreSentinelAsync(byte[] sentinelValue) => throw new NotSupportedException();
        public Task<byte[]?> GetSentinelAsync() => throw new NotSupportedException();
        public Task MarkInitialSyncCompletedAsync() => throw new NotSupportedException();
        public Task UpgradePrivateKeyToV1Async(Guid nodeId, byte[] wrappedPrivateKey, byte[] iv) => throw new NotSupportedException();
        public Task<(int ExpireHours, bool SlidingExpiration)> GetSessionSettingsAsync() => throw new NotSupportedException();
        public Task SetSessionSettingsAsync(int expireHours, bool slidingExpiration) => throw new NotSupportedException();
        public Task<(DateTime ChangedAt, string ByNode)?> GetMasterPasswordNoticeAsync() => throw new NotSupportedException();
        public Task SetMasterPasswordNoticeAsync(DateTime changedAt, string byNode) => throw new NotSupportedException();
        public Task ClearMasterPasswordNoticeAsync() => throw new NotSupportedException();
        public Task<DateTime?> GetMasterPasswordChangedLocallyAtAsync() => throw new NotSupportedException();
        public Task SetMasterPasswordChangedLocallyAtAsync(DateTime at) => throw new NotSupportedException();
        public Task<string?> GetBrandNameAsync() => throw new NotSupportedException();
        public Task SetBrandNameAsync(string? brandName) => throw new NotSupportedException();
        public Task SetCanGenerateEmbeddingsAsync(bool enabled) => throw new NotSupportedException();
    }
}
