using System.Net;
using System.Net.Http.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A console password change while the console state file is damaged or unreadable (BMB-85): it is refused with the
/// reason, and the file is left as it was.
/// </summary>
public class BlindConsoleStateFileEndpointTests : IAsyncLifetime
{
    private const string Old = "old-console-pw-1";
    private const string New = "new-console-pw-22";

    private readonly BlindNodeFactory _node = new();
    private HttpClient _api = null!;

    private string StateFile => Path.Combine(_node.DataPath, "blind", "console.json");

    public async Task InitializeAsync()
    {
        await _node.InitializeNodeAsync(displayName: "StateFileNode");
        _api = _node.CreateClient();
        (await _api.PostAsJsonAsync("/api/blind/console/password", new { newPassword = Old }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    public Task DisposeAsync()
    {
        _api.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> ChangeAsync() =>
        _api.PostAsJsonAsync("/api/blind/console/password", new { currentPassword = Old, newPassword = New });

    [Fact]
    public async Task DamagedFile_RefusesTheChange_SaysWhy_AndLeavesTheFileAsItWas()
    {
        var damaged = "{ \"hash_b64\": \""; // cut off, as after an interrupted write
        File.WriteAllText(StateFile, damaged);

        var response = await ChangeAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("damaged");
        File.ReadAllText(StateFile).Should().Be(damaged);
    }

    [Fact]
    public async Task UnreadableFile_RefusesTheChange_SaysWhy_AndLeavesTheFileAsItWas()
    {
        var before = File.ReadAllText(StateFile);

        using (new FileStream(StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var response = await ChangeAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await response.Content.ReadAsStringAsync()).Should().Contain("cannot be read");
        }

        File.ReadAllText(StateFile).Should().Be(before);
    }
}
