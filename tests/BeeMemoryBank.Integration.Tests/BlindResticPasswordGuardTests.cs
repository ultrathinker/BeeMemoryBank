using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.BlindBackup;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The settings refuse another restic password when a repository EXISTS at the location being saved, not only when
/// this node's own marker says it used that exact location: a settings file from 1.0.12 has no marker, a repository
/// may sit at a second location or behind another spelling of the same folder, and for a bucket the node cannot look.
/// Restic opens an existing repository with whatever password it is given, so a changed one orphans every snapshot.
/// </summary>
public class BlindResticPasswordGuardTests : IAsyncLifetime
{
    private const string Old = "the-repository-password";
    private const string Other = "another-password";

    private readonly BlindNodeFactory _factory = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb_guard_" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => _factory.InitializeNodeAsync(displayName: "GuardNode");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private BlindBackupSettingsStore Store => _factory.Services.GetRequiredService<BlindBackupSettingsStore>();
    private string SettingsFile => Path.Combine(_factory.DataPath, "blind", "settings.json");

    /// <summary>A folder that holds what restic leaves behind: a repository.</summary>
    private string Repository(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config"), "restic repository");
        return dir;
    }

    private string EmptyFolder(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A settings.json exactly as release 1.0.12 wrote it: no marker, no source, no flags.</summary>
    private void Write1012(object settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings));
    }

    private Task<HttpResponseMessage> PutAsync(HttpClient client, object body) =>
        client.PutAsJsonAsync("/api/blind/backup/settings", body);

    private async Task ShouldBeRefusedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("cannot be changed").And.Contain("new repository");
    }

    // ── a folder repository is recognised by its content, whatever this node remembers ──────────

    [Fact]
    public async Task ASettingsFileFrom1012_WithAFolderThatHoldsARepository_RefusesAnotherPassword()
    {
        var repo = Repository("a");
        Write1012(new { RepoType = 0, RepoFolder = repo, ResticPassword = Old });
        using var client = _factory.CreateClient();

        await ShouldBeRefusedAsync(await PutAsync(client, new { resticPassword = Other }));
        await ShouldBeRefusedAsync(await PutAsync(client, new { resticPassword = "" }));

        Store.Load().ResticPassword.Should().Be(Old);
    }

    [Fact]
    public async Task PointingAtAnExistingRepositoryAtASecondLocation_WithAnotherPassword_IsRefused()
    {
        var a = EmptyFolder("a");
        var b = Repository("b");
        Write1012(new { RepoType = 0, RepoFolder = a, ResticPassword = Old });
        using var client = _factory.CreateClient();

        await ShouldBeRefusedAsync(await PutAsync(client, new { repoFolder = b, resticPassword = Other }));

        Store.Load().ResticPassword.Should().Be(Old);
    }

    [Fact]
    public async Task TheSameRepositoryUnderAnotherSpelling_IsStillTheSameRepository()
    {
        var repo = Repository("a");
        Write1012(new { RepoType = 0, RepoFolder = repo, ResticPassword = Old });
        using var client = _factory.CreateClient();

        // A trailing slash and a dot segment: two more names for the folder that holds `config`.
        foreach (var alias in new[] { repo + Path.DirectorySeparatorChar, Path.Combine(repo, ".") })
            await ShouldBeRefusedAsync(await PutAsync(client, new { repoFolder = alias, resticPassword = Other }));

        Store.Load().ResticPassword.Should().Be(Old);
    }

    [Fact]
    public async Task ASymlinkToTheRepository_IsTheSameRepository()
    {
        var repo = Repository("a");
        var link = Path.Combine(_root, "link");
        try { Directory.CreateSymbolicLink(link, repo); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; } // Windows without the privilege
        Write1012(new { RepoType = 0, RepoFolder = EmptyFolder("empty"), ResticPassword = Old });
        using var client = _factory.CreateClient();

        await ShouldBeRefusedAsync(await PutAsync(client, new { repoFolder = link, resticPassword = Other }));
    }

    [Fact]
    public async Task OnWindows_TheSameFolderInAnotherCase_IsTheSameRepository()
    {
        if (!OperatingSystem.IsWindows()) return;
        var repo = Repository("Case");
        Write1012(new { RepoType = 0, RepoFolder = EmptyFolder("empty"), ResticPassword = Old });
        using var client = _factory.CreateClient();

        await ShouldBeRefusedAsync(await PutAsync(client, new { repoFolder = repo.ToUpperInvariant(), resticPassword = Other }));
    }

    // ── a folder that cannot be inspected may hold a repository: only "config is absent" allows a change ──

    /// <summary>Linux: no permission on the repository folder, so `config` cannot be looked at. Returns false when the
    /// permissions do not bite (root), and the caller has nothing to prove.</summary>
    private static bool Lock(string dir)
    {
        if (OperatingSystem.IsWindows()) return false;
        File.SetUnixFileMode(dir, UnixFileMode.None);
        try { File.GetAttributes(Path.Combine(dir, "config")); }
        catch (UnauthorizedAccessException) { return true; }
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return false;
    }

    private static void Unlock(string dir)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task AFolderRepositoryThatCannotBeInspected_IsTreatedAsExisting_ForAPasswordChange()
    {
        var repo = Repository("locked");
        Write1012(new { RepoType = 0, RepoFolder = repo, ResticPassword = Old });
        using var client = _factory.CreateClient();
        if (!Lock(repo)) return;
        try
        {
            await ShouldBeRefusedAsync(await PutAsync(client, new { resticPassword = Other }));
            await ShouldBeRefusedAsync(await PutAsync(client, new { resticPassword = "" }));
        }
        finally
        {
            Unlock(repo);
        }
        Store.Load().ResticPassword.Should().Be(Old);
    }

    [Fact]
    public async Task AFolderThatCannotBeInspected_IsNotAdoptedByTheConsolePassword()
    {
        var repo = Repository("locked-default");
        using var client = _factory.CreateClient();
        (await PutAsync(client, new { repoFolder = repo })).StatusCode.Should().Be(HttpStatusCode.OK);
        if (!Lock(repo)) return;
        try
        {
            (await client.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-one" }))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        finally
        {
            Unlock(repo);
        }
        Store.Load().ResticPassword.Should().BeNull("a repository may be in there: the console password is never its password by default");
    }

    [Fact]
    public async Task ADirectoryNamedConfig_IsNotProofThatNothingIsThere()
    {
        var folder = EmptyFolder("odd");
        Directory.CreateDirectory(Path.Combine(folder, "config"));
        Write1012(new { RepoType = 0, RepoFolder = folder, ResticPassword = Old });
        using var client = _factory.CreateClient();

        await ShouldBeRefusedAsync(await PutAsync(client, new { resticPassword = Other }));
    }

    // ── what still saves ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewEmptyFolder_WithANewPassword_Saves()
    {
        Write1012(new { RepoType = 0, RepoFolder = Repository("a"), ResticPassword = Old });
        using var client = _factory.CreateClient();

        (await PutAsync(client, new { repoFolder = Path.Combine(_root, "fresh"), resticPassword = Other }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Store.Load().ResticPassword.Should().Be(Other);
    }

    [Fact]
    public async Task TheSamePassword_TheMask_AndOtherFields_StillSave_OverAnExistingRepository()
    {
        Write1012(new { RepoType = 0, RepoFolder = Repository("a"), ResticPassword = Old });
        using var client = _factory.CreateClient();

        foreach (var body in new object[] { new { resticPassword = Old }, new { resticPassword = "••••" }, new { keepDaily = 5 } })
            (await PutAsync(client, body)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ANodeWithNoPasswordYet_CanBePointedAtAnExistingRepository_AndGiveItsPassword()
    {
        // After a wipe the settings are gone; the repository was not touched and is reconnected with its own password.
        using var client = _factory.CreateClient();

        (await PutAsync(client, new { repoFolder = Repository("survivor"), resticPassword = Old }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Store.Load().ResticPassword.Should().Be(Old);
    }

    // ── a bucket cannot be looked into: the marker, compared by identity, decides ──────────────

    private static object S3(string endpoint, string bucket, string prefix, string? password = null) => new
    {
        RepoType = 1, S3Endpoint = endpoint, S3Bucket = bucket, S3Prefix = prefix, S3AccessKey = "ak", S3SecretKey = "sk", ResticPassword = password,
    };

    [Fact]
    public async Task ABackupsBucket_UnderEquivalentSpellings_IsTheSameRepository()
    {
        using var client = _factory.CreateClient();
        Store.Save(new BlindBackupSettings
        {
            RepoType = BlindRepoType.S3, S3Endpoint = "https://S3.Example.com:443/", S3Bucket = "bmb", S3Prefix = "blind/repo/",
            S3AccessKey = "ak", S3SecretKey = "sk", ResticPassword = Old, ResticPasswordSource = ResticPasswordSources.Explicit,
        });
        Store.MarkRepositoryInUse(Store.Load().RepositoryKey()); // a backup used it

        foreach (var (endpoint, bucket, prefix) in new[]
                 {
                     ("https://s3.example.com", "bmb", "blind/repo"),
                     ("https://s3.example.com/", "bmb", "/blind//repo/"),
                     ("HTTPS://S3.EXAMPLE.COM:443", "/bmb/", "blind/repo"),
                 })
            await ShouldBeRefusedAsync(await PutAsync(client,
                new { s3Endpoint = endpoint, s3Bucket = bucket, s3Prefix = prefix, resticPassword = Other }));

        Store.Load().ResticPassword.Should().Be(Old);
    }

    [Fact]
    public async Task ABucketAtAnotherPrefix_WithANewPassword_Saves()
    {
        using var client = _factory.CreateClient();
        Store.Save(new BlindBackupSettings
        {
            RepoType = BlindRepoType.S3, S3Endpoint = "https://s3.example.com", S3Bucket = "bmb", S3Prefix = "blind/repo",
            S3AccessKey = "ak", S3SecretKey = "sk", ResticPassword = Old, ResticPasswordSource = ResticPasswordSources.Explicit,
        });
        Store.MarkRepositoryInUse(Store.Load().RepositoryKey());

        (await PutAsync(client, new { s3Prefix = "blind/new-repo", resticPassword = Other })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnS3FileFrom1012_WithAPasswordAndNoMarker_IsTreatedAsInUse_UntilPointedElsewhere()
    {
        Write1012(S3("https://s3.example.com", "bmb", "blind/repo", Old));
        using var client = _factory.CreateClient();

        await ShouldBeRefusedAsync(await PutAsync(client, new { resticPassword = Other }));
        (await PutAsync(client, new { s3Prefix = "blind/second", resticPassword = Other })).StatusCode.Should().Be(HttpStatusCode.OK,
            "another location is a new repository");
    }
}
