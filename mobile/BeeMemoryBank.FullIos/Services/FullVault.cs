using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>Where the phone's vault stands: none yet, there but locked, or open (the master key in memory).</summary>
public enum VaultState
{
    NoVault,
    Locked,
    Unlocked,
}

/// <summary>
/// The phone's vault: made here (a new memory bank) or joined (a copy of an existing one), opened with the master password and closed again.
/// Thin on purpose - every step is the product's own service (InitializationService through the Android join, SessionService,
/// KeyManagementService); what this adds is the order of the steps and the rules a phone needs around them. The master password is used
/// and forgotten: it is never stored, and the master key exists only in SessionService's memory while the vault is open.
/// </summary>
public sealed class FullVault(IServiceProvider services)
{
    private SessionService Session => services.GetRequiredService<SessionService>();

    public bool IsUnlocked => Session.IsUnlocked;

    /// <summary>Raised when the vault locks, whoever locked it (the lock button, leaving the app, idle time, a reset).</summary>
    public event Action? Locked
    {
        add => Session.Locked += value;
        remove => Session.Locked -= value;
    }

    public async Task<VaultState> GetStateAsync()
    {
        using var scope = services.CreateScope();
        // "Claimed", not "initialized": a half-written database (a restore or a join that stopped) is somebody's vault, never an empty
        // phone to set up again (InitializationService.IsClaimedAsync explains why).
        if (!await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()) return VaultState.NoVault;
        return Session.IsUnlocked ? VaultState.Unlocked : VaultState.Locked;
    }

    /// <summary>
    /// Why <paramref name="password"/> cannot be a new vault's master password, or null when it can: the product's rule for passwords
    /// (UserService.ValidatePassword: 8 characters, upper case, lower case, a digit), and the two entries must match.
    /// </summary>
    public static string? CheckNewPassword(string? password, string? repeated)
    {
        try
        {
            UserService.ValidatePassword(password ?? "");
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
        return password == repeated ? null : "The two passwords are not the same.";
    }

    /// <summary>The name this phone is listed under on the other nodes; iOS gives apps the model ("iPhone"), so the person may change it.</summary>
    public static string NodeName(string? typed, string? deviceName)
    {
        var name = (typed ?? "").Trim();
        if (name.Length == 0) name = (deviceName ?? "").Trim();
        if (name.Length == 0) name = "iPhone";
        return name.Length <= 64 ? name : name[..64];
    }

    /// <summary>
    /// Makes a new memory bank on this phone and opens it: the node's identity, the master key wrapped under the password, the
    /// administrator account (named like the node, as on Android); then a recovery key, returned to be shown once and never kept by the app.
    /// </summary>
    public async Task<string> CreateAsync(string nodeName, string password)
    {
        if (CheckNewPassword(password, password) is { } problem) throw new ArgumentException(problem);
        if (await GetStateAsync() != VaultState.NoVault) throw new InvalidOperationException("This phone already holds a memory bank.");

        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NodeSetupService>().InitAsync(nodeName, password);

        if (!await Session.UnlockAsync(password))
            throw new InvalidOperationException("The new memory bank did not open with its own password.");

        using (var scope = services.CreateScope())
            return await scope.ServiceProvider.GetRequiredService<KeyManagementService>().AddRecoveryKeyAsync();
    }

    /// <summary>
    /// Joins an existing network and opens the copy: <paramref name="codeOrAddress"/> is a join code from a computer's "Connect a device"
    /// (the password then goes only to the computer whose key the code pins) or the https address of a server. A damaged code is refused
    /// here, never read as an address: that would drop the pin.
    /// </summary>
    public async Task JoinAsync(string nodeName, string codeOrAddress, string password)
    {
        var text = (codeOrAddress ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("Paste the join code from Connect a device on your computer, or a server's address.");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Enter the master password of your memory bank.");
        if (await GetStateAsync() != VaultState.NoVault) throw new InvalidOperationException("This phone already holds a memory bank.");

        JoinCode? code = null;
        string address;
        if (JoinCode.LooksLikeJoinCode(text))
        {
            if (!JoinCode.TryParse(text, out code)) throw new ArgumentException(JoinCode.NotValidMessage);
            address = code.Address;
        }
        else
        {
            address = ServerAddress(text) ?? throw new ArgumentException(
                "That is neither a join code (it starts with bmb-join:) nor a server's https address.");
        }

        // A node on the local network: first let iOS ask its local-network question (LocalNetwork), so that the request carrying the
        // password is not the one that fails while the question is on the screen.
        await LocalNetwork.WaitUntilReachableAsync(new Uri(address), TimeSpan.FromSeconds(45));

        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync(nodeName, address, password, code);

        if (!await Session.UnlockAsync(password))
            throw new InvalidOperationException("The joined copy did not open with the password the network accepted.");
    }

    /// <summary>
    /// A server's address typed by hand: https only (the master password is about to be sent), host and optional port, no path. A node
    /// on the local network with its own key is joined with its join code instead, which pins that key.
    /// </summary>
    public static string? ServerAddress(string text)
    {
        var candidate = text.Trim().TrimEnd('/');
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)) return null;
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Opens the vault with the master password; false for a wrong password.</summary>
    public Task<bool> UnlockAsync(string password) => Session.UnlockAsync(password ?? "");

    /// <summary>Closes the vault: the master key is wiped from memory.</summary>
    public void Lock()
    {
        if (Session.IsUnlocked) Session.Lock();
    }

    /// <summary>Issues another recovery key (the vault must be open); the old ones keep working.</summary>
    public async Task<string> NewRecoveryKeyAsync()
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<KeyManagementService>().AddRecoveryKeyAsync();
    }

    /// <summary>The node's own identity (name, id), or null before there is a vault.</summary>
    public async Task<NodeIdentity?> IdentityAsync()
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
    }
}
