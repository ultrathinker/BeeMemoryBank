namespace BeeMemoryBank.BlindIos.Services;

/// <summary>Closed text for a BGTaskScheduler request that iOS refused. An NSError description is arbitrary text and never belongs in the device log.</summary>
public sealed record IosBackgroundRequestError(string? Domain, long Code, string? Description);

public static class IosBackgroundRequestText
{
    private const int MaxDomainLength = 80;

    public static string Describe(IosBackgroundRequestError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return $"iOS refused a background request ({Domain(error.Domain)} {error.Code}).";
    }

    private static string Domain(string? domain) =>
        !string.IsNullOrEmpty(domain) && domain.Length <= MaxDomainLength && domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? domain
            : "unknown domain";
}
