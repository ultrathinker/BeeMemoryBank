using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The re-key swap (BeeMemoryBank.Rekey) cannot reference Infrastructure, so it names the Keychain scope file by a literal; this is the one
/// place that sees both and holds the two names together.
/// </summary>
public class ScopeFileCarryOverAgreementTests
{
    [Fact]
    public void TheRekeySwap_CarriesOverTheFileThatTheKeychainStoreScopesBy()
    {
        RekeySwap.CarriedOver.Should().Contain(MacOsKeychainUserSecretStore.ScopeFileName,
            "the re-keyed vault replaces the old data folder: without its scope id the secrets kept in the Keychain would be unreachable");
    }
}
