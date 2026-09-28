using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-merge #4: /api/blind/status carries pairing.code_active (CONTRACTS §5) — true while a pair
/// code someone could still use exists, false before one is issued and once it is spent or expired.
/// </summary>
public class BlindPairingStatusTests : IDisposable
{
    private readonly BlindNodeFactory _blind = new();

    public void Dispose() => _blind.Dispose();

    [Fact]
    public async Task Status_SaysWhetherAPairCodeIsActive()
    {
        using var console = _blind.CreateClient();

        (await CodeActiveAsync(console)).Should().BeFalse("no code was issued yet");

        (await console.GetAsync("/api/blind/pair-code")).EnsureSuccessStatusCode();
        (await CodeActiveAsync(console)).Should().BeTrue();

        await _blind.Services.GetRequiredService<BlindState>()
            .SetPairingSecretAsync("expired", DateTime.UtcNow.AddMinutes(-1));
        (await CodeActiveAsync(console)).Should().BeFalse("an expired code is no code");
    }

    private static async Task<bool> CodeActiveAsync(HttpClient console)
    {
        var json = await console.GetFromJsonAsync<JsonElement>("/api/blind/status");
        json.TryGetProperty("pairing", out var pairing).Should().BeTrue("the status names the pairing state");
        return pairing.GetProperty("code_active").GetBoolean();
    }
}
