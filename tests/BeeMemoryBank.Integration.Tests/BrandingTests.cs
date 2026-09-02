using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The admin-configurable product name shown in the web header and the tab title.
/// An untouched node must keep reporting the built-in name, and only a superadmin may change it.
/// </summary>
public class BrandingTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.DefaultRequestHeaders.Add("X-User-Id", "1");
        await _factory.InitializeNodeAsync(password: "testPassword123");
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private static async Task<(string Name, bool IsCustom, string DefaultName)> ReadAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("name").GetString()!,
                body.GetProperty("isCustom").GetBoolean(),
                body.GetProperty("defaultName").GetString()!);
    }

    [Fact]
    public async Task FreshNode_ReportsTheBuiltInName()
    {
        var (name, isCustom, defaultName) = await ReadAsync(await _admin.GetAsync("/api/branding"));

        name.Should().Be(Branding.DefaultName);
        isCustom.Should().BeFalse();
        defaultName.Should().Be(Branding.DefaultName);
    }

    [Fact]
    public async Task Superadmin_CanSetAndClearTheName()
    {
        var set = await _admin.PutAsJsonAsync("/api/branding", new { name = "  Acme Knowledge  " });
        var (name, isCustom, _) = await ReadAsync(set);
        name.Should().Be("Acme Knowledge", "surrounding whitespace is trimmed before storing");
        isCustom.Should().BeTrue();

        (await ReadAsync(await _admin.GetAsync("/api/branding"))).Name.Should().Be("Acme Knowledge");

        // Blank means "no override" — back to the built-in name rather than an empty header.
        await _admin.PutAsJsonAsync("/api/branding", new { name = "   " });
        var after = await ReadAsync(await _admin.GetAsync("/api/branding"));
        after.Name.Should().Be(Branding.DefaultName);
        after.IsCustom.Should().BeFalse();
    }

    [Fact]
    public async Task Name_LongerThanTheLimit_IsRejected()
    {
        var resp = await _admin.PutAsJsonAsync("/api/branding",
            new { name = new string('x', Branding.MaxNameLength + 1) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(await _admin.GetAsync("/api/branding"))).Name.Should().Be(Branding.DefaultName);
    }

    [Fact]
    public async Task Name_WithControlCharacters_IsRejected()
    {
        var resp = await _admin.PutAsJsonAsync("/api/branding", new { name = "Acme\nKnowledge" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RegularUser_CannotChangeTheName()
    {
        using var user = _factory.CreateClient();
        user.DefaultRequestHeaders.Remove("X-User-Role");
        user.DefaultRequestHeaders.Add("X-User-Role", UserRoles.User);
        user.DefaultRequestHeaders.Add("X-User-Id", "2");

        var resp = await user.PutAsJsonAsync("/api/branding", new { name = "Hijacked" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadAsync(await _admin.GetAsync("/api/branding"))).Name.Should().Be(Branding.DefaultName);
    }
}
