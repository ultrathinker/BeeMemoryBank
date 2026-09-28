using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// "Open an existing profile" in the Windows app: what the native picker returns decides between opening
/// a profile and restoring a backup — so a backup never has to be typed into the restore form, and is
/// never opened as if it were a profile folder.
/// </summary>
public sealed class ExistingProfileTargetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_existing_" + Guid.NewGuid().ToString("N"));

    public ExistingProfileTargetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AnAndroidBackupFile_IsABackup()
    {
        var file = await AndroidBackupAsync(_dir, "phone.bmbbackup");

        var target = ExistingProfileTarget.Classify(file);

        target.Kind.Should().Be(ExistingProfileKind.Backup);
        target.BackupPath.Should().Be(file);
    }

    [Fact]
    public void AFileWithTheExtensionButNotTheFormat_IsNot()
    {
        var fake = Path.Combine(_dir, "fake.bmbbackup");
        File.WriteAllText(fake, "not really a backup");

        ExistingProfileTarget.Classify(fake).Kind.Should().Be(ExistingProfileKind.Profile);
    }

    [Fact]
    public void ABlindNodeBackupFolder_IsABackup()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "backup")).FullName;
        File.WriteAllText(Path.Combine(folder, "backup.recovery-set.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "beememorybank.db"), "");

        ExistingProfileTarget.Classify(folder).Should().Be(
            new ExistingProfileTarget(ExistingProfileKind.Backup, folder, folder),
            "a database copy with its recovery set restores, it is not a profile to open");
    }

    [Fact]
    public void AResticRepositoryWithItsRecoverySetBeside_IsABackup()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_dir, "repo")).FullName;
        File.WriteAllText(repo + ".recovery-set.json", "{}");

        ExistingProfileTarget.Classify(repo + Path.DirectorySeparatorChar).BackupPath.Should().Be(repo);
    }

    [Fact]
    public async Task AFolderWherePhoneBackupsWereSaved_GivesTheNewestOne()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "Downloads")).FullName;
        var older = await AndroidBackupAsync(folder, "bmb-phone-1.bmbbackup");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        var newer = await AndroidBackupAsync(folder, "bmb-phone-2.bmbbackup");
        File.WriteAllText(Path.Combine(folder, "decoy.bmbbackup"), "no");
        File.SetLastWriteTimeUtc(Path.Combine(folder, "decoy.bmbbackup"), DateTime.UtcNow.AddDays(1));

        var target = ExistingProfileTarget.Classify(folder);

        target.Kind.Should().Be(ExistingProfileKind.Backup);
        target.BackupPath.Should().Be(newer, "the newest REAL phone backup, not a newer file that only looks like one");
    }

    [Fact]
    public async Task AProfileFolder_StaysAProfile_EvenIfItHoldsAPhoneBackup()
    {
        var profile = Directory.CreateDirectory(Path.Combine(_dir, "profile")).FullName;
        File.WriteAllText(Path.Combine(profile, "beememorybank.db"), "");
        await AndroidBackupAsync(profile, "saved.bmbbackup");

        ExistingProfileTarget.Classify(profile).Kind.Should().Be(ExistingProfileKind.Profile);
    }

    [Fact]
    public void AnOrdinaryFolder_IsAProfile() =>
        ExistingProfileTarget.Classify(_dir).Kind.Should().Be(ExistingProfileKind.Profile);

    [Fact]
    public void RestoreFormUrl_CarriesThePathEscaped()
    {
        var url = ExistingProfileTarget.RestoreFormUrl("http://127.0.0.1:5310/", @"D:\Backups\phone & co\b.bmbbackup");

        url.GetLeftPart(UriPartial.Path).Should().Be("http://127.0.0.1:5310/Setup");
        Uri.UnescapeDataString(url.Query).Should().Be(@"?step=restore&backup=D:\Backups\phone & co\b.bmbbackup");
        url.Query.TrimStart('?').Split('&').Should().HaveCount(2, "an & in the path must not split the query");
    }

    private static async Task<string> AndroidBackupAsync(string folder, string name)
    {
        var source = Path.Combine(folder, name + ".src");
        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(300));
        var file = Path.Combine(folder, name);
        await AndroidBackupWriter.WriteAsync(source, file, RandomNumberGenerator.GetBytes(32), Guid.NewGuid(),
            "android-backup:x", "{\"format\":\"bmb-recovery-set-v1\"}", 1024);
        File.Delete(source);
        return file;
    }
}
