using System.Globalization;
using System.Text.Json;
using BeeMemoryBank.Api.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The six dream_* tables of chat.db (plan 3.2): settings, runs, advice, rules, journal, examined registry. One partial
/// file per table. Sealed columns go under the chat key (ChatDataProtector) with the AADs of DreamAad; a row the
/// current chat key cannot open is deleted by the sweep (DreamStore.Sweep.cs).
///
/// <para>Ids are written as lowercase "D" text, always through <see cref="Id"/>: the offline re-key builds the AAD from
/// the id column as it reads it, so another spelling would make a row that no longer opens after a re-key.</para>
/// </summary>
public sealed partial class DreamStore(ChatDbConnectionFactory factory, ChatDataProtector protector)
    : ChatRepositoryBase(factory)
{
    private static string Id(Guid id) => id.ToString("D");

    private static string? Id(Guid? id) => id?.ToString("D");

    private static string Time(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static string? Time(DateTime? utc) => utc is { } value ? Time(value) : null;

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTime? ParseTimeOrNull(string? text) => text is null ? null : ParseTime(text);

    /// <summary>Seals a payload under the chat key; the caller holds the lease only for this write.</summary>
    private async Task<(byte[] Cipher, byte[] Iv)> SealAsync<T>(T payload, byte[] aad)
    {
        using var lease = await protector.AcquireAsync();
        return lease.EncryptText(DreamJson.Serialize(payload), aad);
    }

    /// <summary>
    /// Opens a payload, or null when it does not open or does not parse: the row is unreadable, and the sweep removes it.
    /// A locked vault throws from the key lookup instead, so that "locked" is never mistaken for "unreadable".
    /// </summary>
    private T? Open<T>(ChatKeyLease? lease, byte[] cipher, byte[] iv, long? keyVersion, byte[] aad) where T : class
    {
        var json = protector.TryDecryptText(lease, cipher, iv, (int?)keyVersion, aad);
        if (json is null) return null;
        try
        {
            return DreamJson.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
