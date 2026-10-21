using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Api.Services.BlindLimits;

/// <summary>
/// The two limits the owner of a server blind node sets on what it holds and moves for the mesh
/// (release 2.7, the server host's phase 2): <c>BMB_BLIND_MAX_STORED_BYTES</c> and
/// <c>BMB_BLIND_MAX_TRAFFIC_PER_DAY</c>, also editable in the console and stored in
/// <c>blind/settings.json</c> with the node's other local settings. The apps' per-account limits
/// are the same idea for a device (<c>BlindLimitsForm</c> in Blind.AppCore — read, not referenced:
/// the server holds no account and no Vault type).
///
/// <para>Null is no limit of that kind; zero is PAUSED, never "unlimited" (the owner's own words on
/// the apps, kept here so the two surfaces cannot disagree). A daily traffic limit other than zero
/// below <see cref="MinTrafficPerDay"/> is refused: one sync round is already more than that, so
/// nothing would ever be served — it is a typo, not a decision.</para>
/// </summary>
public sealed partial record BlindNodeLimits(long? MaxStoredBytes = null, long? MaxTrafficPerDay = null)
{
    /// <summary>The least a daily traffic limit may be (as on the apps): a round every 15 minutes is a few MB a day.</summary>
    public const long MinTrafficPerDay = 10L << 20;

    public const string StoredEnvName = "BMB_BLIND_MAX_STORED_BYTES";
    public const string TrafficEnvName = "BMB_BLIND_MAX_TRAFFIC_PER_DAY";

    public bool IsEmpty => MaxStoredBytes is null && MaxTrafficPerDay is null;

    /// <summary>
    /// One variable as the file knows it. A set-but-empty variable is "no limit" on purpose (a
    /// compose line left in place to clear a limit), distinct from an unset one, which leaves the
    /// stored value alone: an operator who edits the limit in the console does not want a restart
    /// to undo the edit with a variable nobody set.
    /// </summary>
    public enum Presence
    {
        /// <summary>The variable is not in the environment: nothing to adopt.</summary>
        Unset,

        /// <summary>The variable is set and empty: "no limit" — clears the stored value.</summary>
        Empty,

        /// <summary>The variable carries a value.</summary>
        Set,
    }

    /// <summary>
    /// Reads one variable (from the process environment, or from <paramref name="get"/>: the host's configuration, which holds the
    /// environment and is what a test sets): null when unset, null-but-<see cref="Presence.Empty"/> when set to
    /// nothing, a number when it parses. Anything else is a refusal with the sentence an operator
    /// can act on (the variable's name and what it must be; never the raw value twice over).
    /// </summary>
    public static (long? Bytes, Presence Presence, string? Problem) ReadVariable(string name, Func<string, string?>? get = null)
    {
        var raw = (get ?? Environment.GetEnvironmentVariable)(name);
        if (raw is null) return (null, Presence.Unset, null);
        raw = raw.Trim();
        if (raw.Length == 0) return (null, Presence.Empty, null);
        if (!long.TryParse(raw, out var bytes) || bytes < 0)
            return (null, Presence.Set,
                $"{name} must be a number of bytes (0 pauses, empty or unset means no limit)");
        if (name == TrafficEnvName && TrafficProblem(bytes) is not null)
            return (null, Presence.Set,
                $"{TrafficEnvName} is below {MinTrafficPerDay / (1024 * 1024)} MB a day: a sync round would not fit. Use 0 to pause, or at least {MinTrafficPerDay / (1024 * 1024)} MB");
        return (bytes, Presence.Set, null);
    }

    /// <summary>
    /// Both variables, checked together: the limits they name, or the first problem. A node that
    /// cannot trust its own configuration refuses to start rather than serving without the caps
    /// its owner believes are in force.
    /// </summary>
    public static (BlindNodeLimits? Limits, string? Problem) FromEnvironment(Func<string, string?>? get = null)
    {
        var stored = ReadVariable(StoredEnvName, get);
        if (stored.Problem is { } p) return (null, p);
        var traffic = ReadVariable(TrafficEnvName, get);
        if (traffic.Problem is { } q) return (null, q);
        if (stored.Presence == Presence.Unset && traffic.Presence == Presence.Unset) return (null, null);
        return (new BlindNodeLimits(stored.Bytes, traffic.Bytes), null);
    }

    /// <summary>
    /// The one check a number can fail besides being negative: a daily traffic limit other than zero
    /// below <see cref="MinTrafficPerDay"/> would not let one sync round through. Null when acceptable.
    /// </summary>
    public static string? TrafficProblem(long? traffic) =>
        traffic is > 0 and < MinTrafficPerDay
            ? $"a daily traffic limit below {MinTrafficPerDay / (1024 * 1024)} MB would not let one sync round through: use 0 to pause, or at least {MinTrafficPerDay / (1024 * 1024)} MB"
            : null;

    /// <summary>The largest size the console accepts: a petabyte, far from an overflow of the multiplication.</summary>
    private const long MaxParsed = 1L << 50;

    [GeneratedRegex(@"^\s*(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[KMGT]i?B|B)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    /// <summary>
    /// One limit as the settings PUT carries it: absent or JSON null keeps the stored value, an empty
    /// string clears it, a number is bytes, a string is bytes or a size the way a person writes it
    /// ("500 MB", "2 GB", "1.5 TB": KB, MB, GB and TB are 1024-based, as on the apps). <c>Sent</c>
    /// says whether the limit is to change; <c>Problem</c> is the refusal sentence.
    /// </summary>
    public static (bool Sent, long? Value, string? Problem) ParseWire(JsonElement? sent)
    {
        const string nonsense = "a limit is a number of bytes or a size like 500 MB or 2 GB: 0 pauses, empty clears it";
        if (sent is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return (false, null, null);
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt64(out var number) && number >= 0 ? (true, number, null) : (true, null, nonsense);
        if (element.ValueKind != JsonValueKind.String) return (true, null, nonsense);

        var text = element.GetString() ?? "";
        if (text.Trim().Length == 0) return (true, null, null);
        var match = SizePattern().Match(text);
        if (!match.Success
            || !decimal.TryParse(match.Groups["n"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size))
            return (true, null, nonsense);
        var unit = match.Groups["u"].Value.ToUpperInvariant().Replace("I", "");
        var factor = unit switch { "KB" => 1L << 10, "MB" => 1L << 20, "GB" => 1L << 30, "TB" => 1L << 40, _ => 1L };
        if (unit.Length == 0 && size != Math.Truncate(size)) return (true, null, nonsense);
        if (size > (decimal)MaxParsed / factor) return (true, null, "that is more than a node can hold: write a smaller size");
        return (true, (long)Math.Round(size * factor, MidpointRounding.AwayFromZero), null);
    }
}
