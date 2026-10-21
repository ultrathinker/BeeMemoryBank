namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The names under which one account keeps its things in each platform's store: the NAMING CONTRACT every head implements. Pure strings and
/// paths, so that the rule is one piece of tested code and a head only asks it ("what is the Keystore alias of the backup key of this account?")
/// instead of building a name by hand.
///
/// <para>The legacy account (the 2.6 install, id <c>"legacy"</c>) keeps today's names exactly: nothing is renamed, copied or re-keyed on upgrade,
/// and a 2.6 app that sees the root again finds everything where it left it. Every other account's names carry its id, in a place where the
/// platform BINDS the name to the stored secret (the AES-GCM associated data on Android, the entropy of DPAPI, the account of a Keychain item),
/// so a blob copied from one account's slot to another's does not open.</para>
/// </summary>
public static class BlindAccountNames
{
    /// <summary>The three secrets of an account, and the names the 2.6 hosts gave them.</summary>
    public enum Role { IdentitySeed, PairingSecret, BackupKey }

    // ---- Android: Keystore alias, blob file, AAD -------------------------------------------------------------------------------------------

    /// <summary>The Keystore alias of the key that wraps the role's blob: <c>bmb_blind_seed_v1</c> for the legacy account, <c>bmb_blind_seed_v1.&lt;id&gt;</c> otherwise.</summary>
    public static string AndroidAlias(BlindAccountEntry account, Role role) => WithIdSuffix(account, LegacyAndroidAlias(role), ".");

    /// <summary>The file (relative to the account's folder) that holds the wrapped blob: <c>bmb_blind_seed.bin</c> in both layouts (the folder differs).</summary>
    public static string AndroidBlobFile(Role role) => role switch
    {
        Role.IdentitySeed => "bmb_blind_seed.bin",
        Role.PairingSecret => "bmb_blind_pairing.bin",
        _ => "bmb_blind_backup.bin",
    };

    /// <summary>The associated data of the AES-GCM wrap: the 2.6 string for the legacy account, that string + "/" + id otherwise.</summary>
    public static byte[] AndroidAad(BlindAccountEntry account, Role role) =>
        System.Text.Encoding.UTF8.GetBytes(WithIdSuffix(account, LegacyAndroidAad(role), "/"));

    /// <summary>The SharedPreferences file of the account's state: <c>bmb_blind_state</c> for the legacy account, <c>bmb_blind_state.&lt;id&gt;</c> otherwise.</summary>
    public static string AndroidPreferencesName(BlindAccountEntry account) => WithIdSuffix(account, "bmb_blind_state", ".");

    // ---- Windows: DPAPI file and entropy --------------------------------------------------------------------------------------------------

    /// <summary>The role's file name inside the account's <c>secrets</c> folder (both layouts).</summary>
    public static string WindowsSecretFile(Role role) => LegacyWindowsRole(role) + ".dpapi";

    /// <summary>The DPAPI entropy: <c>BeeMemoryBankBlind/v1/&lt;role&gt;</c> for the legacy account, <c>BeeMemoryBankBlind/v1/&lt;id&gt;/&lt;role&gt;</c> otherwise.</summary>
    public static byte[] WindowsEntropy(BlindAccountEntry account, Role role) =>
        System.Text.Encoding.UTF8.GetBytes(account.IsLegacy
            ? "BeeMemoryBankBlind/v1/" + LegacyWindowsRole(role)
            : "BeeMemoryBankBlind/v1/" + account.Id + "/" + LegacyWindowsRole(role));

    // ---- iOS and macOS: Keychain service and account ---------------------------------------------------------------------------------------

    /// <summary>The Keychain service (the same for every account: the account name tells them apart).</summary>
    public const string KeychainService = "com.beememorybank.blind";

    /// <summary>The Keychain account of an item: <c>identity-seed-v1</c> for the legacy account, <c>&lt;id&gt;/identity-seed-v1</c> otherwise.</summary>
    public static string KeychainAccount(BlindAccountEntry account, Role role) =>
        account.IsLegacy ? LegacyKeychainAccount(role) : account.Id + "/" + LegacyKeychainAccount(role);

    // ---- State file (desktop, iOS, macOS) and the account's folder -------------------------------------------------------------------------

    /// <summary>The state file inside the account's folder (not secret): <c>state.json</c> in both layouts.</summary>
    public const string StateFile = "state.json";

    /// <summary>The account's data folder under <paramref name="root"/>: the root itself for the legacy account, <c>accounts/&lt;id&gt;</c> otherwise.</summary>
    public static string DataDirectory(string root, BlindAccountEntry account)
    {
        if (account.IsLegacy) return root;
        RequireValidDirId(account);
        return Path.Combine(root, BlindAccountRegistry.AccountsFolder, account.Id);
    }

    /// <summary>The account's database file (<c>beememorybank.db</c> in the folder of a new account; the host's own path for the legacy one, given as <paramref name="legacyDatabasePath"/>).</summary>
    public static string DatabasePath(string root, BlindAccountEntry account, string legacyDatabasePath) =>
        account.IsLegacy ? legacyDatabasePath : Path.Combine(DataDirectory(root, account), "beememorybank.db");

    // ----------------------------------------------------------------------------------------------------------------------------------------

    private static string WithIdSuffix(BlindAccountEntry account, string legacyName, string separator) =>
        account.IsLegacy ? legacyName : legacyName + separator + RequireValidDirId(account);

    private static string RequireValidDirId(BlindAccountEntry account)
    {
        // The id ends up in a file name, an alias and an AAD string: only what this app made (32 hex characters) is ever used.
        if (account.Id == BlindAccountEntry.LegacyId || !BlindAccountEntry.IsValidId(account.Id) || account.Layout != BlindAccountLayouts.Dir)
            throw new ArgumentException("This account has no usable id.", nameof(account));
        return account.Id;
    }

    private static string LegacyAndroidAlias(Role role) => role switch
    {
        Role.IdentitySeed => "bmb_blind_seed_v1",
        Role.PairingSecret => "bmb_blind_pairing_v1",
        _ => "bmb_blind_backup_v1",
    };

    private static string LegacyAndroidAad(Role role) => role switch
    {
        Role.IdentitySeed => "bmb-blind-identity-seed-v1",
        Role.PairingSecret => "bmb-blind-pairing-v1",
        _ => "bmb-blind-backup-key-v1",
    };

    private static string LegacyWindowsRole(Role role) => role switch
    {
        Role.IdentitySeed => "identity-seed",
        Role.PairingSecret => "pairing-secret",
        _ => "backup-key",
    };

    private static string LegacyKeychainAccount(Role role) => role switch
    {
        Role.IdentitySeed => "identity-seed-v1",
        Role.PairingSecret => "pairing-secret-v1",
        _ => "backup-key-v1",
    };
}
