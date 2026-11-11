using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>One model a preset knows, so the admin can tick it instead of typing its id.</summary>
public sealed record PresetModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("contextWindow")] int? ContextWindow,
    [property: JsonPropertyName("vision")] bool Vision,
    [property: JsonPropertyName("tools")] bool Tools,
    [property: JsonPropertyName("reasoning")] bool Reasoning);

/// <summary>A provider the admin can pick from the list: its address and protocol, the wire options it needs, a note to show beside it, a
/// few known models. Picking one creates an ordinary provider (not approved until a superadmin approves its host).</summary>
public sealed record ProviderPreset(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("local")] bool Local,
    [property: JsonPropertyName("docs")] string? Docs,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("options")] ProviderWireOptions Options,
    [property: JsonPropertyName("models")] IReadOnlyList<PresetModel> Models);

/// <summary>
/// The built-in list of providers, shipped inside the product as an embedded resource (<c>provider-presets.json</c>). It is never
/// downloaded by the product: <c>tools/provider-presets/generate_presets.py</c> turns a downloaded copy of the open models.dev catalogue
/// (MIT, see THIRD-PARTY-NOTICES.txt) into this file at development time. An entry whose protocol has no client on this node is left out of
/// what the API offers.
/// </summary>
public static class ProviderPresets
{
    public const string ResourceName = "BeeMemoryBank.Api.Services.Providers.provider-presets.json";

    private sealed record PresetFile(
        [property: JsonPropertyName("format")] int Format,
        [property: JsonPropertyName("presets")] List<ProviderPreset> Presets);

    private static readonly Lazy<IReadOnlyList<ProviderPreset>> Loaded = new(Load);

    public static IReadOnlyList<ProviderPreset> All => Loaded.Value;

    public static ProviderPreset? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>Parses a preset file; entries that are not usable (no id, an unknown protocol, an address the rules refuse, invalid options)
    /// are dropped rather than failing the whole list.</summary>
    public static IReadOnlyList<ProviderPreset> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<PresetFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("The provider preset file is empty.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ProviderPreset>();
        foreach (var p in file.Presets ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name) || !seen.Add(p.Id)) continue;
            if (!ProviderProtocols.IsKnown(p.Protocol)) continue;
            var options = p.Options ?? ProviderWireOptions.Default;
            if (options.Validate(p.Protocol) is not null) continue;
            var (url, _) = ProviderUrl.Normalize(p.BaseUrl, p.Local);
            if (url is null) continue;
            var models = (p.Models ?? [])
                .Where(m => !string.IsNullOrWhiteSpace(m.Id) && !m.Id.Any(char.IsWhiteSpace) && m.ContextWindow is null or > 0)
                .GroupBy(m => m.Id, StringComparer.Ordinal).Select(g => g.First())
                .ToList();
            list.Add(p with { BaseUrl = url, Options = options, Models = models });
        }
        return list;
    }

    private static IReadOnlyList<ProviderPreset> Load()
    {
        using var stream = typeof(ProviderPresets).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
