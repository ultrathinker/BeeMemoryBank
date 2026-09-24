using System.Net;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A remote-account token write that raced a DEK rotation.
///
/// <para>The rotation re-encrypts every token already in <c>tbl_remote_account</c> inside its
/// transaction. A credential refresh that took the master DEK BEFORE the rotation but reaches the
/// database AFTER the rotation committed — and before the session swapped to the new DEK — used to
/// store a token sealed under the retired key. The rewrap had already run, so nothing fixed it, and
/// after the next restart the account could not authenticate. The write now re-checks the sentinel
/// inside its own transaction and re-seals under the swapped-in DEK.</para>
/// </summary>
public class RemoteAccountTokenRotationRaceTests : SyncTestFixture
{
    private sealed class TokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"token":"bmbrt_race_token","expiresAt":"2030-01-01T00:00:00Z","userId":1,"username":"u"}""",
                    Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// Runs <paramref name="beforeFirstWrite"/> right before the first token write reaches the
    /// database — the moment the rotation commits in the race — and records what the guard decided.
    /// </summary>
    private sealed class BarrierRepository(IRemoteAccountRepository inner, Func<Task> beforeFirstWrite) : IRemoteAccountRepository
    {
        private int _calls;
        public int RejectedWrites;
        public readonly TaskCompletionSource StaleWriteRejected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> CreateIfSealedUnderCurrentKeyAsync(RemoteAccount account, Func<byte[]?, bool> sealedUnderCurrentKey)
        {
            if (Interlocked.Increment(ref _calls) == 1) await beforeFirstWrite();
            var written = await inner.CreateIfSealedUnderCurrentKeyAsync(account, sealedUnderCurrentKey);
            if (!written)
            {
                Interlocked.Increment(ref RejectedWrites);
                StaleWriteRejected.TrySetResult();
            }
            return written;
        }

        public Task<bool> UpdateTokenIfSealedUnderCurrentKeyAsync(Guid id, byte[] encryptedToken, byte[] tokenIv, DateTime? expiresAt, Func<byte[]?, bool> sealedUnderCurrentKey)
            => inner.UpdateTokenIfSealedUnderCurrentKeyAsync(id, encryptedToken, tokenIv, expiresAt, sealedUnderCurrentKey);
        public Task<RemoteAccount?> GetByIdAsync(Guid id) => inner.GetByIdAsync(id);
        public Task<List<RemoteAccount>> ListAllAsync() => inner.ListAllAsync();
        public Task CreateAsync(RemoteAccount account) => inner.CreateAsync(account);
        public Task UpdateAsync(RemoteAccount account) => inner.UpdateAsync(account);
        public Task UpdateStatusAsync(Guid id, string status, string? error, DateTime? syncedAt) => inner.UpdateStatusAsync(id, status, error, syncedAt);
        public Task UpdateTokenAsync(Guid id, byte[] encryptedToken, byte[] tokenIv, DateTime? expiresAt) => inner.UpdateTokenAsync(id, encryptedToken, tokenIv, expiresAt);
        public Task DeleteAsync(Guid id) => inner.DeleteAsync(id);
    }

    private RemoteAccountService CreateService(IRemoteAccountRepository accounts, SessionService session) => new(
        accounts, new RemoteSubscriptionRepository(Factory), new FolderRepository(Factory, new CallerScopeHolder()),
        NodeRepo, Clock, session, new HttpClient(new TokenHandler()), new CallerScopeHolder());

    [Fact]
    public async Task TokenWrittenBetweenRotationCommitAndSwap_IsResealedUnderTheNewKey_AndSurvivesRestart()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);
        var keySlots = new KeySlotRepository(Factory);

        // What DekRewrapper's committed transaction leaves behind: the sentinel names the new DEK,
        // the key slot wraps it — while this process's session still holds the old one, because
        // SwapMasterDek has not run yet.
        async Task CommitRotationWithoutSwapAsync()
        {
            using var conn = Factory.CreateConnection();
            await conn.ExecuteAsync("UPDATE tbl_node_identity SET sentinel_value = @s",
                new { s = MasterKeyManager.ComputeSentinel(newDek) });
            var slot = (await keySlots.GetAllAsync()).Single(s => s.Salt != null);
            var kek = KeyDerivation.DeriveKek(Password, slot.Salt!, slot.ArgonMemory!.Value, slot.ArgonIterations!.Value, slot.ArgonParallelism!.Value);
            var (enc, iv) = MasterKeyManager.WrapMasterDek(newDek, kek);
            Array.Clear(kek);
            await keySlots.UpdateSlotKeyAsync(slot.SlotId, enc, iv);
        }

        var barrier = new BarrierRepository(new RemoteAccountRepository(Factory), CommitRotationWithoutSwapAsync);
        var service = CreateService(barrier, Session);

        var create = service.CreateAsync("Remote", "https://remote.example", "u", "p");

        // The write sealed under the old DEK reached the database after the "commit" and was refused.
        await barrier.StaleWriteRejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Session.SwapMasterDek((byte[])newDek.Clone());

        var account = await create.WaitAsync(TimeSpan.FromSeconds(10));
        barrier.RejectedWrites.Should().Be(1, "exactly the stale write is refused; the re-sealed one goes through");

        var stored = (await new RemoteAccountRepository(Factory).GetByIdAsync(account.Id))!;
        RemoteAccountService.TryOpenToken(stored.EncryptedToken, stored.TokenIv, oldDek)
            .Should().BeNull("nothing may be stored under the retired master DEK");

        // Restart: a fresh session unlocked from the key slot on disk knows only the new DEK.
        var restarted = new SessionService(keySlots);
        (await restarted.UnlockAsync(Password)).Should().BeTrue();
        CreateService(new RemoteAccountRepository(Factory), restarted).DecryptToken(stored)
            .Should().Be("bmbrt_race_token");
        restarted.Lock();
        Array.Clear(oldDek);
    }

    [Fact]
    public async Task TokenWrite_WithNoRotationInFlight_IsWrittenFirstTime()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        var barrier = new BarrierRepository(new RemoteAccountRepository(Factory), () => Task.CompletedTask);

        var account = await CreateService(barrier, Session).CreateAsync("Remote", "https://remote.example", "u", "p");

        barrier.RejectedWrites.Should().Be(0);
        var stored = (await new RemoteAccountRepository(Factory).GetByIdAsync(account.Id))!;
        CreateService(barrier, Session).DecryptToken(stored).Should().Be("bmbrt_race_token");
    }
}
