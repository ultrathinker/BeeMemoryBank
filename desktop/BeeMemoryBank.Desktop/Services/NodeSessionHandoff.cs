using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Asks the running node to hand its open vault over to the process that starts after an app
/// update (<c>POST /node/update/unlock-handoff</c>, see the API's UpdateUnlockHandoff), so the user
/// is not asked for the password again. Best effort: any failure only means one extra login.
/// </summary>
public static class NodeSessionHandoff
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<bool> RequestAsync(string? frontUrl)
    {
        // Only a node this app hosted has a key in our environment (NodeLifecycleService sets it).
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (string.IsNullOrEmpty(frontUrl) || string.IsNullOrEmpty(key)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{frontUrl.TrimEnd('/')}/node/update/unlock-handoff");
            request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
            request.Headers.TryAddWithoutValidation("X-User-Role", "superadmin");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Session handoff before update failed: {ex.Message}");
            return false;
        }
    }
}
