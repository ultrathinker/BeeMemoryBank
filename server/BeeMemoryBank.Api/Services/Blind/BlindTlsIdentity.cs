using System.Security.Cryptography.X509Certificates;

namespace BeeMemoryBank.Api.Services;

/// <summary>The blind node's loaded HTTPS certificate and the pin that goes into its pair code.</summary>
public sealed class BlindTlsIdentity(X509Certificate2 certificate)
{
    public X509Certificate2 Certificate { get; } = certificate;

    public string Spki { get; } = Sync.Blind.Spki.Of(certificate);
}
