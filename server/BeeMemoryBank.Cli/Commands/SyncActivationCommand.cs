using System.Text;
using System.Text.Json;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// <c>bmb sync readiness</c> and <c>bmb sync activate</c> (docs/design/step-a-spec.md 5.3, 5.4): the metadata activation of
/// the running node, through its Api with the internal key, as <c>bmb dek-rotate</c> does.
/// </summary>
public static class SyncActivationCommand
{
    private static readonly Dictionary<string, string> BlockerWords = new()
    {
        ["peer_not_ready"] = "a device has not shown that it runs 2.6.0 or newer",
        ["vault_locked"] = "this node is locked",
        ["not_superadmin"] = "a device does not see this node as a superadmin",
        ["rotation_pending"] = "a key rotation is in progress",
        ["events_held"] = "this node holds changes it has not applied yet (unlock and wait)",
        ["already_activated"] = "metadata encryption is already on",
    };

    private static HttpClient CreateClient(string dataPath)
    {
        var url = Environment.GetEnvironmentVariable("BMB_API_URL") ?? "http://localhost:5300";
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (string.IsNullOrEmpty(key))
        {
            var keyFile = Path.Combine(dataPath, ".internal-key");
            if (File.Exists(keyFile))
            {
                try { key = File.ReadAllText(keyFile).Trim(); } catch { /* unreadable: the Api answers 404 without it */ }
            }
        }
        // The peers are asked for 10 s each, in parallel: a minute is plenty.
        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(1) };
        if (!string.IsNullOrEmpty(key)) http.DefaultRequestHeaders.Add("X-Internal-Key", key);
        http.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        return http;
    }

    public static async Task<int> ReadinessAsync(string dataPath, TextWriter? output = null)
    {
        output ??= Console.Out;
        try
        {
            using var http = CreateClient(dataPath);
            var resp = await http.GetAsync("/api/sync/readiness");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                await output.WriteLineAsync($"Error: {(int)resp.StatusCode} {body}");
                return 1;
            }
            Print(JsonDocument.Parse(body).RootElement, output);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> ActivateAsync(string dataPath, string password, bool yes, TextWriter? output = null)
    {
        output ??= Console.Out;
        if (!yes)
        {
            await output.WriteLineAsync(
                "Metadata encryption cannot be switched off again. Take a fresh backup of this node first and keep it " +
                "offline: it is the only way back. Every device must run 2.6.0 or newer. Run again with --yes to switch it on.");
            return 1;
        }
        try
        {
            using var http = CreateClient(dataPath);
            var resp = await http.PostAsync("/api/sync/activation",
                new StringContent(JsonSerializer.Serialize(new { password }), Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            switch ((int)resp.StatusCode)
            {
                case 200:
                    var ok = JsonDocument.Parse(body).RootElement;
                    await output.WriteLineAsync(
                        $"Metadata encryption is on: activation {ok.GetProperty("activation_id").GetString()}, " +
                        $"key generation {ok.GetProperty("generation").GetInt32()}. Every device switches as it syncs.");
                    return 0;
                case 409:
                    await output.WriteLineAsync("Not activated:");
                    foreach (var b in JsonDocument.Parse(body).RootElement.GetProperty("blockers").EnumerateArray())
                        await output.WriteLineAsync("  - " + Describe(b));
                    return 1;
                case 401:
                    await output.WriteLineAsync("Error: wrong master password.");
                    return 1;
                case 423:
                    await output.WriteLineAsync("Error: the node is locked; unlock it first.");
                    return 1;
                default:
                    await output.WriteLineAsync($"Error: {(int)resp.StatusCode} {body}");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }

    private static void Print(JsonElement report, TextWriter output)
    {
        if (report.GetProperty("activated").GetBoolean())
        {
            var a = report.GetProperty("activation");
            output.WriteLine(a.ValueKind == JsonValueKind.Object
                ? $"Metadata encryption is on (activation {a.GetProperty("activation_id").GetString()}, {a.GetProperty("activated_at").GetString()})."
                : "Metadata encryption is on.");
            return;
        }
        output.WriteLine(report.GetProperty("ready").GetBoolean()
            ? "Ready: metadata encryption can be switched on (bmb sync activate)."
            : "Not ready:");
        foreach (var b in report.GetProperty("blockers").EnumerateArray())
            output.WriteLine("  - " + Describe(b));
        output.WriteLine();
        output.WriteLine($"{"Device",-28} {"Kind",-6} {"Status",-12} {"Version",-10} Seen");
        foreach (var p in report.GetProperty("peers").EnumerateArray())
        {
            var seen = p.GetProperty("seen_at").ValueKind == JsonValueKind.String ? p.GetProperty("seen_at").GetString() : "never";
            var version = p.GetProperty("app_version").ValueKind == JsonValueKind.String ? p.GetProperty("app_version").GetString() : "-";
            output.WriteLine($"{p.GetProperty("display_name").GetString(),-28} {p.GetProperty("kind").GetString(),-6} " +
                             $"{p.GetProperty("status").GetString(),-12} {version,-10} {seen}");
        }
    }

    private static string Describe(JsonElement blocker)
    {
        var code = blocker.GetProperty("code").GetString() ?? "";
        var words = BlockerWords.GetValueOrDefault(code, code);
        return blocker.TryGetProperty("display_name", out var name) && name.ValueKind == JsonValueKind.String
            ? $"{name.GetString()}: {words} ({blocker.GetProperty("status").GetString()})"
            : words;
    }
}
