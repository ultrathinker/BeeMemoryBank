using System.Text.RegularExpressions;
using BeeMemoryBank.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The page half of the Windows app's backup-file picker: the restore form carries a button, hidden outside
/// the app, whose address is the one the shell turns into the native picker (Desktop's ShellCommandsTests
/// holds the shell half).
/// </summary>
public sealed class RestoreFormFilePickerTests : IDisposable
{
    private readonly RecoveryTestFactory _api = new();
    private readonly BmbWebHostFactory _web = new();

    public void Dispose()
    {
        ((IDisposable)_web).Dispose();
        _api.Dispose();
    }

    [Fact]
    public async Task TheBackupForm_OffersTheWindowsAppsFilePicker_HiddenOutsideTheApp()
    {
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        using var browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var page = await (await browser.GetAsync("/Setup?step=restore")).Content.ReadAsStringAsync();

        page.Should().MatchRegex(
            @"<div data-desktop-only hidden>\s*<sl-button[^>]*href=""" + Regex.Escape(DesktopShellCommands.PickBackupFile) + "\"",
            "the button must navigate to the exact address the shell answers with the native picker");
    }
}
