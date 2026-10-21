using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>Why an invitation is not taken: the words of the contract a head may switch on (the sentences are in <see cref="BlindInvitationWords"/>), never translated.</summary>
public static class BlindInvitationRefusals
{
    /// <summary>Not an invitation at all (empty, damaged, cut short, a field that is wrong).</summary>
    public const string Malformed = "malformed";

    /// <summary>A pairing code of the old way (<c>bmb-blind-call:</c>, <c>bmb-blind-phone:</c>) pasted where an invitation belongs.</summary>
    public const string OtherCode = "other-code";

    /// <summary>An invitation of a version this app does not know (a newer app made it): taking it would be guessing at where to send a secret.</summary>
    public const string UnknownVersion = "unknown-version";

    /// <summary>An invitation that names a way to trust the hub's TLS endpoint this app does not know.</summary>
    public const string UnknownTrust = "unknown-trust";

    public const string Expired = "expired";

    /// <summary>An account for the same hub (the same node id) is already on this device (added by an invitation, or paired the old way).</summary>
    public const string AlreadyHere = "already-here";

    /// <summary>The device holds as many accounts as it may.</summary>
    public const string TooMany = "too-many";

    /// <summary>The list of accounts is damaged, a repair is waiting, another copy of the app holds the folder, or the app is still starting: nothing is added now.</summary>
    public const string Blocked = "blocked";

    /// <summary>The accounts already here could not be read, so it cannot be said whether one is for the same hub (fail closed).</summary>
    public const string Unreadable = "unreadable";
}

/// <summary>The sentences the invitation flow says to a person. One place, so that a screen, a log line and a test read the same words.</summary>
public static class BlindInvitationWords
{
    public const string Malformed = "This is not an invitation. Copy the whole line that begins with bmb-invite: again.";
    public const string OtherCode = "This is a connection code of the old way, not an invitation. Use \"Add with a pairing code\" for it.";
    public const string UnknownVersion = "This invitation was made by a newer Bee Memory Bank than this app understands. Update this app, then use the invitation again.";
    public const string UnknownTrust = "This invitation asks for a way of checking the computer that this app does not know. Update this app, then use the invitation again.";
    public const string Expired = "This invitation has expired. Ask for a new one.";
    public const string TooMany = "This device holds at most {0} accounts. Remove one first.";
    public const string Unreadable = "The accounts on this device could not be read, so nothing was added. Close the app, open it again and try once more.";

    /// <summary>The sentence for a hub that already has an account here.</summary>
    public static string AlreadyHere(string host) =>
        $"This device already has an account for {host}. Remove it first if you want to start again, or use a different invitation.";

    /// <summary>The 404 of a full node older than 2.7: the exact sentence of the plan. It is not worked around automatically (the old way is the person's choice).</summary>
    public const string OlderHub =
        "The owner's Bee Memory Bank is older than 2.7 and cannot take invitations; ask the owner to update, or pair with a code the old way.";

    public static string NotValid(string source) =>
        $"This invitation is not valid any more: it expired, was already used, or was withdrawn. Ask {source} for a new one.";

    public static string HubMismatch(string host) =>
        $"The computer at {host} did not answer with the identity the invitation names. Nothing was sent. Ask for a new invitation.";

    public static string CertificateRefused(string host) =>
        $"The certificate of {host} is not the one this invitation names. Nothing was sent. Ask for a new invitation.";

    public static string KnownUnderOtherKey(string host) =>
        $"{host} already knows a device with this name under another key. Nothing was changed. Ask for a new invitation.";

    public static string NotUnderstood(string host) =>
        $"{host} did not understand the request of this device. Update this app and try again.";

    public static string KeysMissing =>
        "The keys of this new account could not be read, so nothing was sent. Close the app, open it again and try once more.";

    public static string Connecting(string host) => $"Connecting to {host}...";

    public static string Unreachable(string host) => $"Could not reach {host}. It will try again.";

    public static string Locked =>
        "The owner's Bee Memory Bank is locked right now. It will try again until the invitation runs out.";

    public static string AskedToWait(string host) => $"{host} asked this device to wait. It will try again.";

    /// <summary>
    /// What a card says when the hub did not accept this device's question and its own clock disagrees with this device's by more than a few minutes: not a refusal and not an expiry, and
    /// not final - the person corrects the clock and the question is asked again. <paramref name="hubMinusDevice"/> is positive when this device's clock is behind the hub's.
    /// </summary>
    public static string ClockOff(TimeSpan hubMinusDevice, string host)
    {
        var minutes = (int)Math.Round(Math.Abs(hubMinusDevice.TotalMinutes));
        var direction = hubMinusDevice > TimeSpan.Zero ? "behind" : "ahead of";
        return $"This device's clock is {minutes} minutes off ({direction} {host}'s). Correct the date and time on this device; it checks again in a few minutes.";
    }

    /// <summary>Under a card while the owner has not decided.</summary>
    public static string WaitingFor(string name, string? matchCode) =>
        matchCode is null
            ? $"Waiting for {name} to confirm."
            : $"Waiting for {name} to confirm. Read this code out to them: {matchCode}";

    public static string Refused(string name) => $"{name} refused this device. You can remove this account.";

    public static string ExpiredCard => "The invitation expired. You can remove this account.";

    public static string RemovedByOwner(string name) =>
        $"{name} removed this device from the network. The copy stays until you remove it.";

    /// <summary>What the sync round says when the source answers 401 or 403 to a device that had been synced.</summary>
    public static string SourceRefused(string name) =>
        $"{name} no longer accepts this device. The copy stays until you remove it.";

    public static string ValidFor(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var left = expiresAt - now;
        if (left <= TimeSpan.Zero) return "This invitation has expired.";
        if (left < TimeSpan.FromMinutes(1)) return "This invitation works for less than a minute more.";
        var minutes = (int)Math.Round(left.TotalMinutes);
        return minutes == 1 ? "This invitation works for about a minute more." : $"This invitation works for about {minutes} minutes more.";
    }

    public static string For(string refusal, string? host = null, int? max = null) => refusal switch
    {
        BlindInvitationRefusals.OtherCode => OtherCode,
        BlindInvitationRefusals.UnknownVersion => UnknownVersion,
        BlindInvitationRefusals.UnknownTrust => UnknownTrust,
        BlindInvitationRefusals.Expired => Expired,
        BlindInvitationRefusals.TooMany => string.Format(System.Globalization.CultureInfo.InvariantCulture, TooMany, max ?? 8),
        BlindInvitationRefusals.AlreadyHere => AlreadyHere(host ?? "this computer"),
        BlindInvitationRefusals.Unreadable => Unreadable,
        _ => Malformed,
    };
}

/// <summary>What the person is told when the text pasted is looked at: the kind of refusal for a text that is not taken, from the text alone (no network, no account).</summary>
internal static class BlindInvitationText
{
    private const string Prefix = "bmb-invite:";

    /// <summary>
    /// Why <paramref name="text"/> is not an invitation this app takes, as far as its own words can say: another kind of code, a version or a trust mode it does not know,
    /// or just damaged. Used only to choose the sentence; whether to refuse is <see cref="BlindInvitation.TryParse"/>'s decision.
    /// </summary>
    public static string WhyNot(string text)
    {
        if (text.StartsWith("bmb-blind-call:", StringComparison.Ordinal) || text.StartsWith("bmb-blind-phone:", StringComparison.Ordinal))
            return BlindInvitationRefusals.OtherCode;
        if (!text.StartsWith(Prefix + "?", StringComparison.Ordinal)) return BlindInvitationRefusals.Malformed;

        string? version = null, trust = null;
        foreach (var part in text[(Prefix.Length + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (key == "v") version = value;
            else if (key == "t") trust = value;
        }
        if (version is not null && version != "1") return BlindInvitationRefusals.UnknownVersion;
        if (trust is not null && !BlindTrust.IsKnown(trust)) return BlindInvitationRefusals.UnknownTrust;
        return BlindInvitationRefusals.Malformed;
    }
}
