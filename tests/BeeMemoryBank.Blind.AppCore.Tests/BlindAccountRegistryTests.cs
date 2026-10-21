using System.Text;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary><c>accounts.json</c>: read, validated, written atomically under one lock, and never written over when it is damaged.</summary>
public sealed class BlindAccountRegistryTests : IDisposable
{
    private readonly AccountRig _rig = new();
    private string FilePath => Path.Combine(_rig.Root, "accounts.json");

    public void Dispose() => _rig.Dispose();

    private static BlindAccountEntry Account(string? id = null, string name = "A") =>
        new(id ?? BlindAccountEntry.NewId(), BlindAccountLayouts.Dir, name, AddedAt: new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ANewRoot_IsMissing_AndHasNoAccounts()
    {
        var registry = new BlindAccountRegistry(_rig.Root);

        registry.Load().Should().Be(BlindRegistryState.Missing);
        registry.Current.Entries.Should().BeEmpty();
        registry.Problem.Should().BeNull();
        File.Exists(FilePath).Should().BeFalse("loading never creates the file");
    }

    [Fact]
    public void WhatIsWritten_IsReadBackExactly_ByTheNextStart()
    {
        var registry = new BlindAccountRegistry(_rig.Root);
        registry.Load();
        var legacy = new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
        var second = Account(name: "Zoë Müller's copy") with
        {
            SourceOrigin = "https://mem.example.org", InviteId = "inv-1", Order = 2,
            Limits = new BlindAccountLimits(2L << 30, 200L << 20, 5000), UnmeteredOnly = true, PauseOnPowerSaving = true, PausedByUser = true,
            Phase = BlindAccountPhases.AwaitingConfirmation,
        };
        registry.Update(_ => new BlindAccountsFile(Accounts: [legacy, second], Device: new BlindDeviceLimits(10L << 30, 1L << 30)));

        var next = new BlindAccountRegistry(_rig.Root);
        next.Load().Should().Be(BlindRegistryState.Loaded);
        next.Current.Entries.Should().BeEquivalentTo([legacy, second], o => o.WithStrictOrdering());
        next.Current.DeviceLimits.Should().Be(new BlindDeviceLimits(10L << 30, 1L << 30));
        next.Current.Version.Should().Be(1);
    }

    [Fact]
    public void TheFirstWrite_CreatesTheFile_AndLeavesNoTemporaryFileBehind()
    {
        var registry = new BlindAccountRegistry(Path.Combine(_rig.Root, "fresh-root"));
        registry.Update(_ => new BlindAccountsFile(Accounts: [Account()]));

        // The registry file and the lock file (empty, kept in the folder): no temporary file.
        Directory.GetFiles(Path.Combine(_rig.Root, "fresh-root")).Select(Path.GetFileName).Should().BeEquivalentTo("accounts.json", "accounts.lock");
    }

    [Theory]
    [InlineData("registry:before-write", false)]
    [InlineData("registry:tmp-written", false)]
    [InlineData("registry:renamed", true)]
    public void ACrashInTheWrite_LeavesTheOldFileOrTheNewOne_NeverADamagedOne(string step, bool newFileIsThere)
    {
        var old = new BlindAccountRegistry(_rig.Root);
        old.Update(_ => new BlindAccountsFile(Accounts: [Account(name: "old")]));
        var before = File.ReadAllBytes(FilePath);

        var crashing = new BlindAccountRegistry(_rig.Root, new StepCrash(step));
        crashing.Load();
        var act = () => crashing.Update(f => f with { Accounts = [.. f.Entries, Account(name: "new")] });
        act.Should().Throw<SimulatedCrash>();

        var restarted = new BlindAccountRegistry(_rig.Root);
        restarted.Load().Should().Be(BlindRegistryState.Loaded, "a crash must never leave a file the next start calls damaged");
        restarted.Current.Entries.Select(e => e.Name).Should().Equal(newFileIsThere ? ["old", "new"] : ["old"]);
        if (!newFileIsThere) File.ReadAllBytes(FilePath).Should().Equal(before, "the old file is untouched until the rename");
    }

    [Fact]
    public void ACrashDuringTheVeryFirstWrite_LeavesAMissingRegistry_ThatAdoptionCanWriteAgain()
    {
        var crashing = new BlindAccountRegistry(_rig.Root, new StepCrash("registry:tmp-written"));
        var act = () => crashing.Update(_ => new BlindAccountsFile(Accounts: [Account()]));
        act.Should().Throw<SimulatedCrash>();

        var restarted = new BlindAccountRegistry(_rig.Root);
        restarted.Load().Should().Be(BlindRegistryState.Missing, "the temporary file is not the registry");
        restarted.Update(_ => new BlindAccountsFile(Accounts: [Account()]));
        new BlindAccountRegistry(_rig.Root).Load().Should().Be(BlindRegistryState.Loaded);
    }

    public static TheoryData<string, string> DamagedFiles() => new()
    {
        { "not json at all", "this is not json {" },
        { "a truncated file", "{\"version\":1,\"accounts\":[{\"id\":\"legacy\",\"layout\":\"ro" },
        { "an empty file", "" },
        { "JSON null", "null" },
        { "a newer format", "{\"version\":2,\"accounts\":[]}" },
        { "an older format", "{\"version\":0,\"accounts\":[]}" },
        { "an id that is a path", "{\"version\":1,\"accounts\":[{\"id\":\"..\\\\..\\\\x\",\"layout\":\"dir\",\"name\":\"A\"}]}" },
        { "an id with a slash", "{\"version\":1,\"accounts\":[{\"id\":\"../../etc/passwd000000000000\",\"layout\":\"dir\",\"name\":\"A\"}]}" },
        { "an id of the wrong length", "{\"version\":1,\"accounts\":[{\"id\":\"abc\",\"layout\":\"dir\",\"name\":\"A\"}]}" },
        { "an upper-case id", "{\"version\":1,\"accounts\":[{\"id\":\"ABCDEFABCDEFABCDEFABCDEFABCDEFAB\",\"layout\":\"dir\",\"name\":\"A\"}]}" },
        { "the same id twice", "{\"version\":1,\"accounts\":[{\"id\":\"legacy\",\"layout\":\"root\",\"name\":\"A\"},{\"id\":\"legacy\",\"layout\":\"root\",\"name\":\"B\"}]}" },
        { "legacy id in a folder layout", "{\"version\":1,\"accounts\":[{\"id\":\"legacy\",\"layout\":\"dir\",\"name\":\"A\"}]}" },
        { "a hex id in the root layout", "{\"version\":1,\"accounts\":[{\"id\":\"0123456789abcdef0123456789abcdef\",\"layout\":\"root\",\"name\":\"A\"}]}" },
        { "an unknown layout", "{\"version\":1,\"accounts\":[{\"id\":\"legacy\",\"layout\":\"elsewhere\",\"name\":\"A\"}]}" },
        { "an unknown phase", "{\"version\":1,\"accounts\":[{\"id\":\"legacy\",\"layout\":\"root\",\"name\":\"A\",\"phase\":\"zombie\"}]}" },
        { "a name with a control character", "{\"version\":1,\"accounts\":[{\"id\":\"legacy\",\"layout\":\"root\",\"name\":\"A\\u0007\"}]}" },
        { "an empty name", "{\"version\":1,\"accounts\":[{\"id\":\"legacy\",\"layout\":\"root\",\"name\":\"  \"}]}" },
        { "a negative limit", "{\"version\":1,\"accounts\":[{\"id\":\"legacy\",\"layout\":\"root\",\"name\":\"A\",\"limits\":{\"maxStoredBytes\":-1}}]}" },
        { "a negative device limit", "{\"version\":1,\"accounts\":[],\"device\":{\"maxTrafficBytesPerDay\":-5}}" },
    };

    [Theory]
    [MemberData(nameof(DamagedFiles))]
    public void ADamagedFile_IsReportedWithASentence_AndNeverWrittenOver(string what, string content)
    {
        _ = what;
        File.WriteAllText(FilePath, content, new UTF8Encoding(false));
        var before = File.ReadAllBytes(FilePath);
        var registry = new BlindAccountRegistry(_rig.Root);

        registry.Load().Should().Be(BlindRegistryState.Damaged);

        registry.Problem.Should().NotBeNullOrWhiteSpace();
        registry.Problem.Should().Contain("accounts.json").And.Contain("Nothing was");
        if (content.Length >= 12) registry.Problem.Should().NotContain(content[..12], "the sentence never repeats the file's content");
        registry.Current.Entries.Should().BeEmpty("nothing may be started from a damaged registry");
        var act = () => registry.Update(f => f);
        act.Should().Throw<BlindRegistryDamagedException>();
        File.ReadAllBytes(FilePath).Should().Equal(before, "a damaged registry is never started empty over the folders it names");
    }

    [Fact]
    public void AMissingFile_WithAccountFolders_IsDamaged_NotEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_rig.Root, "accounts", BlindAccountEntry.NewId()));
        var registry = new BlindAccountRegistry(_rig.Root);

        registry.Load().Should().Be(BlindRegistryState.Damaged);

        registry.Problem.Should().Contain("missing");
        File.Exists(FilePath).Should().BeFalse("nothing was written");
    }

    [Fact]
    public void AnEmptyAccountsFolder_IsNotAProblem()
    {
        Directory.CreateDirectory(Path.Combine(_rig.Root, "accounts"));
        new BlindAccountRegistry(_rig.Root).Load().Should().Be(BlindRegistryState.Missing);
    }

    [Fact]
    public void ADamagedFile_ThatIsFixed_CanBeLoadedAgain()
    {
        File.WriteAllText(FilePath, "garbage");
        var registry = new BlindAccountRegistry(_rig.Root);
        registry.Load().Should().Be(BlindRegistryState.Damaged);

        File.WriteAllText(FilePath, "{\"version\":1,\"accounts\":[]}");

        registry.Load().Should().Be(BlindRegistryState.Loaded);
        registry.Problem.Should().BeNull();
        registry.Update(f => f with { Accounts = [Account()] }).Entries.Should().HaveCount(1);
    }

    [Fact]
    public void AChangeThatBreaksARule_IsRefused_AndNothingIsWritten()
    {
        var registry = new BlindAccountRegistry(_rig.Root);
        registry.Update(_ => new BlindAccountsFile(Accounts: [Account()]));
        var before = File.ReadAllBytes(FilePath);

        var act = () => registry.Update(f => f with { Accounts = [.. f.Entries, new BlindAccountEntry("../escape", BlindAccountLayouts.Dir, "x")] });

        act.Should().Throw<InvalidOperationException>();
        File.ReadAllBytes(FilePath).Should().Equal(before);
        registry.Current.Entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task ManyWritersAtOnce_NeverLoseAChange_AndNeverLeaveAnInvalidFile()
    {
        var registry = new BlindAccountRegistry(_rig.Root);
        registry.Load();
        var tasks = Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            registry.Update(f => f with { Accounts = [.. f.Entries, Account(name: "n" + i)] }))).ToArray();
        await Task.WhenAll(tasks);

        var reread = new BlindAccountRegistry(_rig.Root);
        reread.Load().Should().Be(BlindRegistryState.Loaded);
        reread.Current.Entries.Select(e => e.Name).Should().BeEquivalentTo(Enumerable.Range(0, 24).Select(i => "n" + i));
    }

    [Theory]
    [InlineData("legacy", true)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdefa", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("../../0123456789abcdef01234567", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAnIdThisAppMakes_IsAnId(string? id, bool valid) => BlindAccountEntry.IsValidId(id).Should().Be(valid);

    [Fact]
    public void AFreshId_IsRandom_AndValid()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => BlindAccountEntry.NewId()).ToList();
        ids.Should().OnlyHaveUniqueItems().And.OnlyContain(id => BlindAccountEntry.IsValidId(id) && id != "legacy");
    }

    private sealed class StepCrash(string step) : IBlindCrashPoints
    {
        public void Reach(string reached)
        {
            if (reached == step) throw new SimulatedCrash(step);
        }
    }
}
