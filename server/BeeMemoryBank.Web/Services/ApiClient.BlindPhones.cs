using System.Text.Json;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// A node an Android blind node can call: a blind node, or a hub — a full node that was set up for it, with a pinned certificate
/// (<c>pin</c>) or a normal one (<c>public-ca</c>), ADR 0007.
/// </summary>
public sealed record BlindPhoneListenerDto(Guid NodeId, string DisplayName, string Address, bool IsBlind, string Trust = "pin")
{
    /// <summary>The mode in the words of the screens.</summary>
    public string TrustLabel => Trust == "public-ca" ? "normal certificate" : "pinned certificate";
}

/// <summary>The paired phone and the "where to call" code to show it.</summary>
public sealed record BlindPhonePairedDto(Guid PhoneId, string DisplayName, string CallCode, Guid ListenerId);

public partial class ApiClient
{
    public async Task<List<BlindPhoneListenerDto>?> ListBlindPhoneListenersAsync()
    {
        try
        {
            var resp = await http.GetAsync("/api/blind-nodes/android/listeners");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<BlindPhoneListenerDto>>(JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Pairs an Android blind node from its code; the error lists every pre-flight problem.</summary>
    public async Task<(BlindPhonePairedDto? Paired, string? Error)> PairBlindPhoneAsync(string code, Guid listenerId)
    {
        var resp = await http.PostAsJsonAsync("/api/blind-nodes/android/", new { code, listenerId }, JsonOpts);
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<BlindPhonePairedDto>(JsonOpts), null);
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (doc.RootElement.TryGetProperty("problems", out var problems))
                error = string.Join(" ", problems.EnumerateArray().Select(p => p.GetString()));
            return (null, error ?? "Could not pair the phone.");
        }
        catch (JsonException)
        {
            return (null, "Could not pair the phone.");
        }
    }
}
