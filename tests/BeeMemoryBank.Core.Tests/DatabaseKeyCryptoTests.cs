using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Tests;

/// <summary>Red tests 15.2.7 (docs/design/step-b-spec.md 3.3): the database-key wrapper format, its refusals and its buffers.</summary>
public class DatabaseKeyCryptoTests
{
    private static readonly Guid Node = Guid.Parse("0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9");

    private static byte[] Random32() => RandomNumberGenerator.GetBytes(32);

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void AWrapperRoundTrips_InTheExactFormatOfSpec33()
    {
        var dbk = Random32();
        var dek = Random32();
        var row = DatabaseKeyCrypto.Wrap(dbk, dek, Node, 3);

        row.Wrapped.Should().HaveCount(48, "ciphertext (32) || GCM tag (16)");
        row.Iv.Should().HaveCount(12);
        row.DbkGeneration.Should().Be(3);
        row.DekFingerprint.Should().MatchRegex("^[0-9a-f]{64}$").And.Be(DekFingerprint.Of(dek));

        // Independent decryption with the AAD written out from the specification text.
        var plain = new byte[32];
        using (var aes = new AesGcm(dek, 16))
            aes.Decrypt(row.Iv, row.Wrapped.AsSpan(0, 32), row.Wrapped.AsSpan(32, 16), plain,
                Encoding.UTF8.GetBytes("bmb-dbk-v1:0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9:3:" + DekFingerprint.Of(dek)));
        plain.Should().Equal(dbk);

        DatabaseKeyCrypto.TryUnwrap(row, dek, Node).Should().Equal(dbk);
    }

    [Fact]
    public void EveryWrapDrawsAFreshIv_SoARowIsNeverEncryptedTwiceUnderTheSameIv()
    {
        var dbk = Random32();
        var dek = Random32();
        var ivs = Enumerable.Range(0, 64).Select(_ => Convert.ToHexString(DatabaseKeyCrypto.Wrap(dbk, dek, Node, 1).Iv)).ToList();
        ivs.Distinct().Should().HaveCount(64);
    }

    [Fact]
    public void EachColumnTamperedOrSwapped_IsNullAndNeverAnException()
    {
        var dbk = Random32();
        var dek = Random32();
        var row = DatabaseKeyCrypto.Wrap(dbk, dek, Node, 2);

        byte[] Flip(byte[] b, int index) { var c = (byte[])b.Clone(); c[index] ^= 1; return c; }
        var otherFingerprint = DekFingerprint.Of(Random32());

        var cases = new Dictionary<string, (BootDbKeyRow Row, Guid Node)>
        {
            ["wrapped ciphertext"] = (row with { Wrapped = Flip(row.Wrapped, 0) }, Node),
            ["wrapped tag"] = (row with { Wrapped = Flip(row.Wrapped, 47) }, Node),
            ["iv"] = (row with { Iv = Flip(row.Iv, 5) }, Node),
            ["dek_fp"] = (row with { DekFingerprint = otherFingerprint }, Node),
            ["generation"] = (row with { DbkGeneration = 3 }, Node),
            ["node id (a foreign boot store)"] = (row, Guid.NewGuid()),
        };
        foreach (var (name, c) in cases)
            DatabaseKeyCrypto.TryUnwrap(c.Row, dek, c.Node).Should().BeNull(name);

        DatabaseKeyCrypto.TryUnwrap(row, Random32(), Node).Should().BeNull("another DEK");
    }

    public static IEnumerable<object[]> MalformedRows()
    {
        var ok = DatabaseKeyCrypto.Wrap(Random32(), Random32(), Node, 1);
        string fp = ok.DekFingerprint;
        foreach (var n in new[] { 0, 11, 13 }) yield return [$"iv of {n}", ok with { Iv = new byte[n] }];
        foreach (var n in new[] { 0, 47, 49 }) yield return [$"wrapped of {n}", ok with { Wrapped = new byte[n] }];
        yield return ["upper-case fingerprint", ok with { DekFingerprint = fp.ToUpperInvariant() }];
        yield return ["fingerprint of 63", ok with { DekFingerprint = fp[..63] }];
        yield return ["fingerprint of 65", ok with { DekFingerprint = fp + "0" }];
        yield return ["fingerprint not hex", ok with { DekFingerprint = new string('g', 64) }];
        yield return ["generation 0", ok with { DbkGeneration = 0 }];
        yield return ["generation -1", ok with { DbkGeneration = -1 }];
    }

    [Theory]
    [MemberData(nameof(MalformedRows))]
    public void AMalformedRowIsNullBeforeAnyCrypto_WithOneLogLineThatNamesTheGenerationOnly(string name, BootDbKeyRow row)
    {
        _ = name;
        var logger = new ListLogger();
        var dek = Random32();
        // The malformed row belongs to a different random DEK; row validation must answer before the fingerprint is even compared.
        DatabaseKeyCrypto.TryUnwrap(row, dek, Node, logger).Should().BeNull();

        logger.Lines.Should().ContainSingle();
        var line = logger.Lines[0].Message;
        line.Should().Contain(row.DbkGeneration.ToString(CultureInfo.InvariantCulture));
        line.Should().NotContain(row.DekFingerprint ?? "x", "the fingerprint is never logged");
        line.Should().NotContainAny("47", "49", "11", "13", "length", "byte", "Length");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void ADekOfTheWrongLengthIsAnArgumentException_WithoutAnyByteInTheMessage(int length)
    {
        var dek = new byte[length];
        Array.Fill(dek, (byte)0xAB);
        var row = DatabaseKeyCrypto.Wrap(Random32(), Random32(), Node, 1);

        var unwrap = () => DatabaseKeyCrypto.TryUnwrap(row, dek, Node);
        unwrap.Should().Throw<ArgumentException>().Which.Message.ToLowerInvariant().Should().NotContain("ab").And.NotContain("171");

        var wrap = () => DatabaseKeyCrypto.Wrap(Random32(), dek, Node, 1);
        wrap.Should().Throw<ArgumentException>().Which.Message.ToLowerInvariant().Should().NotContain("ab").And.NotContain("171");

        var wrapDbk = () => DatabaseKeyCrypto.Wrap(dek, Random32(), Node, 1);
        wrapDbk.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("tr-TR")]
    [InlineData("fa-IR")]
    public void TheAadIsBuiltFromTheInvariantDecimalAndTheDFormat_WhateverTheCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            var dbk = Random32();
            var dek = Random32();
            var row = DatabaseKeyCrypto.Wrap(dbk, dek, Node, 1234);

            DatabaseKeyCrypto.TryUnwrap(row, dek, Node).Should().Equal(dbk);

            var plain = new byte[32];
            using var aes = new AesGcm(dek, 16);
            aes.Decrypt(row.Iv, row.Wrapped.AsSpan(0, 32), row.Wrapped.AsSpan(32, 16), plain,
                Encoding.UTF8.GetBytes("bmb-dbk-v1:0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9:1234:" + DekFingerprint.Of(dek)));
            plain.Should().Equal(dbk, "the AAD digits are ASCII 0-9 under every culture");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [Fact]
    public void EveryTemporaryBufferIsZeroAfterTheCall_OnAuthenticationFailure()
    {
        var dek = Random32();
        var row = DatabaseKeyCrypto.Wrap(Random32(), dek, Node, 1);
        var tampered = row with { Wrapped = (byte[])row.Wrapped.Clone() };
        tampered.Wrapped[3] ^= 0xFF;

        var seen = new List<byte[]>();
        DatabaseKeyCrypto.Cleared = seen.Add;
        try
        {
            DatabaseKeyCrypto.TryUnwrap(tampered, dek, Node).Should().BeNull();
        }
        finally
        {
            DatabaseKeyCrypto.Cleared = null;
        }

        seen.Should().NotBeEmpty("the candidate plaintext buffer was cleared through the seam");
        seen.Should().OnlyContain(b => b.All(x => x == 0));
    }

    [Fact]
    public void OnSuccessTheReturnedDbkBelongsToTheCaller_AndNothingElseHoldsIt()
    {
        var dbk = Random32();
        var dek = Random32();
        var row = DatabaseKeyCrypto.Wrap(dbk, dek, Node, 1);

        var seen = new List<byte[]>();
        DatabaseKeyCrypto.Cleared = seen.Add;
        byte[]? result;
        try
        {
            result = DatabaseKeyCrypto.TryUnwrap(row, dek, Node);
        }
        finally
        {
            DatabaseKeyCrypto.Cleared = null;
        }

        result.Should().Equal(dbk);
        result!.Any(b => b != 0).Should().BeTrue("the caller's buffer is not cleared behind its back");
        seen.Should().NotContain(b => ReferenceEquals(b, result), "the returned buffer is not among the cleared temporaries");
        seen.All(b => b.All(x => x == 0)).Should().BeTrue();
    }

    [Fact]
    public void PageKeysAreHkdfSha256OfTheDbkWithTheInfoOfSpec32_AndTwoPurposesNeverShareOne()
    {
        var dbk = Random32();
        var vault = DatabaseKeyCrypto.DerivePageKey(dbk, "vault");
        var chat = DatabaseKeyCrypto.DerivePageKey(dbk, "chat");

        vault.Should().HaveCount(32);
        vault.Should().Equal(HKDF.DeriveKey(HashAlgorithmName.SHA256, dbk, 32, salt: Array.Empty<byte>(), info: Encoding.UTF8.GetBytes("bmb-db-page-v1:vault")));
        chat.Should().NotEqual(vault);
        DatabaseKeyCrypto.DerivePageKey(dbk, "vault").Should().Equal(vault);
        DatabaseKeyCrypto.DerivePageKey(Random32(), "vault").Should().NotEqual(vault);
        var tooShort = () => DatabaseKeyCrypto.DerivePageKey(new byte[31], "vault");
        tooShort.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task TheKeyServiceReadsTheNodeIdAndTheGenerationFromTheBootMetaAndConsidersTheOneRow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_dbk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new BootStore(Path.Combine(dir, "beememorybank.boot.db"));
            var keys = new BootDbKeyStore(store.Connections);
            var service = new DatabaseKeyService(store, keys);
            var dek = Random32();

            (await service.TryUnwrapAsync(dek)).Should().BeNull("no boot store yet");
            await store.EnsureCreatedAsync();
            (await service.TryUnwrapAsync(dek)).Should().BeNull("no node id in the meta yet");

            await store.SetMetaAsync(BootMetaKeys.NodeId, Node.ToString("D"));
            await store.SetMetaAsync(BootMetaKeys.DbkGeneration, "2");
            var dbk = service.Create();
            dbk.Should().HaveCount(32);
            await keys.AddIfMissingAsync(DatabaseKeyCrypto.Wrap(dbk, dek, Node, 1));
            (await service.TryUnwrapAsync(dek)).Should().BeNull("only generation 1 is there, the meta says 2");

            await keys.AddIfMissingAsync(DatabaseKeyCrypto.Wrap(dbk, dek, Node, 2));
            (await service.TryUnwrapAsync(dek)).Should().Equal(dbk);
            (await service.TryUnwrapAsync(Random32())).Should().BeNull("no wrapper for another DEK");
            await FluentActions.Awaiting(() => service.TryUnwrapAsync(new byte[31])).Should().ThrowAsync<ArgumentException>();

            service.DerivePageKey(dbk, "chat").Should().Equal(DatabaseKeyCrypto.DerivePageKey(dbk, "chat"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
