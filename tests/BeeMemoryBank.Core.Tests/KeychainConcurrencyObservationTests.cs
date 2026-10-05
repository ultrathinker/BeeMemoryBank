using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BeeMemoryBank.Infrastructure.Secrets;
using Xunit.Abstractions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// What the REAL legacy file keychain does when several store objects (each with its own keychain reference, as several services of one
/// process or several processes would have) write and read ONE item at the same time. A Mac only. It counts every outcome and prints the
/// counts; the assertions are the safety rules the store must keep whatever the system does: a write either succeeds or throws a typed
/// failure, a read never returns a torn or foreign value, and - the dangerous one - a read of an item that exists is never "not found".
/// </summary>
public class KeychainConcurrencyObservationTests(ITestOutputHelper output)
{
    [MacOnlyFact]
    public void ManyStoreObjectsRacingOnOneItem_NeverLoseOrTearTheSecret()
    {
        var counts = new ConcurrentDictionary<string, int>();
        void Count(string what) => counts.AddOrUpdate(what, 1, (_, n) => n + 1);

        const int rounds = 12;
        for (var round = 0; round < rounds; round++)
        {
            using var h = new RealKeychainStoreHarness();
            var stores = Enumerable.Range(0, 8).Select(_ => h.OpenStore()).ToArray();
            using var barrier = new Barrier(stores.Length);
            var threads = stores.Select((store, t) => new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var r = 0; r < 25; r++)
                {
                    try
                    {
                        store.Write("race", "item", UserSecretStoreContract.Payload(t, r));
                        Count("write ok");
                    }
                    catch (UserSecretStoreException ex)
                    {
                        Count($"write {ex.FailureKind} {Regex.Match(ex.Message, @"status (-?\d+)").Groups[1].Value}");
                        continue;
                    }
                    try
                    {
                        var seen = store.Read("race", "item");
                        Count(seen is null ? "READ NOT FOUND AFTER OWN WRITE" : UserSecretStoreContract.IsIntactPayload(seen) ? "read ok" : "READ TORN");
                    }
                    catch (UserSecretStoreException ex)
                    {
                        Count($"read {ex.FailureKind} {Regex.Match(ex.Message, @"status (-?\d+)").Groups[1].Value}");
                    }
                }
            })).ToList();
            threads.ForEach(x => x.Start());
            threads.ForEach(x => x.Join());
        }

        foreach (var (what, n) in counts.OrderBy(kv => kv.Key)) output.WriteLine($"OBSERVED concurrent real keychain: {what} x {n}");
        counts.Keys.Should().NotContain("READ NOT FOUND AFTER OWN WRITE", "an existing secret must never read as absent: callers would mint a replacement over it");
        counts.Keys.Should().NotContain("READ TORN");
        counts.Keys.Where(k => k.StartsWith("read ", StringComparison.Ordinal) && k != "read ok").Should().BeEmpty("a read of a complete item has no reason to fail");
    }
}
