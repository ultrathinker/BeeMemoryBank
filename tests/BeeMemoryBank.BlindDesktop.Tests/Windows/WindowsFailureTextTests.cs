using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindDesktop.Windows;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

/// <summary>
/// What the key-store line of a card and the state-store failures say on Windows: a sentence is shown only when this code threw it on purpose (the DPAPI store's "did not answer", the state file's "cannot be
/// read at the moment" - marked, no path), and everything else - an adversarial <see cref="IOException"/> or <see cref="CryptographicException"/> whose message is a path with a key file's name and a canary - is
/// told by its kind. Over the real controller and the real stores in a scratch folder.
/// </summary>
public sealed class WindowsFailureTextTests
{
    private const string AdversarialPath = @"C:\Users\someone\AppData\Local\BeeMemoryBankBlind\accounts\canary-123\secrets\bmb_blind_seed_v1.dpapi canary-123";

    private static readonly string[] Canaries = ["canary-123", "bmb_blind_seed_v1", @"C:\Users", "someone", "AppData", ".dpapi"];

    /// <summary>The real secret stores, except that a test can make a load fail with an exception of its choice.</summary>
    private sealed class FaultySecretStores(IBlindSecretStoreFactory inner) : IBlindSecretStoreFactory
    {
        public Exception? Fault { get; set; }

        public IBlindSecretStore For(BlindAccountEntry account) => new Store(inner.For(account), this);

        private sealed class Store(IBlindSecretStore real, FaultySecretStores owner) : IBlindSecretStore
        {
            private T Load<T>(Func<T> read) => owner.Fault is { } fault ? throw fault : read();

            public byte[]? LoadIdentitySeed() => Load(real.LoadIdentitySeed);
            public byte[]? LoadBackupKey() => Load(real.LoadBackupKey);
            public byte[]? LoadPairingSecret() => Load(real.LoadPairingSecret);
            public void SaveIdentitySeed(byte[] seed) => real.SaveIdentitySeed(seed);
            public void SaveBackupKey(byte[] key) => real.SaveBackupKey(key);
            public void SavePairingSecret(byte[] secret) => real.SavePairingSecret(secret);
            public void ClearPairingSecret() => real.ClearPairingSecret();
            public void Clear() => real.Clear();
        }
    }

    private sealed class Session : IAsyncDisposable
    {
        public string Root { get; } = TestFolders.New("failure-text");
        public FaultySecretStores Secrets { get; private set; } = null!;
        public BlindDesktopRuntime Runtime { get; private set; } = null!;
        public string AccountId { get; private set; } = "";

        public async Task<Session> StartAsync()
        {
            var inner = PlatformSelector.Create(Root);
            Secrets = new FaultySecretStores(inner.SecretStores);
            Runtime = BlindDesktopRuntime.Create(new WindowsTestPlatform(inner, Secrets), () => null, displayName: () => "Test Desktop",
                scheduler: new BlindDeviceSchedulerOptions { Tick = TimeSpan.FromHours(10) });
            await Runtime.StartAsync();
            AccountId = (await Runtime.Accounts.AddAsync("Berlin office")).AccountId!;
            return this;
        }

        public IBlindAppController App => Runtime.Accounts.For(AccountId);

        public string SecretFile(string name) => Path.Combine(Root, "accounts", AccountId, "secrets", name);

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>Takes the right to read the file away from the current user until disposed (the owner may always change the permissions back).</summary>
    private static IDisposable DenyRead(string path)
    {
        var info = new FileInfo(path);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
        return new Restore(() =>
        {
            var again = info.GetAccessControl();
            again.RemoveAccessRule(rule);
            info.SetAccessControl(again);
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static void NothingOfThePathIsSaid(string? text)
    {
        text.Should().NotBeNullOrEmpty();
        foreach (var canary in Canaries) text.Should().NotContainEquivalentOf(canary);
    }

    // ---- everything that is not this code's own sentence is told by its kind ------------------------------------------------------------------

    [Theory]
    [InlineData("io", "IOException: a file or the storage of this device could not be read or written")]
    [InlineData("crypto", "CryptographicException: a key or encrypted data could not be used")]
    [InlineData("denied", "UnauthorizedAccessException: access was denied")]
    [InlineData("invalid", "an unexpected problem (InvalidOperationException)")]
    public async Task AKeyStoreThatFailsWithAnAdversarialMessage_IsToldByItsKind_OnTheStatusAndOnThePanel(string fault, string expected)
    {
        await using var session = await new Session().StartAsync();
        session.Secrets.Fault = fault switch
        {
            "io" => new IOException(AdversarialPath),
            "crypto" => new CryptographicException(AdversarialPath),
            "denied" => new UnauthorizedAccessException($"Access to the path '{AdversarialPath}' is denied."),
            _ => new InvalidOperationException(AdversarialPath),
        };

        var status = session.App.GetStatus();

        status.KeyStoreUnavailable.Should().Be(expected, "the key-store line is the kind of failure, never the message");
        NothingOfThePathIsSaid(status.KeyStoreUnavailable);
        var panel = new AccountPanelViewModel(session.App, session.Runtime.WorkFor(session.AccountId), _ => []);
        await panel.RefreshAsync();
        panel.KeyStoreNoticeText.Should().Contain("did not answer (" + expected + ")");
        NothingOfThePathIsSaid(panel.KeyStoreNoticeText);
        NothingOfThePathIsSaid(panel.Notice ?? "x");
        session.Runtime.Accounts.GetSnapshot().Accounts.Single().Status!.KeyStoreUnavailable.Should().Be(expected);
        var card = BlindAccountCard.Build(session.Runtime.Accounts.GetSnapshot().Accounts.Single(), TimeZoneInfo.Utc, DateTimeOffset.UtcNow);
        card.State.Should().Contain("did not answer");
        NothingOfThePathIsSaid(card.State);
    }

    [Fact]
    public async Task AKeyStoreThatWorksAgain_ShowsNoProblem()
    {
        await using var session = await new Session().StartAsync();
        session.Secrets.Fault = new IOException(AdversarialPath);
        session.App.GetStatus().KeyStoreUnavailable.Should().NotBeNull();

        session.Secrets.Fault = null;

        session.App.GetStatus().KeyStoreUnavailable.Should().BeNull();
    }

    // ---- this code's own sentences, from the real stores, are said as they are (and name no place) ------------------------------------------

    [Fact]
    public async Task ADpapiFileThatCannotBeRead_IsAnOwnSentence_SaidWithoutThePath()
    {
        await using var session = await new Session().StartAsync();
        var file = session.SecretFile("backup-key.dpapi");
        File.Exists(file).Should().BeTrue();

        using (DenyRead(file))
        {
            var status = session.App.GetStatus();

            status.KeyStoreUnavailable.Should().Be("BlindFileSentenceException: The secret store did not answer: the file cannot be read (access denied)");
            status.KeyStoreUnavailable.Should().NotContain(session.AccountId).And.NotContain(".dpapi").And.NotContain(session.Root);
            status.BackupKeyLost.Should().BeFalse("a store that did not answer is not a lost key");
        }
        session.App.GetStatus().KeyStoreUnavailable.Should().BeNull("the permission is back: the store answers again");
    }

    [Fact]
    public void TheDpapiStore_ThrowsAMarkedSentence_AndTheFrameworksFailureRidesAlongInsideForTheLog()
    {
        var folder = Path.Combine(TestFolders.New("failure-text-store"), "secrets");
        var store = new DpapiSecretStore(folder);
        store.SaveBackupKey(new byte[32]);
        var file = Path.Combine(folder, "backup-key.dpapi");

        using (DenyRead(file))
        {
            var act = () => store.LoadBackupKey();

            var ex = act.Should().Throw<IOException>().Which;
            BlindOwnSentence.IsMarked(ex).Should().BeTrue();
            BlindFailureText.Say(ex).Should().Be("The secret store did not answer: the file cannot be read (access denied).");
            ex.InnerException.Should().BeOfType<UnauthorizedAccessException>("the framework's own failure, which names the path, is for the log only");
            BlindFailureText.Say(ex).Should().NotContain(".dpapi");
        }
    }

    [Fact]
    public void AStateFileThatCannotBeReadForNow_IsAnOwnSentence_NotAPathOrAKind()
    {
        var file = Path.Combine(TestFolders.New("failure-text-state"), "state.json");
        new AtomicJsonStateStore(file).Set("k", "v");
        var store = new AtomicJsonStateStore(file, readRetryDelay: TimeSpan.Zero);

        using (DenyRead(file))
        {
            var act = () => store.Get("k");

            var ex = act.Should().Throw<IOException>().Which;
            BlindOwnSentence.IsMarked(ex).Should().BeTrue();
            BlindFailureText.Say(ex).Should().Be("The state file cannot be read at the moment (access denied).");
            BlindFailureText.Say(ex).Should().NotContain("state.json").And.NotContain(Path.GetDirectoryName(file)!);
        }
    }

    [Fact]
    public void AnIOExceptionOfTheFrameworkOnTheStateFile_IsToldByItsKind()
    {
        var file = Path.Combine(TestFolders.New("failure-text-state"), "state.json");
        new AtomicJsonStateStore(file).Set("k", "v");
        var store = new AtomicJsonStateStore(file, readRetryDelay: TimeSpan.Zero);

        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);   // another process holds it: the framework's message names the file
        var act = () => store.Get("k");

        var ex = act.Should().Throw<IOException>().Which;
        ex.Message.Should().Contain("state.json", "the framework's own message names the file");
        BlindOwnSentence.IsMarked(ex).Should().BeFalse();
        BlindFailureText.Say(ex).Should().Be("a file or the storage of this device could not be read or written");
    }

    [Fact]
    public void TheSentencesOfThePlatformThatAScreenMayShow_AreMarked_AndNameNoPlace()
    {
        var sentences = new Exception[]
        {
            new BlindSentenceException("The app was not started from its own executable, so there is nothing to register to start with Windows."),
            new BlindSentenceException("Windows did not give this user a LocalApplicationData folder."),
        };

        foreach (var sentence in sentences)
        {
            BlindFailureText.Say(sentence).Should().Be(sentence.Message);
            BlindFailureText.LooksLikeALocation(sentence.Message).Should().BeFalse();
        }
    }
}
