using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Fix round 2, the accounts host: an account that cannot be started, removed or added from an invitation says the KIND of the failure on its card, in the add result and in the exception the
/// removal throws - and nothing a head can show holds the path, the key alias, the address or the words that the underlying exception carried.
/// </summary>
public sealed class BlindAccountFailureTextTests : IDisposable
{
    private readonly InviteRig _rig = new();

    public void Dispose() => _rig.Dispose();

    public static TheoryData<string> Adversarial => FailureCanaries.Names;

    private sealed class FaultySecrets(IBlindSecretStoreFactory inner) : IBlindSecretStoreFactory
    {
        public Exception? OnClear { get; set; }
        public Exception? OnSaveSeed { get; set; }

        public IBlindSecretStore For(BlindAccountEntry account) => new Store(this, inner.For(account));

        private sealed class Store(FaultySecrets owner, IBlindSecretStore real) : IBlindSecretStore
        {
            public void SaveIdentitySeed(byte[] seed) { if (owner.OnSaveSeed is { } failure) throw failure; real.SaveIdentitySeed(seed); }
            public byte[]? LoadIdentitySeed() => real.LoadIdentitySeed();
            public void SaveBackupKey(byte[] key) => real.SaveBackupKey(key);
            public byte[]? LoadBackupKey() => real.LoadBackupKey();
            public void SavePairingSecret(byte[] secret) => real.SavePairingSecret(secret);
            public byte[]? LoadPairingSecret() => real.LoadPairingSecret();
            public void ClearPairingSecret() => real.ClearPairingSecret();
            public void Clear() { if (owner.OnClear is { } failure) throw failure; real.Clear(); }
        }
    }

    private sealed class FaultyStates(IBlindStateStoreFactory inner) : IBlindStateStoreFactory
    {
        public Exception? OnSet { get; set; }

        public IBlindStateStore For(BlindAccountEntry account) => new Store(this, inner.For(account));

        private sealed class Store(FaultyStates owner, IBlindStateStore real) : IBlindStateStore
        {
            public string? Get(string key) => real.Get(key);
            public void Set(string key, string? value) { if (owner.OnSet is { } failure) throw failure; real.Set(key, value); }
            public void Erase() => real.Erase();
        }
    }

    private BlindAccountHost Host(IBlindSecretStoreFactory secrets, IBlindStateStoreFactory? states = null) => new(new BlindAccountHostOptions
    {
        RootDirectory = _rig.Accounts.Root,
        LegacyDatabasePath = _rig.Accounts.LegacyDb,
        Secrets = secrets,
        States = states ?? _rig.Accounts.States,
        DisplayNameFactory = () => "Test device",
        PrimaryHandlerFactory = _ => () => _rig.Router,
        DrainTimeout = TimeSpan.FromSeconds(10),
        LeaveTimeout = TimeSpan.FromSeconds(2),
        Time = _rig.Clock,
        RootLockWait = TimeSpan.FromMilliseconds(150),
    });

    private void AssertHostSaysNothingOfTheException(BlindAccountHost host)
    {
        var snapshot = host.GetSnapshot();
        FailureCanaries.AssertClean(snapshot, "the host's snapshot (the cards, their status and problem lines, the repairs)");
        FailureCanaries.AssertClean(BlindAccountsView.Build(snapshot, TimeZoneInfo.Utc, _rig.Clock.GetUtcNow()), "the accounts view");
        foreach (var card in snapshot.Accounts)
            FailureCanaries.AssertClean(BlindAccountCard.Build(card, TimeZoneInfo.Utc, _rig.Clock.GetUtcNow()), "an account's card");
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task ARemovalThatCannotForgetTheKeys_ThrowsNoPathOrAliasToAHead_AndTheCardSaysTheKind(string name)
    {
        var secrets = new FaultySecrets(_rig.Accounts.Secrets);
        await using var host = Host(secrets);
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        secrets.OnClear = FailureCanaries.Make(name);

        var act = () => host.RemoveAsync(a);

        var failure = (await act.Should().ThrowAsync<AggregateException>()).Which;
        FailureCanaries.AssertClean(failure, "the exception a head gets (its message and everything inside it)");
        FailureCanaries.Leaks(BeeMemoryBank.Sync.FailureText.Story(failure)).Should().BeFalse("the whole story a head's busy helper tells");
        failure.Message.Should().Contain("forgetting the keys");
        failure.InnerExceptions.Should().OnlyContain(e => e.InnerException == null, "the raw exception is not carried along for a head to read");
        var card = host.GetSnapshot().Accounts.Single(c => c.Entry.Id == a);
        card.Problem.Should().Contain("Its removal is not complete").And.Contain("forgetting the keys");
        AssertHostSaysNothingOfTheException(host);
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task ARemovalThatCannotForgetTheState_SaysTheKindOnTheCard_AndInTheException(string name)
    {
        var states = new FaultyStates(_rig.Accounts.States);
        await using var host = Host(_rig.Accounts.Secrets, states);
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        states.OnSet = FailureCanaries.Make(name);

        var act = () => host.RemoveAsync(a);

        var failure = (await act.Should().ThrowAsync<AggregateException>()).Which;
        FailureCanaries.AssertClean(failure, "the exception a head gets");
        failure.Message.Should().Contain("forgetting the state");
        AssertHostSaysNothingOfTheException(host);
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task AnAccountThatCannotBeStarted_SaysTheKind_InTheAddResultAndOnItsCard(string name)
    {
        var secrets = new FaultySecrets(_rig.Accounts.Secrets);
        await using var host = Host(secrets);
        await host.StartAsync();
        secrets.OnSaveSeed = FailureCanaries.Make(name);

        var added = await host.AddAsync("A");

        FailureCanaries.AssertClean(added, "the add result");
        var card = BlindAccountCard.Build(host.GetSnapshot().Accounts.Single(), TimeZoneInfo.Utc, _rig.Clock.GetUtcNow());
        card.Kind.Should().Be(BlindCardKind.Problem, "the identity could not be made");
        card.State.Should().StartWith("Could not start: ").And.Contain(BlindFailureText.WithoutFullStop(BlindFailureText.Say(FailureCanaries.Make(name))));
        AssertHostSaysNothingOfTheException(host);
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task AnInvitationThatCannotBeKeptOnTheDevice_SaysTheKind_AndNothingWasSent(string name)
    {
        var states = new FaultyStates(_rig.Accounts.States);
        var hub = _rig.Hub("hub-a.test");
        await using var host = Host(_rig.Accounts.Secrets, states);
        await host.StartAsync();
        var invitation = hub.Issue("Alex");
        states.OnSet = FailureCanaries.Make(name);

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeFalse();
        added.Error.Should().StartWith("The invitation could not be kept on this device (").And.EndWith("). Nothing was sent.");
        FailureCanaries.AssertClean(added, "the add result");
        AssertHostSaysNothingOfTheException(host);
    }

    [Fact]
    public async Task ASentenceThrownOnPurpose_IsSaidAsItIs_OnTheCardAndInTheRemoval()
    {
        var secrets = new FaultySecrets(_rig.Accounts.Secrets);
        await using var host = Host(secrets);
        await host.StartAsync();
        secrets.OnSaveSeed = new BeeMemoryBank.Sync.Blind.BlindSentenceException("Choose a smaller number.");

        await host.AddAsync("A");

        var card = BlindAccountCard.Build(host.GetSnapshot().Accounts.Single(), TimeZoneInfo.Utc, _rig.Clock.GetUtcNow());
        card.State.Should().Be("Could not start: Choose a smaller number.");

        secrets.OnSaveSeed = null;
        secrets.OnClear = new BeeMemoryBank.Sync.Blind.BlindSentenceException("Choose a smaller number.");
        var act = () => host.RemoveAsync(host.GetSnapshot().Accounts.Single().Entry.Id);
        (await act.Should().ThrowAsync<AggregateException>()).Which.Message.Should().Contain("forgetting the keys: Choose a smaller number.");
    }
}
