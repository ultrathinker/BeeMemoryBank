using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BeeMemoryBank.Cli.Commands;

public static class DekRotateCommand
{
    private static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var url = Environment.GetEnvironmentVariable("BMB_API_URL") ?? "http://localhost:5300";
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        var http = new HttpClient { BaseAddress = new Uri(url) };
        if (timeout.HasValue)
            http.Timeout = timeout.Value;
        if (!string.IsNullOrEmpty(key))
            http.DefaultRequestHeaders.Add("X-Internal-Key", key);
        http.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        return http;
    }

    /// <summary>
    /// Reads a password from stdin without echoing characters. Backspace works.
    /// Falls back to plain ReadLine if input is redirected (no tty).
    /// </summary>
    private static string ReadMaskedPassword()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var buf = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buf.ToString(); }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buf.Length > 0) buf.Length--;
                continue;
            }
            // Skip non-printing keys (arrows, function keys, etc).
            if (!char.IsControl(key.KeyChar)) buf.Append(key.KeyChar);
        }
    }

    public static async Task<int> HandleProposeAsync()
    {
        Console.Write("Enter Master Password: ");
        var pwd = ReadMaskedPassword();

        try
        {
            using var http = CreateClient();
            var req = new { masterPassword = pwd };
            var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("/api/dek-rotation/propose", content);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {resp.StatusCode} — {body}");
                return 1;
            }

            var json = JsonDocument.Parse(body);
            var commitEventId = json.RootElement.GetProperty("commitEventId").GetString();
            Console.WriteLine($"Proposed: commit-event-id={commitEventId}");
            Console.WriteLine("Run `bmb dek-rotate accept <commit-event-id>` to perform the rotation.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> HandleAcceptAsync(string commitEventId)
    {
        Console.Write("Enter Master Password: ");
        var pwd = ReadMaskedPassword();

        Console.WriteLine();
        Console.WriteLine("This will re-encrypt all article bodies/versions/conflicts/media, delete all agents, drop all other user key slots, and invalidate all recovery keys. A pre-rotation snapshot will be created automatically. Continue? [y/N] ");
        var confirm = (Console.ReadLine() ?? string.Empty).Trim();
        if (!(string.Equals(confirm, "y", StringComparison.OrdinalIgnoreCase)
              || string.Equals(confirm, "yes", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Cancelled.");
            return 1;
        }

        var cts = new CancellationTokenSource();
        var progressTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(3000, cts.Token);
                    using var pollHttp = CreateClient();
                    var resp = await pollHttp.GetAsync("/api/dek-rotation/progress", cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(cts.Token);
                        var json = JsonDocument.Parse(body);
                        var root = json.RootElement;
                        var pct = root.TryGetProperty("percentageComplete", out var p) ? p.GetInt32() : 0;
                        var step = root.TryGetProperty("currentStep", out var s) ? s.GetString() : "";
                        var msg = root.TryGetProperty("statusMessage", out var m) ? m.GetString() : "";
                        Console.WriteLine($"[{pct}%] {step}: {msg}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }, cts.Token);

        try
        {
            using var http = CreateClient(TimeSpan.FromMinutes(30));
            var req = new { commitEventId, masterPassword = pwd };
            var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("/api/dek-rotation/accept", content);
            cts.Cancel();
            await progressTask;
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {resp.StatusCode} — {body}");
                return 1;
            }

            Console.WriteLine("DEK rotation completed. Issue a new recovery key immediately.");
            return 0;
        }
        catch (Exception ex)
        {
            cts.Cancel();
            try { await progressTask; } catch { }
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> HandleProgressAsync()
    {
        try
        {
            using var http = CreateClient();
            var resp = await http.GetAsync("/api/dek-rotation/progress");
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {resp.StatusCode} — {body}");
                return 1;
            }

            var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var eventId = root.TryGetProperty("eventId", out var eid) ? eid.GetString() ?? "—" : "—";
            var step = root.TryGetProperty("currentStep", out var s) ? s.GetString() ?? "" : "";
            var pct = root.TryGetProperty("percentageComplete", out var p) ? p.GetInt32() : 0;
            var status = root.TryGetProperty("statusMessage", out var sm) ? sm.GetString() ?? "" : "";
            var error = root.TryGetProperty("errorMessage", out var e) ? e.GetString() ?? "" : "";

            Console.WriteLine($"Event:       {eventId}");
            Console.WriteLine($"Step:        {step}");
            Console.WriteLine($"Progress:    {pct}%");
            Console.WriteLine($"Status:      {status}");
            if (!string.IsNullOrEmpty(error))
                Console.WriteLine($"Error:       {error}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> HandleCancelAsync(string eventId)
    {
        try
        {
            using var http = CreateClient();
            var resp = await http.PostAsync($"/api/dek-rotation/cancel/{eventId}", null);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {resp.StatusCode} — {body}");
                return 1;
            }

            Console.WriteLine($"Cancelled: {body}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
