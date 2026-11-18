using System.Text;
using System.Text.Json;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// <c>bmb node rekey [--reason text]</c>: gives this node a new identity key in place (release 2.12, plan WS-D) through the local API
/// (<c>POST /api/node/identity/rekey</c>), as the Node identity page does. The node keeps its id, data and peers; the old key stops working.
/// </summary>
public static class NodeRekeyCommand
{
    public static async Task<int> HandleAsync(string? reason)
    {
        try
        {
            using var http = DekRotateCommand.CreateClient(TimeSpan.FromMinutes(5));
            var content = new StringContent(JsonSerializer.Serialize(new { reason }), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("/api/node/identity/rekey", content);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                // The API answers refusals with a sentence meant to be shown (409); anything else is reported by its status only.
                var sentence = TryError(body);
                Console.WriteLine(sentence is null ? $"Error: the node answered {(int)resp.StatusCode}." : $"Not done: {sentence}");
                return 1;
            }
            using var json = JsonDocument.Parse(body);
            Console.WriteLine($"This node has a new identity key (rotation {json.RootElement.GetProperty("counter").GetInt64()}).");
            Console.WriteLine($"Fingerprint: {json.RootElement.GetProperty("fingerprint").GetString()}");
            Console.WriteLine("Peers switch to it as they apply the rotation; a copy offline across more than 16 re-keys must be paired again.");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"Error: the node could not be reached ({ex.GetType().Name}).");
            return 1;
        }
    }

    private static string? TryError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
