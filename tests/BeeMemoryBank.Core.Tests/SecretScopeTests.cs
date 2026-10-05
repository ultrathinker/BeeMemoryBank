using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The scope id of a vault: <c>&lt;dataPath&gt;/.secret-scope</c>, 16 lowercase hex characters made from 8 random bytes. It belongs to the
/// FOLDER, not to its path, so renaming, moving or copying a vault (or reaching it through a symbolic link) keeps its secrets reachable -
/// the property that a hash of the path lacked, and that a missing CA key (which makes <c>LocalCaService</c> mint a new CA and orphan the
/// trust of every paired device) makes essential. Fake Keychain backend, every operating system; the real keychain is exercised in
/// <see cref="MacOsKeychainUserSecretStoreMacTests"/>.
/// </summary>
public class SecretScopeTests
{
    private static readonly TimeSpan ShortPatience = TimeSpan.FromMilliseconds(80);
    private const string ScopeFile = MacOsKeychainUserSecretStore.ScopeFileName;

    private static string ScopeIn(string dir) => File.ReadAllText(Path.Combine(dir, ScopeFile));

    private static MacOsKeychainUserSecretStore StoreFor(FakeKeychainBackend fake, string dir) =>
        MacOsKeychainUserSecretStore.WithDataPathScope(fake, dir, "test.bmb.desktop.secrets.scope", isSupported: true);

    // ── creation ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFolderWithoutTheFile_GetsSixteenLowercaseHexCharacters_AndTheFolderIsMadeIfMissing()
    {
        using var folders = new TempDataFolders();
        var dir = Path.Combine(folders.Root, "not-made-yet", "vault");

        var id = MacOsKeychainUserSecretStore.ScopeForDataPath(dir);

        id.Should().MatchRegex("^[0-9a-f]{16}$");
        File.ReadAllBytes(Path.Combine(dir, ScopeFile)).Should().Equal(Encoding.ASCII.GetBytes(id), "the file holds the id and nothing else: no newline, no BOM");
        Directory.GetFileSystemEntries(dir).Should().ContainSingle("no temporary file is left next to it");
    }

    [Fact]
    public void TheSameFolder_GivesTheSameId_EveryTime_WhateverTheSpelling()
    {
        using var folders = new TempDataFolders();
        var dir = folders.Make("Vault One");

        var id = MacOsKeychainUserSecretStore.ScopeForDataPath(dir);

        MacOsKeychainUserSecretStore.ScopeForDataPath(dir).Should().Be(id);
        MacOsKeychainUserSecretStore.ScopeForDataPath(dir + Path.DirectorySeparatorChar).Should().Be(id, "a trailing separator");
        MacOsKeychainUserSecretStore.ScopeForDataPath(Path.Combine(dir, "..", "Vault One")).Should().Be(id, "dot segments");
    }

    [Fact]
    public void TwoFolders_HaveTwoIds()
    {
        using var folders = new TempDataFolders();

        var first = MacOsKeychainUserSecretStore.ScopeForDataPath(folders.Make());
        var second = MacOsKeychainUserSecretStore.ScopeForDataPath(folders.Make());

        first.Should().NotBe(second);
    }

    [Fact]
    public void ManyTasksStartingAtOnce_EndWithOneId_AndOneFile()
    {
        using var folders = new TempDataFolders();
        for (var round = 0; round < 20; round++)
        {
            var dir = folders.Make();
            using var barrier = new Barrier(16);
            var tasks = Enumerable.Range(0, 16)
                .Select(_ => Task.Factory.StartNew(() =>
                {
                    barrier.SignalAndWait();
                    return MacOsKeychainUserSecretStore.ScopeForDataPath(dir);
                }, TaskCreationOptions.LongRunning)).ToArray();

            Task.WaitAll(tasks);

            tasks.Select(t => t.Result).Distinct().Should().ContainSingle($"round {round}: every starter must end with the id that is in the file");
            ScopeIn(dir).Should().Be(tasks[0].Result);
            Directory.GetFileSystemEntries(dir).Should().ContainSingle();
        }
    }

    [Fact]
    public void StoreObjectsOfOneVault_StartingAtOnce_ShareOneScope_AndSeeEachOthersSecrets()
    {
        using var folders = new TempDataFolders();
        var dir = folders.Make();
        var fake = new FakeKeychainBackend();
        const int writers = 8;
        var errors = new ConcurrentQueue<Exception>();
        using var barrier = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(t => new Thread(() =>
        {
            try
            {
                var store = StoreFor(fake, dir);
                barrier.SignalAndWait();
                store.Write("p", "account-" + t, [(byte)t]);
                if (store.Read("p", "account-" + t) is not { Length: 1 } mine || mine[0] != t)
                    errors.Enqueue(new InvalidOperationException("lost its own write"));
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToList();

        threads.ForEach(x => x.Start());
        threads.ForEach(x => x.Join());

        errors.Should().BeEmpty();
        var id = ScopeIn(dir);
        fake.Keys.Should().HaveCount(writers).And.OnlyContain(k => k.Account.StartsWith(id + "/p/account-", StringComparison.Ordinal),
            "all of them wrote under the one id that is in the file");
        var reader = StoreFor(fake, dir);
        for (var t = 0; t < writers; t++) reader.Read("p", "account-" + t).Should().Equal(new[] { (byte)t });
    }

    [Fact]
    public void AnUnfinishedFile_IsWaitedFor_AndUsedOnceItIsComplete()
    {
        // Another process has created the file and not yet written all of it. A reader must neither call that damaged nor replace it.
        using var folders = new TempDataFolders();
        var dir = folders.Make();
        var file = Path.Combine(dir, ScopeFile);
        using var writer = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        writer.Write("0123456789"u8);
        writer.Flush();

        var reader = Task.Run(() => MacOsKeychainUserSecretStore.ScopeForDataPath(dir, TimeSpan.FromSeconds(20)));
        Thread.Sleep(150);
        reader.IsCompleted.Should().BeFalse("it is waiting for the writer");
        writer.Write("abcdef"u8);
        writer.Flush();
        writer.Dispose();

        reader.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
        reader.Result.Should().Be("0123456789abcdef");
    }

    // ── the id moves with the folder ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACopiedFolder_KeepsItsId_AndItsSecretsStayReachable()
    {
        using var folders = new TempDataFolders();
        var original = folders.Make();
        var fake = new FakeKeychainBackend();
        var secret = RandomNumberGenerator.GetBytes(32);
        StoreFor(fake, original).Write("local-ca", "default", secret);
        File.WriteAllText(Path.Combine(original, "ca.crt"), "stands for the public certificate that lives beside the key's id");

        var copy = folders.CopyOf(original);

        ScopeIn(copy).Should().Be(ScopeIn(original));
        MacOsKeychainUserSecretStore.ScopeForDataPath(copy).Should().Be(ScopeIn(original));
        StoreFor(fake, copy).Read("local-ca", "default").Should().Equal(secret, "the vault under another name is the same vault");
    }

    [Fact]
    public void ASymbolicLinkToAFolder_IsTheSameVault()
    {
        using var folders = new TempDataFolders();
        var dir = folders.Make();
        var alias = folders.TryLinkTo(dir);
        if (alias is null) return; // this process cannot make symbolic links (Windows without the privilege): nothing to prove here
        var fake = new FakeKeychainBackend();
        var secret = RandomNumberGenerator.GetBytes(32);
        StoreFor(fake, dir).Write("os-auto-unlock", "default", secret);

        MacOsKeychainUserSecretStore.ScopeForDataPath(alias).Should().Be(ScopeIn(dir));
        StoreFor(fake, alias).Read("os-auto-unlock", "default").Should().Equal(secret);
        Directory.GetFileSystemEntries(dir).Should().ContainSingle("the alias did not make a second file");
    }

    [Fact]
    public void TwoFoldersDifferingOnlyByCase_AreTwoVaults_WhereTheFileSystemTellsThemApart()
    {
        using var folders = new TempDataFolders();
        if (!folders.IsCaseSensitive())
        {
            // A case-insensitive volume cannot hold both spellings (they are one folder); what is left to prove is that two folders are two ids.
            MacOsKeychainUserSecretStore.ScopeForDataPath(folders.Make("Vault")).Should().NotBe(MacOsKeychainUserSecretStore.ScopeForDataPath(folders.Make("Vault Two")));
            return;
        }
        var upper = folders.Make("Vault");
        var lower = folders.Make("vault");

        MacOsKeychainUserSecretStore.ScopeForDataPath(upper).Should().NotBe(MacOsKeychainUserSecretStore.ScopeForDataPath(lower));
    }

    // ── damaged and unusable ───────────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> DamagedContents()
    {
        yield return ["an empty file", Array.Empty<byte>()];
        yield return ["a hexadecimal beginning that never gets finished", "0123456789ab"u8.ToArray()];
        yield return ["upper case hexadecimal", "0123456789ABCDEF"u8.ToArray()];
        yield return ["a non-hex character", "0123456789abcdeg"u8.ToArray()];
        yield return ["a trailing newline", "0123456789abcdef\n"u8.ToArray()];
        yield return ["a trailing carriage return and newline", "0123456789abcdef\r\n"u8.ToArray()];
        yield return ["one character too many", "0123456789abcdef0"u8.ToArray()];
        yield return ["thirty-two characters", "0123456789abcdef0123456789abcdef"u8.ToArray()];
        yield return ["a text", "this is not an id"u8.ToArray()];
        yield return ["a byte order mark", new byte[] { 0xEF, 0xBB, 0xBF, (byte)'0', (byte)'1', (byte)'2', (byte)'3' }];
        yield return ["binary", new byte[] { 0, 1, 2, 3, 255, 254, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7 }];
        yield return ["a long file", Enumerable.Repeat((byte)'a', 5000).ToArray()];
    }

    [Theory]
    [MemberData(nameof(DamagedContents))]
    public void ADamagedFile_IsMalformed_IsNotReplaced_AndTheFileIsUntouched(string what, byte[] content)
    {
        using var folders = new TempDataFolders();
        var dir = folders.Make();
        var file = Path.Combine(dir, ScopeFile);
        File.WriteAllBytes(file, content);

        var scope = () => MacOsKeychainUserSecretStore.ScopeForDataPath(dir, ShortPatience);

        var thrown = scope.Should().Throw<UserSecretStoreException>(what).Which;
        thrown.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
        thrown.Message.Should().NotContain(dir).And.NotContain(Path.GetFileName(dir));
        File.ReadAllBytes(file).Should().Equal(content, "a new id would orphan the secrets kept under the old one");
        Directory.GetFileSystemEntries(dir).Should().ContainSingle();
    }

    [Fact]
    public void AFolderThatCannotBeMade_IsUnavailable()
    {
        using var folders = new TempDataFolders();
        var aFile = Path.Combine(folders.Root, "a-file");
        File.WriteAllText(aFile, "not a folder");

        foreach (var path in new[] { aFile, Path.Combine(aFile, "under-a-file") })
        {
            var scope = () => MacOsKeychainUserSecretStore.ScopeForDataPath(path);
            scope.Should().Throw<UserSecretStoreException>(path).Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
        }
        File.ReadAllText(aFile).Should().Be("not a folder");
    }

    [Fact]
    public void AReadOnlyFolder_IsUnavailable_AndNothingIsMade()
    {
        using var folders = new TempDataFolders();
        var dir = folders.Make();
        var restore = MakeReadOnly(dir, out var enforced);
        try
        {
            if (!enforced) return; // running with rights that ignore a read-only folder (root): nothing to prove here
            var scope = () => MacOsKeychainUserSecretStore.ScopeForDataPath(dir);

            var thrown = scope.Should().Throw<UserSecretStoreException>().Which;

            thrown.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
            thrown.Message.Should().NotContain(dir);
            File.Exists(Path.Combine(dir, ScopeFile)).Should().BeFalse();
        }
        finally
        {
            restore();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankPath_IsRefused(string? path)
    {
        var scope = () => MacOsKeychainUserSecretStore.ScopeForDataPath(path!);

        scope.Should().Throw<ArgumentException>();
    }

    /// <summary>Makes the folder refuse new files for this process; <paramref name="enforced"/> says whether it really does (root ignores it).</summary>
    private static Action MakeReadOnly(string dir, out bool enforced)
    {
        if (OperatingSystem.IsWindows())
        {
            enforced = true;
            return DenyCreateFilesOnWindows(dir);
        }
        var before = File.GetUnixFileMode(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using (File.Create(Path.Combine(dir, "probe"))) { }
            enforced = false; // the probe succeeded: this process ignores the mode
            File.Delete(Path.Combine(dir, "probe"));
        }
        catch (UnauthorizedAccessException)
        {
            enforced = true;
        }
        return () => File.SetUnixFileMode(dir, before);
    }

    [SupportedOSPlatform("windows")]
    private static Action DenyCreateFilesOnWindows(string dir)
    {
        var info = new DirectoryInfo(dir);
        var user = WindowsIdentity.GetCurrent().User!;
        var security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(user, FileSystemRights.CreateFiles | FileSystemRights.WriteData, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return () =>
        {
            var current = info.GetAccessControl();
            current.RemoveAccessRule(rule);
            info.SetAccessControl(current);
        };
    }

    // ── the store reads the scope when it is used, not when it is made ──────────────────────────────────────────

    [Fact]
    public void ConstructingAStore_NeverTouchesTheFolder_AndAnUnusableFolderShowsAtTheFirstUse()
    {
        using var folders = new TempDataFolders();
        var aFile = Path.Combine(folders.Root, "a-file");
        File.WriteAllText(aFile, "not a folder");
        var neverMade = Path.Combine(folders.Root, "never-made");
        var fake = new FakeKeychainBackend();

        var broken = StoreFor(fake, Path.Combine(aFile, "vault"));
        var absent = StoreFor(fake, neverMade);
        using var production = MacOsKeychainUserSecretStore.ForDataPath(neverMade);
        var viaTheFactory = UserSecretStores.CreateDefault(neverMade);

        Directory.Exists(neverMade).Should().BeFalse("nothing is created when a store is constructed");
        fake.Calls.Should().BeEmpty();
        viaTheFactory.Should().NotBeNull();
        var read = () => broken.Read("p", "a");
        read.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
        fake.Calls.Should().BeEmpty("the Keychain is not called without a scope");
        absent.Write("p", "a", [1]);
        Directory.Exists(neverMade).Should().BeTrue("the first use made the folder and its scope");
    }

    [Fact]
    public void AnUnsupportedStore_DoesNotReadOrCreateTheScope()
    {
        using var folders = new TempDataFolders();
        var neverMade = Path.Combine(folders.Root, "never-made");
        var store = MacOsKeychainUserSecretStore.WithDataPathScope(new FakeKeychainBackend(), neverMade, isSupported: false);

        var read = () => store.Read("p", "a");

        read.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
        Directory.Exists(neverMade).Should().BeFalse();
    }

    [Fact]
    public void AFailureToReadTheScope_IsNotRemembered_SoARepairedFolderWorksAtOnce()
    {
        using var folders = new TempDataFolders();
        var dir = folders.Make();
        var file = Path.Combine(dir, ScopeFile);
        File.WriteAllText(file, "damaged");
        var fake = new FakeKeychainBackend();
        var store = StoreFor(fake, dir);

        var first = () => store.Write("p", "a", [1]);
        first.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
        fake.Keys.Should().BeEmpty("a write without a scope writes nothing");

        File.WriteAllText(file, "fedcba9876543210"); // the owner restored the file from a backup
        store.Write("p", "a", [1]);

        fake.Keys.Should().ContainSingle().Which.Account.Should().Be("fedcba9876543210/p/a");
        store.Read("p", "a").Should().Equal(1);
    }

    [Fact]
    public void TheKeychainAccount_IsTheIdOfTheFolderThenPurposeThenAccount_AndTheDigestBindsTheId()
    {
        using var folders = new TempDataFolders();
        var one = folders.Make();
        var two = folders.Make();
        var fake = new FakeKeychainBackend();
        StoreFor(fake, one).Write("os-auto-unlock", "default", RandomNumberGenerator.GetBytes(32));
        var idOne = ScopeIn(one);

        fake.Keys.Should().ContainSingle().Which.Account.Should().Be(idOne + "/os-auto-unlock/default");

        // The same item moved under the other vault's id is not that vault's secret.
        StoreFor(fake, two).Read("os-auto-unlock", "default").Should().BeNull();
        var idTwo = ScopeIn(two);
        fake.SetRaw("test.bmb.desktop.secrets.scope", idTwo + "/os-auto-unlock/default", fake.GetRaw("test.bmb.desktop.secrets.scope", idOne + "/os-auto-unlock/default")!);
        var read = () => StoreFor(fake, two).Read("os-auto-unlock", "default");
        read.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
    }
}
