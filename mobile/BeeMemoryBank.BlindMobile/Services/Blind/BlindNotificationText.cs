namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The words of the blind app's one ongoing notification. Two versions exist on purpose. The FULL one names the account whose job is running ("Alpha: First load: 45 %"): with several copies on the phone that is what
/// tells the person whose job it is. The name of a copy is the name of its source (a person's or a computer's), which does not belong on a lock screen, so the notification is marked private and carries a PUBLIC
/// version that the system shows instead while the phone is locked and the person has chosen to hide sensitive content: the app and "Blind copies", no account, no title, no figure. When no account has been
/// selected (the service does not know it yet) the full notification says the neutral words too, never a name that is not that job's.
/// </summary>
public static class BlindNotificationText
{
    public const string AppName = "BeeMemoryBank blind copy";

    /// <summary>The neutral status: no account is named.</summary>
    public const string Neutral = "Blind copies";

    /// <summary>What the lock screen shows (the public version of the notification).</summary>
    public const string Public = Neutral;

    private const int MaxName = 60;

    /// <summary>The text of the full notification: <c>name: title: 45 %</c>, or the neutral words when no account is selected.</summary>
    public static string Body(string? account, string title, double progress)
    {
        var name = NameOf(account);
        if (name.Length == 0) return Neutral;
        return progress > 0 ? $"{name}: {title}: {progress:P0}" : $"{name}: {title}";
    }

    /// <summary>The account's name as one short line (a name with a line break or a very long one must not take the notification over).</summary>
    public static string NameOf(string? account)
    {
        var name = (account ?? "").ReplaceLineEndings(" ").Trim();
        return name.Length <= MaxName ? name : name[..MaxName] + "...";
    }
}
