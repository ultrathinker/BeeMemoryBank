namespace BeeMemoryBank.Core.Services.Acme;

/// <summary>
/// Well-known ACME directory endpoints. Use <see cref="LetsEncryptStagingV2"/> for all
/// development/testing: the production directory has strict rate limits and the staging CA
/// is intentionally untrusted (its roots are not in any browser trust store), which is exactly
/// what you want while iterating on issuance logic.
/// </summary>
public static class AcmeDirectories
{
    /// <summary>
    /// Let's Encrypt ACMEv2 production directory. Real, publicly-trusted certs, but rate-limited.
    /// Only point a real, owned domain at this.
    /// </summary>
    public const string LetsEncryptV2 = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>
    /// Let's Encrypt ACMEv2 staging directory. Generous rate limits, untrusted certs. Use for tests.
    /// </summary>
    public const string LetsEncryptStagingV2 = "https://acme-staging-v02.api.letsencrypt.org/directory";
}

/// <summary>
/// Configuration for <see cref="AcmeCertificateService"/>.
/// </summary>
public sealed class AcmeOptions
{
    /// <summary>ACME directory URL. Defaults to the Let's Encrypt staging environment.</summary>
    public string DirectoryUri { get; set; } = AcmeDirectories.LetsEncryptStagingV2;

    /// <summary>
    /// Contact email registered with the ACME account (e.g. for expiry notifications from the CA).
    /// May be empty; Let's Encrypt accepts accounts without a contact.
    /// </summary>
    public string ContactsEmail { get; set; } = "";

    /// <summary>
    /// A cert is renewed when its remaining validity drops to or below this many days.
    /// Let's Encrypt certs are valid for 90 days; 30 leaves two renewal attempts before expiry.
    /// </summary>
    public int RenewalDaysThreshold { get; set; } = 30;

    /// <summary>
    /// How long to poll an authorization/challenge for a <c>valid</c> result before giving up.
    /// </summary>
    public TimeSpan ChallengeTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Interval between status polls while waiting for the CA to validate a challenge.</summary>
    public TimeSpan ChallengePollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Length of the PFX export password. Randomly generated per stored cert.</summary>
    public int PfxPasswordLength { get; set; } = 32;
}
