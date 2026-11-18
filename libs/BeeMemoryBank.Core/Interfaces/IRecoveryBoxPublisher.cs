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

    /// <summary>
    /// As <see cref="PublishDeviceBoxAsync(MasterKeyStore, byte[])"/>, with the KEK of the slot's password in hand (2.12): a sealed (v2) door
    /// is published as a v2 box only once every active peer opens one (capability <c>door-box-v1</c>); until then, a caller that holds the
    /// password publishes the old shape instead (the DEK wrapped under that KEK, beside the same salt and parameters).
    /// </summary>
    Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek, byte[]? kek) => PublishDeviceBoxAsync(slot, dek);

    /// <summary>
    /// After a rotation or the delete of a door (2.12, plan WS-A4): publishes this node's device box again from the door it should copy now
    /// (the door of its latest device box, else the sealed door of the active superadmin who signed in last), sealed to <paramref name="dek"/>.
    /// Nothing when a peer cannot open a v2 box or there is no such door: the old box stays and the bridge covers it.
    /// </summary>
    Task RepublishForDoorsAsync(byte[] dek) => Task.CompletedTask;
}

/// <summary>Hosts without sync (tests, tools) have nobody to publish to.</summary>
public sealed class NullRecoveryBoxPublisher : IRecoveryBoxPublisher
{
    public Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek) => Task.CompletedTask;
}
