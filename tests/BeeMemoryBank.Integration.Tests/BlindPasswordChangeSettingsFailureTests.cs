using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.BlindConsole;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A console password change reads the backup settings (the console password is the default restic
/// password), so the settings must be readable BEFORE anything changes: a file the node cannot read —
/// a directory in its place, no permission, another process holding it, content that is not JSON —
/// refuses the change with a plain message and leaves the console password exactly as it was.
/// </summary>
public class BlindPasswordChangeSettingsFailureTests : IAsyncLifetime
{
    private readonly BlindNodeFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(displayName: "FailureNode");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private string SettingsFile => Path.Combine(_factory.DataPath, "blind", "settings.json");
    private BlindConsoleAuthService Auth => _factory.Services.GetRequiredService<BlindConsoleAuthService>();

    private static Task<HttpResponseMessage> SetAsync(HttpClient client, string password, string? current = null) =>
        client.PostAsJsonAsync("/api/blind/console/password", new { newPassword = password, currentPassword = current });

    private static async Task ShouldBeRefusedPlainlyAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        error.Should().Contain("was not changed").And.Contain("settings").And.Contain("try again",
            "what is wrong, that nothing changed, and what to do");
    }

    [Fact]
    public async Task ADirectoryWhereTheSettingsFileShouldBe_RefusesTheFirstPassword_AndSetsNothing()
    {
        Directory.CreateDirectory(SettingsFile);
        using var client = _factory.CreateClient();

        await ShouldBeRefusedPlainlyAsync(await SetAsync(client, "console-pw-one"));

        Auth.HasPassword().Should().BeFalse("nothing was changed");
    }

    [Fact]
    public async Task ASettingsFileThatIsNotJson_RefusesTheChange_AndKeepsBothTheFileAndThePassword()
    {
        using var client = _factory.CreateClient();
        (await SetAsync(client, "console-pw-one")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        const string broken = "{ \"ResticPassword\": \"the-real-one\" ";
        await File.WriteAllTextAsync(SettingsFile, broken);

        await ShouldBeRefusedPlainlyAsync(await SetAsync(client, "console-pw-two", current: "console-pw-one"));

        (await File.ReadAllTextAsync(SettingsFile)).Should().Be(broken);
        Auth.Verify("console-pw-one", null).Ok.Should().BeTrue("the old password still works");
        Auth.Verify("console-pw-two", null).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task ASettingsFileTheNodeMayNotRead_RefusesTheChange_AndKeepsThePassword()
    {
        using var client = _factory.CreateClient();
        (await SetAsync(client, "console-pw-one")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        // Windows: another handle holds the file. Elsewhere: no permission (root ignores it: the test then has nothing to prove).
        using var hold = OperatingSystem.IsWindows() ? new FileStream(SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(SettingsFile, UnixFileMode.None);
        try
        {
            if (!OperatingSystem.IsWindows() && CanRead(SettingsFile)) return;

            await ShouldBeRefusedPlainlyAsync(await SetAsync(client, "console-pw-two", current: "console-pw-one"));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(SettingsFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        hold?.Dispose();
        Auth.Verify("console-pw-one", null).Ok.Should().BeTrue("the old password still works");
    }

    [Fact]
    public async Task AFolderThatCannotBeEntered_IsNotTakenForNoSettingsFile()
    {
        if (OperatingSystem.IsWindows()) return;
        using var client = _factory.CreateClient();
        (await SetAsync(client, "console-pw-one")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var dir = Path.GetDirectoryName(SettingsFile)!;
        File.SetUnixFileMode(dir, UnixFileMode.None);
        try
        {
            if (CanRead(SettingsFile)) return; // root ignores modes

            await ShouldBeRefusedPlainlyAsync(await SetAsync(client, "console-pw-two", current: "console-pw-one"));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Auth.Verify("console-pw-one", null).Ok.Should().BeTrue("nothing was changed");
    }

    [Fact]
    public async Task ASettingsFileThatReadsButCannotBeWritten_StillChangesThePassword_AndLeavesResticAlone()
    {
        if (!OperatingSystem.IsWindows()) return; // there the read-only attribute is the only per-file write lock
        using var client = _factory.CreateClient();
        (await SetAsync(client, "console-pw-one")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.SetAttributes(SettingsFile, FileAttributes.ReadOnly);
        try
        {
            var changed = await SetAsync(client, "console-pw-two", current: "console-pw-one");

            changed.StatusCode.Should().Be(HttpStatusCode.NoContent, "the settings were readable: nothing to refuse, no 500 after the fact");
            Auth.Verify("console-pw-two", null).Ok.Should().BeTrue();
            _factory.Services.GetRequiredService<BeeMemoryBank.Api.Services.BlindBackup.BlindBackupSettingsStore>()
                .Load().ResticPassword.Should().Be("console-pw-one", "the restic password stays what the repository would be created with");
        }
        finally
        {
            File.SetAttributes(SettingsFile, FileAttributes.Normal);
        }
    }

    private static bool CanRead(string path)
    {
        try { File.ReadAllText(path); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}