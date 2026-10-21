using System.Reflection;
using System.Runtime.CompilerServices;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.Windows;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

/// <summary>
/// What the Windows account stores keep for the life of the process: nothing for an account that is gone. The factories hold each account's state store, and the DPAPI store's table holds each
/// secrets folder's lock, only while something else holds them (the account's runtime does); one object per file stays true for as long as anyone has it.
/// </summary>
public sealed class WindowsStoreCacheTests
{
    private static BlindAccountEntry NewAccount() => new(BlindAccountEntry.NewId(), BlindAccountLayouts.Dir, "A");

    private static void CollectEverything()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Collects until <paramref name="holds"/> or the budget is spent, then runs <paramref name="assert"/> either way. One collect can be too early: the stores are cleaned up
    /// asynchronously (thread pool, finalizers), and a check that reads one of the tables sees the dead entries only after the sweep that the next store runs has removed them.
    /// Strict in both directions: the loop leaves the moment <paramref name="holds"/> is true, so a pass costs nothing, and it never passes by itself - <paramref name="assert"/>
    /// always has the last word, so a real leak (something holds the store) still fails after the wait, with the old message. <paramref name="poke"/> re-runs the sweep that lets
    /// the cleanup show, for the checks that read a table.
    /// </summary>
    private static void CollectUntil(Func<bool> holds, Action assert, Action? poke = null)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!holds() && Environment.TickCount64 < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            poke?.Invoke();
            Thread.Sleep(50);
        }
        assert();
    }

    private static bool IsAlive(WeakReference<IBlindStateStore> reference) => reference.TryGetTarget(out _);

    // The stores are made and dropped in methods of their own, so that nothing of this test's frame keeps them alive (a debug build keeps locals to the end of the method).

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MakeAndDropStateStores(WindowsStateStoreFactory factory, int count)
    {
        for (var i = 0; i < count; i++) _ = factory.For(NewAccount()).Get("k");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MakeAndDropSecretStores(string root, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var account = NewAccount();
            _ = new DpapiSecretStore(Path.Combine(BlindAccountNames.DataDirectory(root, account), "secrets"), account);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<IBlindStateStore> WriteAndDrop(WindowsStateStoreFactory factory, BlindAccountEntry account)
    {
        var store = factory.For(account);
        store.Set("k", "kept in the file");
        return new WeakReference<IBlindStateStore>(store);
    }

    [Fact]
    public void TheStateFactory_DoesNotKeepTheStoresOfAccountsThatAreGone()
    {
        var factory = new WindowsStateStoreFactory(TestFolders.New("cache-state"));

        MakeAndDropStateStores(factory, 300);
        CollectEverything();
        _ = factory.For(NewAccount());   // the next store sweeps the dead entries out of the table

        CollectUntil(() => factory.Tracked < 5,
            () => factory.Tracked.Should().BeLessThan(5, "300 accounts were made and dropped; the table holds what is alive, not what once was"),
            () => _ = factory.For(NewAccount()));
    }

    [Fact]
    public void TheStateFactory_GivesEveryoneTheSameStore_WhileAnyoneHoldsIt()
    {
        var factory = new WindowsStateStoreFactory(TestFolders.New("cache-state"));
        var account = NewAccount();
        var held = factory.For(account);
        CollectEverything();

        factory.For(account).Should().BeSameAs(held, "one object per file while it is in use: two would each have their own copy of the file");
        held.Set("k", "v");
        factory.For(account).Get("k").Should().Be("v");
    }

    [Fact]
    public void ADroppedStore_IsReplacedByOneThatReadsTheFileAfresh()
    {
        var factory = new WindowsStateStoreFactory(TestFolders.New("cache-state"));
        var account = NewAccount();
        var first = WriteAndDrop(factory, account);
        CollectEverything();

        CollectUntil(() => !first.TryGetTarget(out _), () => first.TryGetTarget(out _).Should().BeFalse("nothing holds it"));
        factory.For(account).Get("k").Should().Be("kept in the file", "the state is in the file, not in the object");
    }

    [Fact]
    public void TheSecretsFolderLockTable_DoesNotKeepTheFoldersOfAccountsThatAreGone()
    {
        var root = TestFolders.New("cache-locks");

        MakeAndDropSecretStores(root, 300);
        CollectEverything();
        MakeAndDropSecretStores(root, 1);   // the next store sweeps the dead entries out of the table

        // The table is shared by every test of the process; what this one made is gone, so it holds a handful, not 300.
        CollectUntil(() => DpapiSecretStore.TrackedFolderLocks < 100, () => DpapiSecretStore.TrackedFolderLocks.Should().BeLessThan(100),
            () => MakeAndDropSecretStores(root, 1));
    }

    [Fact]
    public void TwoStoresOfOneFolder_ShareOneLock_WhileEitherIsAlive_AndAnotherFolderHasItsOwn()
    {
        var root = TestFolders.New("cache-locks");
        var a = NewAccount();
        var b = NewAccount();
        var folderA = Path.Combine(BlindAccountNames.DataDirectory(root, a), "secrets");
        var gate = typeof(DpapiSecretStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var one = new DpapiSecretStore(folderA, a);
        CollectEverything();
        var two = new DpapiSecretStore(folderA, a);
        var other = new DpapiSecretStore(Path.Combine(BlindAccountNames.DataDirectory(root, b), "secrets"), b);

        gate.GetValue(two).Should().BeSameAs(gate.GetValue(one), "the secrets of one account are written one at a time, whichever object does it");
        gate.GetValue(other).Should().NotBeSameAs(gate.GetValue(one));
        GC.KeepAlive(one);
    }

    // ---- through the real composition: the account host over the real factories, accounts added and removed again ----------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(string Id, WeakReference<IBlindStateStore> State)> AddAsync(BlindDesktopRuntime runtime, IBlindDesktopPlatform platform)
    {
        var id = (await runtime.Accounts.AddAsync("Cycle")).AccountId!;
        var entry = runtime.Accounts.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry;
        return (id, new WeakReference<IBlindStateStore>(platform.StateStores.For(entry)));
    }

    [Fact]
    public async Task AccountsAddedAndRemovedAgain_LeaveNothingInTheFactories()
    {
        var root = TestFolders.New("cache-cycles");
        var platform = new WindowsTestPlatform(PlatformSelector.Create(root));
        var runtime = BlindDesktopRuntime.Create(platform, () => null, displayName: () => "Test Desktop", scheduler: new BlindDeviceSchedulerOptions { Tick = TimeSpan.FromHours(10) });
        try
        {
            await runtime.StartAsync();
            var states = new List<WeakReference<IBlindStateStore>>();
            for (var i = 0; i < 4; i++)
            {
                var (id, state) = await AddAsync(runtime, platform);
                states.Add(state);
                await runtime.Accounts.RemoveAsync(id);
                Directory.Exists(Path.Combine(root, "accounts", id)).Should().BeFalse();
            }
            SqliteConnection.ClearAllPools();
            CollectEverything();

            runtime.Accounts.GetSnapshot().Accounts.Should().BeEmpty();
            CollectUntil(() => states.Count(IsAlive) == 0,
                () => states.Count(IsAlive).Should().Be(0, "a removed account's state store is not kept by the factory, nor by anything the host left behind"));
        }
        finally
        {
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }
}
