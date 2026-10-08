using System.Globalization;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>The last background round iOS granted, as the state store keeps it ("time|task|result") and as the screen says it.</summary>
public static class IosLastRound
{
    public static string Format(DateTimeOffset at, string task, string result) =>
        $"{at.ToString("O", CultureInfo.InvariantCulture)}|{task}|{result.ReplaceLineEndings(" ")}";

    /// <summary>The screen line; "none yet" for nothing or for a value that is not one of ours.</summary>
    public static string Describe(string? stored)
    {
        var parts = stored?.Split('|', 3);
        if (parts is not { Length: 3 } ||
            !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            return "Last background round: none yet. iOS decides when the copy may run in the background.";
        return $"Last background round: {at.ToLocalTime():dd.MM.yyyy HH:mm} ({parts[1]}): {parts[2]}";
    }
}
