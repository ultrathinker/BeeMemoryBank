using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// The one point every path that writes a master-password key slot calls (BMB-43, plan 6.3): the new
/// slot is published, unchanged, as this node's "device box", so a blind node holds a way back into
/// the vault under the password this device actually uses. Passwords are per device (tbl_key_slot is
/// not replicated), which is why every device publishes its own.
///
/// <para>Called AFTER the slot is stored. Never throws: the password change has already happened, and
/// failing to announce it must not turn into a failed password change. The next slot change, or the
/// strong box a PC builds at login, covers a missed publication.</para>
/// </summary>
public interface IRecoveryBoxPublisher
{
    /// <param name="slot">The slot as stored (salt, parameters, wrapped DEK, IV).</param>
    /// <param name="dek">The master DEK inside it — only its fingerprint leaves this call.</param>
    Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek);
}

/// <summary>Hosts without sync (tests, tools) have nobody to publish to.</summary>
public sealed class NullRecoveryBoxPublisher : IRecoveryBoxPublisher
{
    public Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek) => Task.CompletedTask;
}
