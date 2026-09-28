extern alias WebApp;

using WebApp::BeeMemoryBank.Web.Pages;
using WebApp::BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>"Open an existing profile": a chosen file is a backup to restore only when a recognizer says so.</summary>
public class BackupFileRecognizerTests
{
    private sealed class Suffix(string suffix) : IBackupFileRecognizer
    {
        public bool Recognizes(string fullPath) => fullPath.EndsWith(suffix, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileARecognizerKnows_IsABackup_OtherwiseNot()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bmb-recognizer-{Guid.NewGuid():N}.bmbbackup");
        File.WriteAllText(file, "x");
        try
        {
            SetupModel.LooksLikeBlindBackup(file, [new Suffix(".bmbbackup")]).Should().BeTrue();
            SetupModel.LooksLikeBlindBackup(file, [new Suffix(".other")]).Should().BeFalse();
            SetupModel.LooksLikeBlindBackup(file).Should().BeFalse();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
