using System.Collections.Concurrent;
using System.Security.Cryptography;
using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The behavioral contract of <see cref="IUserSecretStore"/> that every implementation honors, written once and run against three stores:
/// the in-memory test double, the macOS Keychain store over a fake in-memory Keychain (any operating system), and the macOS Keychain store
/// over a real throwaway keychain FILE (a Mac only; never the login keychain). A behavior a store cannot show (an item moved under another
/// name needs a stored item) is skipped for the stores that have none, not weakened for the ones that do.
/// </summary>
public static class UserSecretStoreContract
{
    private const string Purpose = "contract-test";

    private static readonly Dictionary<string, Action<IStoreHarness>> Table = new(StringComparer.Ordinal)
    {
        ["TheStoreIsSupported"] = h => h.Store.IsSupported.Should().BeTrue(),
        ["RoundTripOfAnyBytes"] = RoundTripOfAnyBytes,
        ["OverwriteReplacesTheValue"] = OverwriteReplacesTheValue,
        ["NotFoundIsNullAndOnlyNotFound"] = NotFoundIsNull,
        ["DeleteIsIdempotent"] = DeleteIsIdempotent,
        ["PurposeAndAccountBindTheValue"] = PurposeAndAccountBindTheValue,
        ["AMovedItemIsNeverReturned"] = AMovedItemIsNeverReturned,
        ["TheReturnedArrayIsACopy"] = TheReturnedArrayIsACopy,
        ["InvalidNamesAreRefused"] = InvalidNamesAreRefused,
        ["UnicodeSpaceAndSlashAccounts"] = UnicodeSpaceAndSlashAccounts,
        ["ConcurrentWritersDoNotCorrupt"] = ConcurrentWritersDoNotCorrupt,
        ["TwoStoreObjectsOverOneKeychainSeeEachOther"] = TwoStoreObjectsSeeEachOther,
    };

    /// <summary>The names of the behaviors, for a theory per store.</summary>
    public static IEnumerable<object[]> Cases => Table.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    internal static void Run(string behavior, IStoreHarness harness) => Table[behavior](harness);

    // ── the behaviors ──────────────────────────────────────────────────────────────────────────────────────────

    private static void RoundTripOfAnyBytes(IStoreHarness h)
    {
        var values = new List<byte[]>();
        foreach (var length in new[] { 1, 31, 32, 33, 1000, 65_536 }) values.Add(RandomNumberGenerator.GetBytes(length));
        values.Add(new byte[32]); // all zero
        values.Add(Enumerable.Repeat((byte)0xFF, 32).ToArray());
        values.Add(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        values.Add("a text that looks like a password: p@ss/w0rd\u0000end"u8.ToArray());

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            var before = value.ToArray();

            h.Store.Write(Purpose, "round-trip-" + i, value);

            value.Should().Equal(before, "the store never alters or zeroes the caller's buffer");
            h.Store.Read(Purpose, "round-trip-" + i).Should().Equal(before);
            h.Store.Read(Purpose, "round-trip-" + i).Should().Equal(before, "reading does not consume the secret");
        }
    }

    private static void OverwriteReplacesTheValue(IStoreHarness h)
    {
        var first = RandomNumberGenerator.GetBytes(40);
        var second = RandomNumberGenerator.GetBytes(7);
        var third = RandomNumberGenerator.GetBytes(300);

        h.Store.Write(Purpose, "overwrite", first);
        h.Store.Read(Purpose, "overwrite").Should().Equal(first);
        h.Store.Write(Purpose, "overwrite", second);
        h.Store.Read(Purpose, "overwrite").Should().Equal(second, "a shorter value fully replaces a longer one");
        h.Store.Write(Purpose, "overwrite", third);
        h.Store.Read(Purpose, "overwrite").Should().Equal(third);
        h.Store.Write(Purpose, "overwrite", second);
        h.Store.Read(Purpose, "overwrite").Should().Equal(second);
    }

    private static void NotFoundIsNull(IStoreHarness h)
    {
        h.Store.Read(Purpose, "never-written").Should().BeNull();
        h.Store.Write(Purpose, "present", [1, 2, 3]);

        h.Store.Read(Purpose, "absent").Should().BeNull("another account of the same purpose is a different secret");
        h.Store.Read("another-purpose", "present").Should().BeNull("another purpose with the same account is a different secret");
        h.Store.Read(Purpose, "present").Should().Equal(1, 2, 3);
    }

    private static void DeleteIsIdempotent(IStoreHarness h)
    {
        ((Action)(() => h.Store.Delete(Purpose, "missing"))).Should().NotThrow("a missing secret is not an error");
        h.Store.Write(Purpose, "doomed", [1]);
        h.Store.Write(Purpose, "survivor", [2]);

        h.Store.Delete(Purpose, "doomed");

        h.Store.Read(Purpose, "doomed").Should().BeNull();
        ((Action)(() => h.Store.Delete(Purpose, "doomed"))).Should().NotThrow();
        h.Store.Read(Purpose, "survivor").Should().Equal(new byte[] { 2 }, "deleting one secret deletes only that secret");
        h.Store.Write(Purpose, "doomed", [3]);
        h.Store.Read(Purpose, "doomed").Should().Equal(new byte[] { 3 }, "a deleted name can be written again");
    }

    private static void PurposeAndAccountBindTheValue(IStoreHarness h)
    {
        var values = new Dictionary<(string, string), byte[]>
        {
            [("purpose-one", "account-a")] = RandomNumberGenerator.GetBytes(32),
            [("purpose-one", "account-b")] = RandomNumberGenerator.GetBytes(32),
            [("purpose-two", "account-a")] = RandomNumberGenerator.GetBytes(32),
            [("purpose-two", "account-b")] = RandomNumberGenerator.GetBytes(32),
        };
        foreach (var ((purpose, account), value) in values) h.Store.Write(purpose, account, value);

        foreach (var ((purpose, account), value) in values)
            h.Store.Read(purpose, account).Should().Equal(value, $"{purpose}/{account} must read back its own value, never a neighbor's");

        h.Store.Delete("purpose-one", "account-a");
        h.Store.Read("purpose-one", "account-a").Should().BeNull();
        h.Store.Read("purpose-one", "account-b").Should().Equal(values[("purpose-one", "account-b")]);
        h.Store.Read("purpose-two", "account-a").Should().Equal(values[("purpose-two", "account-a")]);
    }

    private static void AMovedItemIsNeverReturned(IStoreHarness h)
    {
        if (!h.CanTransplant) return; // a store without a stored item has nothing to move
        var secret = RandomNumberGenerator.GetBytes(32);
        h.Store.Write("purpose-one", "account-a", secret);

        h.Transplant("purpose-one", "account-a", "purpose-two", "account-a");
        h.Transplant("purpose-one", "account-a", "purpose-one", "account-b");

        foreach (var (purpose, account) in new[] { ("purpose-two", "account-a"), ("purpose-one", "account-b") })
        {
            var read = () => h.Store.Read(purpose, account);
            read.Should().Throw<UserSecretStoreException>($"an item copied to {purpose}/{account} from another name is not that secret")
                .Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
        }
        h.Store.Read("purpose-one", "account-a").Should().Equal(secret, "the original is untouched");

        // A damaged item is recovered by writing the secret again, not by reading it.
        var replacement = RandomNumberGenerator.GetBytes(32);
        h.Store.Write("purpose-two", "account-a", replacement);
        h.Store.Read("purpose-two", "account-a").Should().Equal(replacement);
    }

    private static void TheReturnedArrayIsACopy(IStoreHarness h)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        h.Store.Write(Purpose, "copy", secret);

        var first = h.Store.Read(Purpose, "copy")!;
        Array.Clear(first);

        h.Store.Read(Purpose, "copy").Should().Equal(secret, "the caller zeroes what it was given; the stored secret is not that array");
    }

    private static void InvalidNamesAreRefused(IStoreHarness h)
    {
        foreach (var bad in new string?[] { null, "", " ", "\t\r\n" })
        {
            ((Action)(() => h.Store.Read(bad!, "account"))).Should().Throw<ArgumentException>();
            ((Action)(() => h.Store.Read("purpose", bad!))).Should().Throw<ArgumentException>();
            ((Action)(() => h.Store.Write(bad!, "account", [1]))).Should().Throw<ArgumentException>();
            ((Action)(() => h.Store.Write("purpose", bad!, [1]))).Should().Throw<ArgumentException>();
            ((Action)(() => h.Store.Delete(bad!, "account"))).Should().Throw<ArgumentException>();
            ((Action)(() => h.Store.Delete("purpose", bad!))).Should().Throw<ArgumentException>();
        }
    }

    private static void UnicodeSpaceAndSlashAccounts(IStoreHarness h)
    {
        var account = "\u0437\u0430\u0434\u0430\u0447\u0430/账户 é / vault 1";
        var secret = RandomNumberGenerator.GetBytes(24);

        h.Store.Write("ddns-token", account, secret);

        h.Store.Read("ddns-token", account).Should().Equal(secret);
        h.Store.Read("ddns-token", "\u0437\u0430\u0434\u0430\u0447\u0430").Should().BeNull();
        h.Store.Delete("ddns-token", account);
        h.Store.Read("ddns-token", account).Should().BeNull();
    }

    /// <summary>A payload that proves its own integrity: a torn or mixed value matches no (thread, round).</summary>
    internal static byte[] Payload(int thread, int round)
    {
        var payload = new byte[24 + (thread * 7 + round) % 50];
        payload[0] = (byte)thread;
        payload[1] = (byte)round;
        for (var i = 2; i < payload.Length; i++) payload[i] = (byte)(thread * 31 + round * 17 + i);
        return payload;
    }

    internal static bool IsIntactPayload(byte[]? payload) =>
        payload is { Length: >= 2 } && payload.AsSpan().SequenceEqual(Payload(payload[0], payload[1]));

    private static void ConcurrentWritersDoNotCorrupt(IStoreHarness h)
    {
        const int threads = 8;
        const int rounds = 20;
        var errors = new ConcurrentQueue<Exception>();
        var last = new byte[threads][];
        var stores = new IUserSecretStore[threads];
        for (var t = 0; t < threads; t++) stores[t] = t % 2 == 0 ? h.Store : h.OpenAnother();
        using var barrier = new Barrier(threads);

        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                for (var r = 0; r < rounds; r++)
                {
                    var payload = Payload(t, r);
                    stores[t].Write(Purpose, "shared", payload);
                    stores[t].Write(Purpose, "private-" + t, payload);
                    last[t] = payload;
                    var seen = stores[t].Read(Purpose, "shared");
                    if (!IsIntactPayload(seen)) errors.Enqueue(new InvalidOperationException($"thread {t} round {r} read a corrupt shared value"));
                    if (stores[t].Read(Purpose, "private-" + t) is not { } mine || !mine.AsSpan().SequenceEqual(payload))
                        errors.Enqueue(new InvalidOperationException($"thread {t} round {r} did not read its own private value"));
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        })).ToList();

        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());

        errors.Should().BeEmpty();
        IsIntactPayload(h.Store.Read(Purpose, "shared")).Should().BeTrue("the shared secret is exactly one of the written values");
        for (var t = 0; t < threads; t++) h.Store.Read(Purpose, "private-" + t).Should().Equal(last[t]);
    }

    private static void TwoStoreObjectsSeeEachOther(IStoreHarness h)
    {
        var other = h.OpenAnother();
        var secret = RandomNumberGenerator.GetBytes(32);

        h.Store.Write(Purpose, "shared-view", secret);

        other.Read(Purpose, "shared-view").Should().Equal(secret);
        other.Delete(Purpose, "shared-view");
        h.Store.Read(Purpose, "shared-view").Should().BeNull();
    }
}

/// <summary>The in-memory store honors the contract (it is the double the consumers' tests use, so it must be faithful).</summary>
public class InMemoryUserSecretStoreContractTests
{
    [Theory]
    [MemberData(nameof(UserSecretStoreContract.Cases), MemberType = typeof(UserSecretStoreContract))]
    public void Honors_the_contract(string behavior)
    {
        using var harness = new InMemoryStoreHarness();
        UserSecretStoreContract.Run(behavior, harness);
    }
}

/// <summary>The Keychain store over a fake in-memory Keychain honors the contract, on every operating system.</summary>
public class KeychainStoreOverAFakeBackendContractTests
{
    [Theory]
    [MemberData(nameof(UserSecretStoreContract.Cases), MemberType = typeof(UserSecretStoreContract))]
    public void Honors_the_contract(string behavior)
    {
        using var harness = new FakeKeychainStoreHarness();
        UserSecretStoreContract.Run(behavior, harness);
    }

    [Theory]
    [MemberData(nameof(UserSecretStoreContract.Cases), MemberType = typeof(UserSecretStoreContract))]
    public void Honors_the_contract_when_the_store_is_scoped_to_a_data_folder(string behavior)
    {
        using var harness = new FakeKeychainStoreHarness(scope: "0123456789abcdef");
        UserSecretStoreContract.Run(behavior, harness);
    }
}

/// <summary>The Keychain store over a real throwaway keychain file honors the contract. A Mac only; skipped, not failed, elsewhere.</summary>
public class KeychainStoreOverAThrowawayKeychainContractTests
{
    [MacOnlyTheory]
    [MemberData(nameof(UserSecretStoreContract.Cases), MemberType = typeof(UserSecretStoreContract))]
    public void Honors_the_contract(string behavior)
    {
        using var harness = new RealKeychainStoreHarness();
        UserSecretStoreContract.Run(behavior, harness);
    }

    [MacOnlyTheory]
    [MemberData(nameof(UserSecretStoreContract.Cases), MemberType = typeof(UserSecretStoreContract))]
    public void Honors_the_contract_when_the_store_is_scoped_to_a_data_folder(string behavior)
    {
        using var harness = new RealKeychainStoreHarness(scope: "0123456789abcdef");
        UserSecretStoreContract.Run(behavior, harness);
    }
}
