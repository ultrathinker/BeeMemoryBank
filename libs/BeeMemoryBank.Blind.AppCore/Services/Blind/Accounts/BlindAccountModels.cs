using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>Where an account's files are: in the app's root (the one a 2.6 install had) or in its own folder under <c>accounts/</c>.</summary>
public static class BlindAccountLayouts
{
    /// <summary>The 2.6 layout: the app's root folder IS the account (database, media, keys and state under the names they always had).</summary>
    public const string Root = "root";

    /// <summary><c>accounts/&lt;id&gt;/</c>, the same names inside.</summary>
    public const string Dir = "dir";
}

/// <summary>Where an account is in its life. Only <see cref="Active"/> and <see cref="Deleting"/> are written by the account host's own lifecycle; the others belong to the invitation flow.</summary>
public static class BlindAccountPhases
{
    public const string Active = "active";

    /// <summary>An invitation was accepted on this device and the hub has not answered the redemption yet (it could not be reached, was locked, or asked to wait): the account exists, holds an identity and nothing else.</summary>
    public const string Pending = "pending";

    public const string AwaitingConfirmation = "awaiting-confirmation";
    public const string Refused = "refused";
    public const string Expired = "expired";
    public const string RemovedByOwner = "removed-by-owner";

    /// <summary>Removal was started (keys, state and files are being taken away): a crash resumes it at the next start instead of leaving half an account.</summary>
    public const string Deleting = "deleting";

    public static readonly IReadOnlyList<string> All = [Active, Pending, AwaitingConfirmation, Refused, Expired, RemovedByOwner, Deleting];
}

/// <summary>
/// What one account may use up. Null is "no limit"; ZERO MEANS PAUSED, never "unlimited" (a form that stores 0 for an empty box must not open the tap).
/// The numbers are enforced by the limits slice; here they are only what the person set.
/// </summary>
public sealed record BlindAccountLimits(long? MaxStoredBytes = null, long? MaxTrafficBytesPerDay = null, int? MaxRequestsPerDay = null)
{
    public static readonly BlindAccountLimits None = new();

    /// <summary>True when no number is set.</summary>
    [JsonIgnore]
    public bool IsEmpty => MaxStoredBytes is null && MaxTrafficBytesPerDay is null && MaxRequestsPerDay is null;
}

/// <summary>What the whole device may use up, across every account (null: no limit; zero: paused).</summary>
public sealed record BlindDeviceLimits(long? MaxStoredBytes = null, long? MaxTrafficBytesPerDay = null)
{
    public static readonly BlindDeviceLimits None = new();
}

/// <summary>
/// One account in <c>accounts.json</c>: the local administration of one source mesh's copy and nothing secret (keys are in the platform's
/// store, the copy's own state is in the account's own state store). <see cref="Id"/> is the local name of the account and the name of its
/// folder; it is not the node id the source mesh knows.
/// </summary>
/// <param name="Id"><c>"legacy"</c> for the account a 2.6 install had, otherwise 32 random lower-case hex characters. Nothing else is accepted: the id becomes a folder name.</param>
/// <param name="Layout"><see cref="BlindAccountLayouts"/>.</param>
/// <param name="Name">What the person calls it; the source's name from an invitation, editable. At most <see cref="BlindAccountEntry.MaxNameLength"/> characters, no control characters.</param>
/// <param name="Phase"><see cref="BlindAccountPhases"/>.</param>
/// <param name="Established">Written once the account has completed its identity (it has data and keys that must never be silently replaced). A listed account that is not established has
/// never really started, so a missing folder is simply made; one that is established and has lost its folder is a repair case, never a fresh start over its keys.</param>
/// <param name="MatchCode">The code <c>K7F-2QX</c> the person reads to the owner while the account is <see cref="BlindAccountPhases.AwaitingConfirmation"/>; null otherwise. Not secret: it is on both screens.</param>
public sealed record BlindAccountEntry(
    string Id,
    string Layout,
    string Name,
    string Phase = BlindAccountPhases.Active,
    string? SourceOrigin = null,
    string? InviteId = null,
    DateTimeOffset? AddedAt = null,
    int Order = 0,
    BlindAccountLimits? Limits = null,
    bool UnmeteredOnly = false,
    bool PauseOnPowerSaving = false,
    bool PausedByUser = false,
    bool Established = false,
    string? MatchCode = null)
{
    public const string LegacyId = "legacy";
    public const int MaxNameLength = 64;
    public const int IdLength = 32;

    /// <summary>The limits as the person set them; never null.</summary>
    [JsonIgnore]
    public BlindAccountLimits EffectiveLimits => Limits ?? BlindAccountLimits.None;

    [JsonIgnore]
    public bool IsLegacy => Layout == BlindAccountLayouts.Root;

    /// <summary>A fresh random id: 128 bits, hex. It is also a folder name, so it never holds anything else.</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(IdLength / 2)).ToLowerInvariant();

    /// <summary>True for the one id of the adopted account or for exactly 32 lower-case hex characters.</summary>
    public static bool IsValidId(string? id)
    {
        if (id is null) return false;
        if (id == LegacyId) return true;
        if (id.Length != IdLength) return false;
        foreach (var c in id)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }

    /// <summary>Null when <paramref name="name"/> is a usable account name, otherwise the sentence that says why not.</summary>
    public static string? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "An account needs a name.";
        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength) return $"The name is longer than {MaxNameLength} characters.";
        if (trimmed.Any(char.IsControl)) return "The name may not contain control characters.";
        return null;
    }
}

/// <summary>The content of <c>accounts.json</c>, version 1.</summary>
public sealed record BlindAccountsFile(
    int Version = BlindAccountsFile.CurrentVersion,
    IReadOnlyList<BlindAccountEntry>? Accounts = null,
    BlindDeviceLimits? Device = null)
{
    public const int CurrentVersion = 1;

    /// <summary>A file with no account.</summary>
    public static BlindAccountsFile Empty { get; } = new();

    [JsonIgnore]
    public IReadOnlyList<BlindAccountEntry> Entries => Accounts ?? [];

    [JsonIgnore]
    public BlindDeviceLimits DeviceLimits => Device ?? BlindDeviceLimits.None;

    public BlindAccountEntry? Find(string id) => Entries.FirstOrDefault(a => a.Id == id);
}
