using System.Collections.Concurrent;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The model prompts embedded in the API assembly (<c>Dream/Prompts/*.md</c>, plan 3.1). The file name without its
/// extension is the prompt version (<see cref="DreamNames.PairPromptVersion"/> and its siblings), so a changed prompt
/// is a new file and a new version, and the examined registry sees the change. The same files are what a command-line
/// agent is handed in the model comparison without a key (plan 7), so each one is the whole instruction.
/// </summary>
public static class DreamPromptResources
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>The text of prompt <paramref name="version"/> ("pair-1"), line ends as "\n".</summary>
    public static string Load(string version) => Cache.GetOrAdd(version, static v =>
    {
        var name = DreamNames.PromptResourceName(v);
        using var stream = typeof(DreamPromptResources).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The Dream prompt '{v}' is not embedded in the assembly.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n").Trim();
    });
}
