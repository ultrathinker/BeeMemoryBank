using System.Security.Cryptography;
using BeeMemoryBank.BlindMobile.Services.Blind;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.TestSupport;

/// <summary>
/// The contract of a per-account secret store (<see cref="IBlindSecretStoreFactory"/>), as a test base that EVERY head runs over its own implementation (Android Keystore blobs,
/// Windows DPAPI files, iOS and macOS Keychain items) the way <c>InMemoryBlindSecretStoreContractTests</c> runs it over the reference: derive from it, say how to make a factory over
/// storage that survives a "restart", and how to copy one account's persisted blob into another account's slot. The names an implementation must use are
/// <see cref="BlindAccountNames"/>; what the contract proves is that they do their job: two accounts never see each other's secrets, clearing one leaves the other, what was saved is
/// there after a restart, and a blob that is moved into another account's slot does not open there.
/// </summary>
public abstract class BlindSecretStoreContract
{
    protected static readonly BlindAccountEntry Legacy = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
    protected static readonly BlindAccountEntry A = new("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", BlindAccountLayouts.Dir, "A");
    protected static readonly BlindAccountEntry B = new("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", BlindAccountLayouts.Dir, "B");

    /// <summary>A factory over storage that starts empty for this test and keeps what is saved until the test ends.</summary>
    protected abstract IBlindSecretStoreFactory CreateFactory();

    /// <summary>A NEW factory instance over the same storage: what the next start of the app sees.</summary>
    protected abstract IBlindSecretStoreFactory Reopen();

    /// <summary>Copies the persisted blob of <paramref name="role"/> from <paramref name="from"/>'s slot into <paramref name="to"/>'s slot, as copying the file or the item would.</summary>
    protected abstract void CopyBlob(BlindAccountEntry from, BlindAccountEntry to, BlindAccountNames.Role role);

    private static byte[] Secret(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    [Fact]
    public void TwoAccounts_KeepTheirOwnSecrets_AndNeitherSeesTheOthers()
    {
        var factory = CreateFactory();
        var a = factory.For(A);
        var b = factory.For(B);
        a.SaveIdentitySeed(Secret(1)); a.SaveBackupKey(Secret(2)); a.SavePairingSecret(Secret(3));

        b.LoadIdentitySeed().Should().BeNull("account B has saved nothing");
        b.LoadBackupKey().Should().BeNull();
        b.LoadPairingSecret().Should().BeNull();

        b.SaveIdentitySeed(Secret(11)); b.SaveBackupKey(Secret(12)); b.SavePairingSecret(Secret(13));
        a.LoadIdentitySeed().Should().Equal(Secret(1));
        a.LoadBackupKey().Should().Equal(Secret(2));
        a.LoadPairingSecret().Should().Equal(Secret(3));
        b.LoadIdentitySeed().Should().Equal(Secret(11));
        b.LoadBackupKey().Should().Equal(Secret(12));
        b.LoadPairingSecret().Should().Equal(Secret(13));
    }

    [Fact]
    public void TheLegacyAccount_IsOneMoreAccount_NotTheSameSlotAsAnother()
    {
        var factory = CreateFactory();
        factory.For(Legacy).SaveIdentitySeed(Secret(5));
        factory.For(A).LoadIdentitySeed().Should().BeNull("a new account never reads the 2.6 account's key");
        factory.For(A).SaveIdentitySeed(Secret(6));
        factory.For(Legacy).LoadIdentitySeed().Should().Equal(Secret(5));
    }

    [Fact]
    public void ClearingOneAccount_LeavesTheOther()
    {
        var factory = CreateFactory();
        var a = factory.For(A);
        var b = factory.For(B);
        foreach (var store in new[] { a, b }) { store.SaveIdentitySeed(Secret(7)); store.SaveBackupKey(Secret(8)); store.SavePairingSecret(Secret(9)); }

        a.Clear();

        a.LoadIdentitySeed().Should().BeNull();
        a.LoadBackupKey().Should().BeNull();
        a.LoadPairingSecret().Should().BeNull();
        b.LoadIdentitySeed().Should().Equal(Secret(7));
        b.LoadBackupKey().Should().Equal(Secret(8));
        b.LoadPairingSecret().Should().Equal(Secret(9));
    }

    [Fact]
    public void SpendingThePairingSecret_OfOneAccount_LeavesTheOthersAndTheAccountsOwnKeys()
    {
        var factory = CreateFactory();
        var a = factory.For(A);
        var b = factory.For(B);
        a.SaveIdentitySeed(Secret(1)); a.SavePairingSecret(Secret(3));
        b.SavePairingSecret(Secret(13));

        a.ClearPairingSecret();

        a.LoadPairingSecret().Should().BeNull();
        a.LoadIdentitySeed().Should().Equal(Secret(1));
        b.LoadPairingSecret().Should().Equal(Secret(13));
    }

    [Fact]
    public void WhatWasSaved_IsThereAfterARestart_UnderTheSameAccount_AndOnlyThere()
    {
        var first = CreateFactory();
        first.For(A).SaveBackupKey(Secret(21));
        first.For(B).SaveBackupKey(Secret(22));

        var second = Reopen();
        second.For(A).LoadBackupKey().Should().Equal(Secret(21));
        second.For(B).LoadBackupKey().Should().Equal(Secret(22));
        second.For(Legacy).LoadBackupKey().Should().BeNull();
    }

    [Theory]
    [InlineData(BlindAccountNames.Role.IdentitySeed)]
    [InlineData(BlindAccountNames.Role.PairingSecret)]
    [InlineData(BlindAccountNames.Role.BackupKey)]
    public void ABlobOfOneAccount_PutIntoAnotherAccountsSlot_DoesNotOpenThere(BlindAccountNames.Role role)
    {
        var factory = CreateFactory();
        var a = factory.For(A);
        switch (role)
        {
            case BlindAccountNames.Role.IdentitySeed: a.SaveIdentitySeed(Secret(31)); break;
            case BlindAccountNames.Role.PairingSecret: a.SavePairingSecret(Secret(31)); break;
            default: a.SaveBackupKey(Secret(31)); break;
        }

        CopyBlob(A, B, role);

        var b = Reopen().For(B);
        Func<byte[]?> open = role switch
        {
            BlindAccountNames.Role.IdentitySeed => b.LoadIdentitySeed,
            BlindAccountNames.Role.PairingSecret => b.LoadPairingSecret,
            _ => b.LoadBackupKey,
        };
        byte[]? opened = null;
        var act = () => { opened = open(); };
        // Either way is "does not open": the store refuses the blob (an exception) or finds nothing; it must never hand A's secret to B.
        try { act(); } catch (Exception ex) when (ex is CryptographicException or InvalidDataException or IOException) { opened = null; }
        opened.Should().BeNull("a blob is bound to the account that wrote it");
    }
}

/// <summary>The contract of a per-account state store (<see cref="IBlindStateStoreFactory"/>), for every head's tests as <see cref="BlindSecretStoreContract"/> is for secrets.</summary>
public abstract class BlindStateStoreContract
{
    protected static readonly BlindAccountEntry Legacy = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
    protected static readonly BlindAccountEntry A = new("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", BlindAccountLayouts.Dir, "A");
    protected static readonly BlindAccountEntry B = new("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", BlindAccountLayouts.Dir, "B");

    protected abstract IBlindStateStoreFactory CreateFactory();

    protected abstract IBlindStateStoreFactory Reopen();

    [Fact]
    public void TwoAccounts_NeverShareAKey()
    {
        var factory = CreateFactory();
        var a = factory.For(A);
        var b = factory.For(B);
        a.Set("bmb.blind.node_id", "node-of-a");
        b.Get("bmb.blind.node_id").Should().BeNull();
        b.Set("bmb.blind.node_id", "node-of-b");
        a.Get("bmb.blind.node_id").Should().Be("node-of-a");
        b.Get("bmb.blind.node_id").Should().Be("node-of-b");
        factory.For(Legacy).Get("bmb.blind.node_id").Should().BeNull("the 2.6 account's state is its own");
    }

    [Fact]
    public void SettingNull_RemovesTheKey_OfThatAccountOnly()
    {
        var factory = CreateFactory();
        factory.For(A).Set("k", "1");
        factory.For(B).Set("k", "2");
        factory.For(A).Set("k", null);
        factory.For(A).Get("k").Should().BeNull();
        factory.For(B).Get("k").Should().Be("2");
    }

    [Fact]
    public void Erase_RemovesTheStateOfThatAccount_AndNothingElse()
    {
        var factory = CreateFactory();
        factory.For(A).Set("k", "1");
        factory.For(B).Set("k", "2");

        factory.For(A).Erase();

        factory.For(A).Get("k").Should().BeNull();
        factory.For(B).Get("k").Should().Be("2");
        Reopen().For(A).Get("k").Should().BeNull("erased means gone after a restart too");
    }

    [Fact]
    public void WhatWasSet_IsThereAfterARestart()
    {
        var first = CreateFactory();
        first.For(A).Set("k", "persisted-a");
        first.For(B).Set("k", "persisted-b");

        var second = Reopen();
        second.For(A).Get("k").Should().Be("persisted-a");
        second.For(B).Get("k").Should().Be("persisted-b");
    }
}
