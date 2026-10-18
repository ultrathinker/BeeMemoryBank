using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Sealed;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-312/308 fix round, findings 1 and 3: an EMPTY sentinel (<c>X''</c>) is a missing sentinel, exactly like NULL, at every door that reads it
/// (the one predicate is <see cref="MasterKeyManager.IsSentinelMissing"/>), and the seal preflight stores the sentinel it makes with the same conditional
/// statement as every other healer, so it can never overwrite one somebody stored after it looked.
/// </summary>
public sealed class EmptySentinelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_empty_sentinel_" + Guid.NewGuid().ToString("N"));

    public EmptySentinelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string StagedDb(string sentinelSql)
    {
        var path = Path.Combine(_dir, "staged-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        conn.Execute("CREATE TABLE tbl_node_identity (sentinel_value BLOB)");
        conn.Execute($"INSERT INTO tbl_node_identity (sentinel_value) VALUES ({sentinelSql})");
        return path;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        return conn;
    }

    private static async Task<SessionService> UnlockedSessionAsync(string dir, byte[] dek)
    {
        DapperConfig.Configure();
        var factory = new DbConnectionFactory(dir);
        await new MigrationRunner(factory).RunMigrationsAsync();
        var session = new SessionService(new KeySlotRepository(factory));
        session.UnlockWithDek((byte[])dek.Clone());
        return session;
    }

    // ---------------------------------------------------------------- the key of a restored snapshot

    [Theory]
    [InlineData("NULL")]
    [InlineData("X''")]
    public async Task A_staged_snapshot_without_a_sentinel_of_either_shape_is_read_with_the_key_of_the_session(string missing)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var session = await UnlockedSessionAsync(_dir, dek);
        var keys = new SessionSnapshotKeyOperations(session);

        var found = keys.FindStagedMasterDek(StagedDb(missing), masterPassword: null, Open);

        found.Should().Equal(dek, "no sentinel (NULL or empty) is a vault from before the check: the session's key is the trust an unlock places in it");
    }

    [Fact]
    public async Task A_staged_snapshot_whose_sentinel_names_another_key_is_still_not_read_with_the_key_of_the_session()
    {
        var session = await UnlockedSessionAsync(_dir, RandomNumberGenerator.GetBytes(32));
        var foreign = Convert.ToHexString(MasterKeyManager.ComputeSentinel(RandomNumberGenerator.GetBytes(32)));

        new SessionSnapshotKeyOperations(session).FindStagedMasterDek(StagedDb($"X'{foreign}'"), masterPassword: null, Open)
            .Should().BeNull("a sentinel that is there is checked, not skipped");
    }

    // ---------------------------------------------------------------- what a peer is told

    [Fact]
    public async Task A_vault_with_an_empty_sentinel_has_none_to_give_a_peer()
    {
        using var node = new BmbWebApplicationFactory();
        await node.InitializeNodeAsync("SentinelNode", "sentinel-pw-1");
        using var http = node.CreateClient();
        (await http.GetAsync("/api/sync/sentinel")).StatusCode.Should().Be(HttpStatusCode.OK, "the control: a vault with its sentinel gives it");

        using (var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_node_identity SET sentinel_value = X''");

        (await http.GetAsync("/api/sync/sentinel")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an empty value is no sentinel: the peer must not take empty bytes for one and fail its own check against them");
    }

    // ---------------------------------------------------------------- the seal preflight

    /// <summary>
    /// Forwards every call to the real repository, and right after the preflight has READ the missing sentinel another healer stores its own: the race the
    /// review named. A <see cref="DispatchProxy"/> so that the whole interface is the real one.
    /// </summary>
    public class RacingRepository : DispatchProxy
    {
        public INodeIdentityRepository Inner = null!;
        public byte[] Winner = null!;
        public int Reads;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                var result = targetMethod!.Invoke(Inner, args);
                if (targetMethod.Name != nameof(INodeIdentityRepository.GetSentinelAsync) || Reads++ > 0) return result;
                var read = ((Task<byte[]?>)result!).GetAwaiter().GetResult();
                Inner.StoreSentinelIfMissingAsync(Winner).GetAwaiter().GetResult();
                return Task.FromResult(read);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("X''")]
    public async Task The_seal_preflight_does_not_overwrite_a_sentinel_stored_after_it_looked(string missing)
    {
        DapperConfig.Configure();
        var dek = RandomNumberGenerator.GetBytes(32);
        var factory = new DbConnectionFactory(_dir);
        await new MigrationRunner(factory).RunMigrationsAsync();
        var real = new NodeIdentityRepository(factory);
        await new InitializationService(real, new KeySlotRepository(factory), new UserRepository(factory), factory).InitializeAsync("admin", "Preflight", "preflight-pw-1");
        using (var conn = factory.CreateConnection()) conn.Execute($"UPDATE tbl_node_identity SET sentinel_value = {missing}");
        var winner = MasterKeyManager.ComputeSentinel(RandomNumberGenerator.GetBytes(32));   // another healer's, under whatever key it held
        var racing = DispatchProxy.Create<INodeIdentityRepository, RacingRepository>();
        ((RacingRepository)(object)racing).Inner = real;
        ((RacingRepository)(object)racing).Winner = winner;
        var session = new SessionService(new KeySlotRepository(factory));
        session.UnlockWithDek(dek);
        var services = new ServiceCollection()
            .AddSingleton(racing)
            .AddSingleton(session)
            .BuildServiceProvider();

        await new ApiSealMigrationHost(services).PrepareVaultAsync(CancellationToken.None);

        using var read = factory.CreateConnection();
        read.ExecuteScalar<byte[]>("SELECT sentinel_value FROM tbl_node_identity").Should().Equal(winner,
            "the preflight's own write is conditional on the sentinel still being missing; another healer's value stays");
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("X''")]
    public async Task The_seal_preflight_makes_the_sentinel_of_a_vault_that_has_none_of_either_shape(string missing)
    {
        DapperConfig.Configure();
        var dek = RandomNumberGenerator.GetBytes(32);
        var factory = new DbConnectionFactory(_dir);
        await new MigrationRunner(factory).RunMigrationsAsync();
        var real = new NodeIdentityRepository(factory);
        await new InitializationService(real, new KeySlotRepository(factory), new UserRepository(factory), factory).InitializeAsync("admin", "Preflight", "preflight-pw-1");
        using (var conn = factory.CreateConnection()) conn.Execute($"UPDATE tbl_node_identity SET sentinel_value = {missing}");
        var session = new SessionService(new KeySlotRepository(factory));
        session.UnlockWithDek(dek);
        var services = new ServiceCollection().AddSingleton<INodeIdentityRepository>(real).AddSingleton(session).BuildServiceProvider();

        await new ApiSealMigrationHost(services).PrepareVaultAsync(CancellationToken.None);

        var sentinel = await real.GetSentinelAsync();
        sentinel.Should().NotBeNullOrEmpty();
        MasterKeyManager.VerifySentinel(sentinel!, dek).Should().BeTrue("it names the key of the unlocked session");
    }
}
