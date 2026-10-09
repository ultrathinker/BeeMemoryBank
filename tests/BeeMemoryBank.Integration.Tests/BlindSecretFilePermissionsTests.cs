using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Sync;
using BeeMemoryBank.TestSupport;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review release-a #7: the blind node's three secrets in clear - the identity seed, the TLS key, the backup credentials -
/// took over the data folder's inherited ACL on Windows, so in a folder outside the user's profile (C:\BMB\data, a service
/// install) every account of the computer could read them. They are now created owner-only, and the files an older build
/// left are repaired in place at start - never refused, so no existing node stops starting.
/// </summary>
public class BlindSecretFilePermissionsTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"bmb_blindacl_{Guid.NewGuid():N}");

    public BlindSecretFilePermissionsTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch { /* best-effort */ }
    }

    [WindowsAclFact]
    public void TheThreeWriters_InAFolderEveryAccountCanRead_CreateOwnerOnlyFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsAcl.MakeReadableByAllUsers(_data);

        var seed = new FileNodeKey(Path.Combine(_data, FileNodeKey.FileName));
        seed.Create();
        using (BlindTlsCertificate.LoadOrCreate(_data)) { }
        new BlindBackupSettingsStore(_data).Save(new BlindBackupSettings { ResticPassword = "restic-secret" });

        foreach (var file in new[] { seed.Path, BlindTlsCertificate.PathIn(_data), BlindBackupSettingsStore.SettingsFileIn(_data) })
        {
            WindowsAcl.UsersCanRead(file).Should().BeFalse($"{Path.GetFileName(file)} holds a secret in clear");
            WindowsAcl.OwnerHasFullControl(file).Should().BeTrue();
        }
    }

    [WindowsAclFact]
    public void FilesAnOlderBuildLeft_AreRepairedAtStart_AndStillWork()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsAcl.MakeReadableByAllUsers(_data);
        // As an older build wrote them: plain files with the folder's inherited ACL.
        var seed = new FileNodeKey(Path.Combine(_data, FileNodeKey.FileName));
        var publicKey = seed.Create();
        var seedBytes = File.ReadAllBytes(seed.Path);
        File.Delete(seed.Path);
        File.WriteAllBytes(seed.Path, seedBytes);
        Directory.CreateDirectory(Path.Combine(_data, "blind"));
        File.WriteAllText(BlindBackupSettingsStore.SettingsFileIn(_data), "{\"ResticPassword\":\"restic-secret\"}");
        WindowsAcl.UsersCanRead(seed.Path).Should().BeTrue("precondition: every account can read the old files");
        WindowsAcl.UsersCanRead(BlindBackupSettingsStore.SettingsFileIn(_data)).Should().BeTrue("precondition");
        var logger = new ListLogger();

        BlindRoleStartup.RepairSecretPermissions(_data, seed.Path, logger);

        WindowsAcl.UsersCanRead(seed.Path).Should().BeFalse();
        WindowsAcl.UsersCanRead(BlindBackupSettingsStore.SettingsFileIn(_data)).Should().BeFalse();
        seed.Matches(publicKey).Should().BeTrue("the repaired seed is the same seed, readable by the node");
        new BlindBackupSettingsStore(_data).Load().ResticPassword.Should().Be("restic-secret");
        logger.Lines.Should().Contain(l => l.Contains("now owner-only"));

        // A second start finds nothing to repair, and says nothing.
        var again = new ListLogger();
        BlindRoleStartup.RepairSecretPermissions(_data, seed.Path, again);
        again.Lines.Should().BeEmpty();
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [UnixModeFact]
    public void TheThreeWriters_CreateMode0600Files_WhateverTheFoldersUmaskWouldGive()
    {
        var seed = new FileNodeKey(Path.Combine(_data, FileNodeKey.FileName));
        seed.Create();
        using (BlindTlsCertificate.LoadOrCreate(_data)) { }
        new BlindBackupSettingsStore(_data).Save(new BlindBackupSettings { ResticPassword = "restic-secret" });

        foreach (var file in new[] { seed.Path, BlindTlsCertificate.PathIn(_data), BlindBackupSettingsStore.SettingsFileIn(_data) })
            File.GetUnixFileMode(file).Should().Be(OwnerOnly, $"{Path.GetFileName(file)} holds a secret in clear");
    }

    [Fact]
    public void TheRepair_NeverThrows_WhenThereIsNothingThere()
    {
        var logger = new ListLogger();

        var act = () => BlindRoleStartup.RepairSecretPermissions(_data, Path.Combine(_data, FileNodeKey.FileName), logger);

        act.Should().NotThrow();
        logger.Lines.Should().BeEmpty();
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
