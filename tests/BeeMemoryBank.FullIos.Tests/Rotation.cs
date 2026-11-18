using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.DekRotation;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// A key rotation as a phone applies one another node started (the peer side of the quiet rotation): the real <see cref="DekRewrapper"/>
/// over the phone's own database and session, with the phone's boot-store mirror when it is encrypted. Every v2 door is re-sealed to the new key.
/// </summary>
internal static class Rotation
{
    public static async Task AsPeerAsync(IServiceProvider services, byte[] newDek)
    {
        var session = services.GetRequiredService<SessionService>();
        var factory = services.GetRequiredService<DbConnectionFactory>();
        var commitId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("O");
        using (var c = factory.CreateConnection())
            await c.ExecuteAsync(
                "INSERT INTO tbl_dek_rotation_state (event_id, state, rotation_ts, created_at, updated_at) VALUES (@commitId, 'COMMITTING', @now, @now, @now)",
                new { commitId, now });

        var oldDek = session.GetMasterDek();
        try
        {
            var (chain, chainIv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
            await DekRewrapper.RewrapAllAsync(factory, session, oldDek, (byte[])newDek.Clone(), 2, commitId, isInitiator: false,
                chainEncryptedNewDekB64: Convert.ToBase64String(chain), chainIvB64: Convert.ToBase64String(chainIv),
                sealedHooks: new SealedRotationHooks(services.GetService<IUnlockMaterialMirror>(), null));
        }
        finally
        {
            Array.Clear(oldDek);
        }
    }
}
