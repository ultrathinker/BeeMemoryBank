using System;
using BeeMemoryBank.Desktop.Services;
using BeeMemoryBank.Hosting;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The WebView → shell half of the pickers: the addresses the Setup and restore pages navigate to
/// (<see cref="DesktopShellCommands"/>, rendered by the Web host — RestoreWizardPageTests checks the
/// button) are the ones the shell turns into a native picker, and nothing else is.
/// </summary>
public sealed class ShellCommandsTests
{
    [Fact]
    public void TheRestorePagesButton_OpensTheBackupFilePicker() =>
        ShellCommands.Match(new Uri(DesktopShellCommands.PickBackupFile)).Should().Be(ShellCommand.PickBackupFile);

    [Fact]
    public void TheSetupPagesExistingProfileButton_OpensTheFolderPicker() =>
        ShellCommands.Match(new Uri(DesktopShellCommands.OpenExistingProfile)).Should().Be(ShellCommand.OpenExistingProfile);

    [Theory]
    [InlineData("https://bmb-desktop.invalid/pick-backup-file?path=C:/x.bmbbackup")]
    [InlineData("https://bmb-desktop.invalid/pick-backup-file/more")]
    [InlineData("http://bmb-desktop.invalid/pick-backup-file")]
    [InlineData("https://bmb-desktop.invalid.example.com/pick-backup-file")]
    [InlineData("https://localhost:5301/Setup?step=restore")]
    public void AnythingElse_IsAnOrdinaryNavigation(string url) =>
        ShellCommands.Match(new Uri(url)).Should().Be(ShellCommand.None);
}
