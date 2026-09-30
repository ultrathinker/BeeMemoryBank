using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.BlindBackup;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The console password is the DEFAULT restic password (owner request). The node keeps only a hash of
/// the console password, so the plaintext can be copied at one moment only: inside the handler that
/// sets it (the console page and <c>bmb blind init</c> both go through it). The copy happens while the
/// restic password is undecided and no repository exists; after that it must never move, because the
/// repository is encrypted under the password it was created with.
/// </summary>
public class BlindResticDefaultPasswordTests : IAsyncLifetime
{
    private readonly BlindNodeFactory _factory = new();
    private readonly string _repoDir =
        Path.Combine(Path.GetTempPath(), "bmb_blind_default_pw_" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => _factory.InitializeNodeAsync(displayName: "DefaultPwNode");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        if (Directory.Exists(_repoDir))
            try { Directory.Delete(_repoDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private BlindBackupSettingsStore Store => _factory.Services.GetRequiredService<BlindBackupSettingsStore>();

    private static async Task SetConsolePasswordAsync(HttpClient client, string password, string? current = null) =>
        (await client.PostAsJsonAsync("/api/blind/console/password", new { newPassword = password, currentPassword = current }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

    private async Task ConfigureRepositoryAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { repoType = "folder", repoFolder = _repoDir }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task AFreshNode_SettingTheConsolePassword_AlsoSetsResticPassword_AndTheBackupIsConfigured()
    {
        using var client = _factory.CreateClient();
        await ConfigureRepositoryAsync(client);
        (await ReadSettingsAsync(client)).GetProperty("configured").GetBoolean().Should().BeFalse(
            "a repository without a password is not a backup");

        await SetConsolePasswordAsync(client, "console-pw-one");

        var stored = Store.Load();
        stored.ResticPassword.Should().Be("console-pw-one");
        stored.ResticPasswordSource.Should().Be(ResticPasswordSources.Console);
        var shown = await ReadSettingsAsync(client);
        shown.GetProperty("configured").GetBoolean().Should().BeTrue();
        shown.GetProperty("settings").GetProperty("resticPassword").GetString().Should().Be("••••",
            "the form never gets the password itself");
        shown.GetProperty("settings").GetProperty("resticPasswordSource").GetString().Should().Be("console");
    }

    [Fact]
    public async Task AnExplicitResticPassword_SetFirst_IsNotTouchedByTheConsolePassword()
    {
        using var client = _factory.CreateClient();
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { resticPassword = "operators-own" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await SetConsolePasswordAsync(client, "console-pw-one");
        await SetConsolePasswordAsync(client, "console-pw-two", current: "console-pw-one");

        Store.Load().ResticPassword.Should().Be("operators-own", "an explicit password wins, and later changes never touch it");
        Store.Load().ResticPasswordSource.Should().Be(ResticPasswordSources.Explicit);
    }

    [Fact]
    public async Task AnExplicitResticPassword_SetAfterTheDefault_TakesOverForGood()
    {
        using var client = _factory.CreateClient();
        await SetConsolePasswordAsync(client, "console-pw-one");
        Store.Load().ResticPassword.Should().Be("console-pw-one");

        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { resticPassword = "operators-own" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await SetConsolePasswordAsync(client, "console-pw-two", current: "console-pw-one");

        Store.Load().ResticPassword.Should().Be("operators-own");
    }

    [Fact]
    public async Task AResticPasswordFromBeforeTheSourceWasRecorded_CountsAsTheOperatorsOwn()
    {
        // A settings.json written by a release without the source field: the password is somebody's choice.
        using var client = _factory.CreateClient();
        Store.Save(new BlindBackupSettings { ResticPassword = "written-long-ago" });

        await SetConsolePasswordAsync(client, "console-pw-one");

        Store.Load().ResticPassword.Should().Be("written-long-ago");
    }

    [Fact]
    public async Task ASettingsFileThatCannotBeRead_IsLeftAsItIs_ByTheConsolePassword()
    {
        // Hand-edited into something unreadable: it may still hold the password of a repository, which
        // the operator can recover by repairing the file. A console password change must not replace it.
        using var client = _factory.CreateClient();
        var file = Path.Combine(_factory.DataPath, "blind", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        const string broken = "{ \"ResticPassword\": \"the-real-one\" ";
        await File.WriteAllTextAsync(file, broken);

        await SetConsolePasswordAsync(client, "console-pw-one");

        (await File.ReadAllTextAsync(file)).Should().Be(broken);
    }

    [Fact]
    public async Task OnceTheRepositoryExists_TheConsolePasswordNoLongerMovesResticPassword()
    {
        using var client = _factory.CreateClient();
        await ConfigureRepositoryAsync(client);
        await SetConsolePasswordAsync(client, "console-pw-one");
        Store.Load().ResticPassword.Should().Be("console-pw-one");

        Store.MarkRepositoryInUse(); // the first backup found or created the repository

        await SetConsolePasswordAsync(client, "console-pw-two", current: "console-pw-one");
        Store.Load().ResticPassword.Should().Be("console-pw-one",
            "the repository is encrypted under the first password; a changed setting would lock the owner out of the backups");
        Store.Load().RepoInUse.Should().BeTrue();
        (await ReadSettingsAsync(client)).GetProperty("settings").GetProperty("repoInUse").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AFolderRepositoryThatAlreadyHasItsConfigFile_IsAnExistingRepository()
    {
        using var client = _factory.CreateClient();
        Directory.CreateDirectory(_repoDir);
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "config"), "restic repository config");
        await ConfigureRepositoryAsync(client);

        await SetConsolePasswordAsync(client, "console-pw-one");

        Store.Load().ResticPassword.Should().BeNull(
            "somebody else's repository is encrypted under a password this node cannot guess");
    }

    [Fact]
    public async Task TheConsolePasswordChangedTwiceBeforeTheFirstBackup_KeepsBothInStep()
    {
        using var client = _factory.CreateClient();
        await ConfigureRepositoryAsync(client);

        await SetConsolePasswordAsync(client, "console-pw-one");
        await SetConsolePasswordAsync(client, "console-pw-two", current: "console-pw-one");
        Store.Load().ResticPassword.Should().Be("console-pw-two");
        await SetConsolePasswordAsync(client, "console-pw-three", current: "console-pw-two");

        var stored = Store.Load();
        stored.ResticPassword.Should().Be("console-pw-three");
        stored.ResticPasswordSource.Should().Be(ResticPasswordSources.Console);
    }

    [Fact]
    public async Task ARefusedChange_MovesNothing()
    {
        using var client = _factory.CreateClient();
        await SetConsolePasswordAsync(client, "console-pw-one");

        (await client.PostAsJsonAsync("/api/blind/console/password",
            new { newPassword = "console-pw-two", currentPassword = "wrong" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/blind/console/password",
            new { newPassword = "short", currentPassword = "console-pw-one" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Store.Load().ResticPassword.Should().Be("console-pw-one");
    }

    [Fact]
    public void AStaleSave_CannotTurnTheRepositoryInUseFlagOff()
    {
        // A settings write that read the file before the first backup, saved after it.
        var stale = Store.Load();
        Store.MarkRepositoryInUse();

        Store.Save(stale);

        Store.Load().RepoInUse.Should().BeTrue("a repository, once used, stays used until the wipe deletes the settings");
    }

    private static async Task<JsonElement> ReadSettingsAsync(HttpClient client) =>
        await (await client.GetAsync("/api/blind/backup/settings")).Content.ReadFromJsonAsync<JsonElement>();
}
