using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The text form of the Dream enumerations, as the API, the chat.db columns and the query strings carry it: the
/// <see cref="JsonStringEnumMemberNameAttribute"/> of each value (<c>in_chat</c>, <c>duplicate_exact</c>). Minimal
/// API binds an enum from a query string with Enum.TryParse, which does not know these names, so endpoints take the
/// raw string and parse it here.
/// </summary>
public static class DreamWire
{
    private static readonly ConcurrentDictionary<Type, (Dictionary<object, string> ToText, Dictionary<string, object> FromText)> Maps = new();

    private static (Dictionary<object, string> ToText, Dictionary<string, object> FromText) MapOf(Type type) =>
        Maps.GetOrAdd(type, static t =>
        {
            var to = new Dictionary<object, string>();
            var from = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var value = field.GetValue(null)!;
                var name = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                    ?? throw new InvalidOperationException($"{t.Name}.{field.Name} has no wire name.");
                to.Add(value, name);
                from.Add(name, value);
            }
            return (to, from);
        });

    /// <summary>The wire name of <paramref name="value"/>, e.g. <c>in_chat</c>.</summary>
    public static string ToWire<T>(T value) where T : struct, Enum =>
        MapOf(typeof(T)).ToText.TryGetValue(value, out var text)
            ? text
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Not a defined {typeof(T).Name}.");

    /// <summary>Parses a wire name (exact, case-sensitive). Null, empty or unknown text gives false.</summary>
    public static bool TryParse<T>([NotNullWhen(true)] string? text, out T value) where T : struct, Enum
    {
        if (text != null && MapOf(typeof(T)).FromText.TryGetValue(text, out var boxed))
        {
            value = (T)boxed;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Parses a wire name read from the store; an unknown value there is a bug or a newer writer, not input.</summary>
    public static T Parse<T>(string text) where T : struct, Enum =>
        TryParse<T>(text, out var value) ? value : throw new FormatException($"Unknown {typeof(T).Name} '{text}'.");

    /// <summary>Every wire name of <typeparamref name="T"/>, in declaration order.</summary>
    public static IReadOnlyList<string> NamesOf<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(ToWire).ToList();
}

/// <summary>
/// The serializer of the JSON Dream keeps under the chat key (advice, rule and event payloads) and of the Dream
/// endpoints' own reads: camelCase, enums by wire name, nulls written (the payload shape of plan 3.4 lists them).
/// </summary>
public static class DreamJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static byte[] SerializeToUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);
}

/// <summary>Names fixed by the plan (3.1, 3.7) that more than one package uses.</summary>
public static class DreamNames
{
    /// <summary>The LongOperationGate key of a running Dream run (visible in GET /api/admin/operations).</summary>
    public const string LongOperationKey = "dream-run";

    /// <summary>The author mark of a change Apply makes, set the way the chat sets its "chat" agent name.</summary>
    public const string AgentName = "dream";

    public const string PairVerdictTask = "pair_verdict";
    public const string FolderReviewTask = "folder_review";
    public const string TreeReviewTask = "tree_review";

    /// <summary>Prompt versions: the embedded resource file name under Dream/Prompts without its extension.</summary>
    public const string PairPromptVersion = "pair-1";
    public const string FolderPromptVersion = "folder-1";
    public const string TreePromptVersion = "tree-1";

    /// <summary>The manifest resource name of a prompt version's file (Dream/Prompts/{version}.md, embedded by the project file).</summary>
    public static string PromptResourceName(string promptVersion) => $"BeeMemoryBank.Api.Dream.Prompts.{promptVersion}.md";

    /// <summary>The text of a prompt version, or null when the assembly has no such file.</summary>
    public static string? LoadPrompt(string promptVersion)
    {
        using var stream = typeof(DreamNames).Assembly.GetManifestResourceStream(PromptResourceName(promptVersion));
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The <c>familyKey</c> of an advice about a pair of articles: <c>pair|minId|maxId</c> (ordinal order of the
    /// "D" forms), shared by the rule's near_duplicate and the model's verdict on the same pair.</summary>
    public static string PairFamilyKey(Guid a, Guid b)
    {
        var x = a.ToString("D");
        var y = b.ToString("D");
        return string.CompareOrdinal(x, y) <= 0 ? $"pair|{x}|{y}" : $"pair|{y}|{x}";
    }
}

/// <summary>Error codes of <c>ErrorResponse.Code</c> and of dream_run.error_code (plan 3.3, 3.8).</summary>
public static class DreamErrorCodes
{
    public const string AlreadyRunning = "already_running";
    public const string Locked = "locked";
    public const string Expired = "expired";
    public const string NotApplicable = "not_applicable";
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    /// <summary>Apply: a service refused the change for the caller's access (403).</summary>
    public const string Forbidden = "forbidden";
    /// <summary>Apply: a service refused the change for the state of the vault (409); the advice is new again with lastError.</summary>
    public const string Refused = "refused";
    /// <summary>The Dream page is not served on this node (a blind node, or the feature cannot run here).</summary>
    public const string Unavailable = "unavailable";

    // Why a scheduled run was skipped (outcome skipped).
    public const string Busy = "busy";
    public const string OpenListFull = "open_list_full";
    public const string NoEmbeddings = "no_embeddings";

    // Why the model of the status is not ready (DreamStatusResponse.ModelProblem).
    public const string NoModel = "no_model";
    public const string NoKey = "no_key";
}

/// <summary>Fixed texts of dream_advice.status_note.</summary>
public static class DreamStatusNotes
{
    /// <summary>A model verdict on the same pair replaced an open rule advice (same familyKey).</summary>
    public const string Superseded = "superseded";

    /// <summary>An in_chat advice whose article_unchanged stopped holding: counted as done after the chat.</summary>
    public const string ChangedAfterChat = "changed_after_chat";

    /// <summary>An applied advice a person marked done by hand (Mark done), not one Apply carried out.</summary>
    public const string MarkedDone = "marked_done";
}

/// <summary>
/// The AAD of each sealed Dream column, bound to the row id. libs/BeeMemoryBank.Rekey/Steps/ChatColumns.cs spells the
/// same strings (the re-key library does not reference the API); DreamContractsTests keeps the two equal.
/// </summary>
public static class DreamAad
{
    public const string AdvicePrefix = "bmb-dream-advice-v1:";
    public const string RulePrefix = "bmb-dream-rule-v1:";
    public const string EventPrefix = "bmb-dream-event-v1:";

    public static byte[] Advice(Guid id) => Encoding.UTF8.GetBytes($"{AdvicePrefix}{id:D}");
    public static byte[] Rule(Guid id) => Encoding.UTF8.GetBytes($"{RulePrefix}{id:D}");
    public static byte[] Event(Guid id) => Encoding.UTF8.GetBytes($"{EventPrefix}{id:D}");
}
