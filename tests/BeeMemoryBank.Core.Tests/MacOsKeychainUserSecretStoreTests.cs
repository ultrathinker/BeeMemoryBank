using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Platforms.Apple.Keychain;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// What is specific to the macOS Keychain store, proved over a fake in-memory Keychain on every operating system: the item layout (service,
/// account, label, envelope), the damaged-envelope and moved-item findings, the OSStatus mapping of every operation, "never a partial item",
/// the zeroing of every buffer the store makes, the absence of any secret/account/purpose in a message, the scope that keeps two data
/// folders apart, and the platform flag. The behaviors every store shares are in <see cref="UserSecretStoreContract"/>.
/// </summary>
public class MacOsKeychainUserSecretStoreTests
{
    private const string Service = "test.bmb.desktop.secrets.unit";
    private const string SentinelAccount = "account-sentinel-7c1f";
    private const string SentinelPurpose = "purpose-sentinel-5d2e";

    private static (MacOsKeychainUserSecretStore Store, FakeKeychainBackend Fake) New(string? scope = null, string service = Service, bool? isSupported = true)
    {
        var fake = new FakeKeychainBackend();
        return (new MacOsKeychainUserSecretStore(fake, service, scope, isSupported), fake);
    }

    /// <summary>The envelope, recomputed here from the documented layout, independent of the store's code.</summary>
    private static byte[] ExpectedItem(string? scope, string purpose, string account, byte[] data)
    {
        var input = new List<byte>();
        input.AddRange("bmb-desktop-keychain-item-v1\0"u8.ToArray());
        input.AddRange(Encoding.UTF8.GetBytes(scope ?? ""));
        input.Add(0);
        input.AddRange(Encoding.UTF8.GetBytes(purpose));
        input.Add(0);
        input.AddRange(Encoding.UTF8.GetBytes(account));
        input.Add(0);
        input.AddRange(data);
        var item = new byte[1 + 32 + data.Length];
        item[0] = 1;
        SHA256.HashData(input.ToArray()).CopyTo(item, 1);
        data.CopyTo(item, 33);
        return item;
    }

    // ── the item layout ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultService_IsThePublishedOne()
    {
        MacOsKeychainUserSecretStore.DefaultService.Should().Be("com.beememorybank.desktop.secrets.v1");
        var fake = new FakeKeychainBackend();
        var store = new MacOsKeychainUserSecretStore(fake, isSupported: true);

        store.Write("os-auto-unlock", "default", [1, 2, 3]);

        fake.Keys.Should().ContainSingle().Which.Should().Be(("com.beememorybank.desktop.secrets.v1", "os-auto-unlock/default"));
    }

    [Fact]
    public void TheServiceCanBeOverridden_SoATestNeverSharesItemsWithARealInstall()
    {
        var (store, fake) = New(service: "some.other.service");

        store.Write("local-ca", "default", [1]);

        fake.Keys.Should().ContainSingle().Which.Service.Should().Be("some.other.service");
    }

    [Fact]
    public void TheKeychainAccount_IsPurposeSlashAccount_AndScopeSlashPurposeSlashAccountWhenScoped()
    {
        var (plain, plainFake) = New();
        var (scoped, scopedFake) = New(scope: "0123456789abcdef");

        plain.Write("sample-password", "node.example.com", [1]);
        scoped.Write("sample-password", "node.example.com", [1]);

        plainFake.Keys.Should().ContainSingle().Which.Account.Should().Be("sample-password/node.example.com");
        scopedFake.Keys.Should().ContainSingle().Which.Account.Should().Be("0123456789abcdef/sample-password/node.example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789abcdef")]
    public void TheItem_IsVersionDigestData_ExactlyAsDocumented(string? scope)
    {
        var (store, fake) = New(scope);
        var secret = RandomNumberGenerator.GetBytes(32);

        store.Write("os-auto-unlock", "default", secret);

        var raw = fake.GetRaw(Service, (scope is null ? "" : scope + "/") + "os-auto-unlock/default")!;
        raw.Length.Should().Be(1 + 32 + 32);
        raw[0].Should().Be(1, "the envelope version");
        raw.Should().Equal(ExpectedItem(scope, "os-auto-unlock", "default", secret));
    }

    [Fact]
    public void AnEmptyValue_IsAValidSecret_AndRoundTrips()
    {
        var (store, fake) = New();

        store.Write("purpose", "account", ReadOnlySpan<byte>.Empty);

        fake.GetRaw(Service, "purpose/account")!.Length.Should().Be(33);
        store.Read("purpose", "account").Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void TheLabel_NamesTheApp_AndThePurpose_NeverTheAccountOrTheSecret()
    {
        var (store, fake) = New();
        var secret = Encoding.UTF8.GetBytes("super-secret-value-0123456789");

        store.Write("local-ca", SentinelAccount, secret);

        var label = fake.Labels.Should().ContainSingle().Subject;
        label.Should().StartWith("Bee Memory Bank").And.Contain("local-ca");
        label.Should().NotContain(SentinelAccount).And.NotContain("super-secret");
    }

    // ── damaged and moved items ────────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Damages()
    {
        yield return ["empty item", new Func<byte[], byte[]>(_ => [])];
        yield return ["only the version byte", new Func<byte[], byte[]>(i => i[..1])];
        yield return ["truncated inside the digest", new Func<byte[], byte[]>(i => i[..32])];
        yield return ["a plain string put there from outside", new Func<byte[], byte[]>(_ => "a plain string, not an envelope"u8.ToArray())];
        yield return ["another envelope version", new Func<byte[], byte[]>(i => { var c = i.ToArray(); c[0] = 2; return c; })];
        yield return ["version zero", new Func<byte[], byte[]>(i => { var c = i.ToArray(); c[0] = 0; return c; })];
        yield return ["a flipped digest bit", new Func<byte[], byte[]>(i => { var c = i.ToArray(); c[1] ^= 0x01; return c; })];
        yield return ["a flipped last digest bit", new Func<byte[], byte[]>(i => { var c = i.ToArray(); c[32] ^= 0x80; return c; })];
        yield return ["a flipped data bit", new Func<byte[], byte[]>(i => { var c = i.ToArray(); c[33] ^= 0x01; return c; })];
        yield return ["a byte appended", new Func<byte[], byte[]>(i => [.. i, 0])];
        yield return ["a data byte removed", new Func<byte[], byte[]>(i => i[..^1])];
        yield return ["the digest zeroed", new Func<byte[], byte[]>(i => { var c = i.ToArray(); Array.Clear(c, 1, 32); return c; })];
    }

    [Theory]
    [MemberData(nameof(Damages))]
    public void ADamagedItem_IsMalformed_AndNeverReturnedAsASecret(string what, Func<byte[], byte[]> damage)
    {
        var (store, fake) = New();
        store.Write("os-auto-unlock", "default", RandomNumberGenerator.GetBytes(32));
        fake.SetRaw(Service, "os-auto-unlock/default", damage(fake.GetRaw(Service, "os-auto-unlock/default")!));

        var read = () => store.Read("os-auto-unlock", "default");

        read.Should().Throw<UserSecretStoreException>(what).Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
    }

    [Fact]
    public void ADamagedItem_IsReplacedByWritingAgain_ButNeverRepairedByReading()
    {
        var (store, fake) = New();
        store.Write("p", "a", [1, 2, 3]);
        fake.SetRaw(Service, "p/a", [9, 9, 9]);
        var before = fake.GetRaw(Service, "p/a")!;

        ((Action)(() => store.Read("p", "a"))).Should().Throw<UserSecretStoreException>();
        fake.GetRaw(Service, "p/a").Should().Equal(before, "a failed read changes nothing");

        store.Write("p", "a", [4, 5, 6]);
        store.Read("p", "a").Should().Equal(4, 5, 6);
    }

    [Fact]
    public void AnItemCopiedToAnotherPurposeOrAccount_IsMalformed()
    {
        var (store, fake) = New();
        store.Write("os-auto-unlock", "default", RandomNumberGenerator.GetBytes(32));
        var item = fake.GetRaw(Service, "os-auto-unlock/default")!;
        fake.SetRaw(Service, "update-unlock/default", item);
        fake.SetRaw(Service, "os-auto-unlock/other", item);

        foreach (var (purpose, account) in new[] { ("update-unlock", "default"), ("os-auto-unlock", "other") })
        {
            var read = () => store.Read(purpose, account);
            read.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
        }
    }

    [Fact]
    public void AnItemCopiedFromAnotherDataFolder_IsMalformed()
    {
        var fake = new FakeKeychainBackend();
        var folderA = new MacOsKeychainUserSecretStore(fake, Service, "aaaaaaaaaaaaaaaa", isSupported: true);
        var folderB = new MacOsKeychainUserSecretStore(fake, Service, "bbbbbbbbbbbbbbbb", isSupported: true);
        folderA.Write("os-auto-unlock", "default", RandomNumberGenerator.GetBytes(32));
        fake.SetRaw(Service, "bbbbbbbbbbbbbbbb/os-auto-unlock/default", fake.GetRaw(Service, "aaaaaaaaaaaaaaaa/os-auto-unlock/default")!);

        var read = () => folderB.Read("os-auto-unlock", "default");

        read.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
    }

    [Fact]
    public void AnItemOfAnUnscopedStore_IsNotAScopedStoresItem()
    {
        var fake = new FakeKeychainBackend();
        var unscoped = new MacOsKeychainUserSecretStore(fake, Service, null, isSupported: true);
        var scoped = new MacOsKeychainUserSecretStore(fake, Service, "aaaaaaaaaaaaaaaa", isSupported: true);
        unscoped.Write("p", "a", [1, 2, 3]);
        fake.SetRaw(Service, "aaaaaaaaaaaaaaaa/p/a", fake.GetRaw(Service, "p/a")!);

        ((Action)(() => scoped.Read("p", "a"))).Should().Throw<UserSecretStoreException>()
            .Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
        scoped.Read("p", "other").Should().BeNull();
    }

    // ── the OSStatus mapping ───────────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> FailureStatuses()
    {
        yield return [KeychainStatus.InteractionNotAllowed, UserSecretStoreFailureKind.Locked];
        yield return [KeychainStatus.AuthFailed, UserSecretStoreFailureKind.Locked];
        yield return [KeychainStatus.UserCanceled, UserSecretStoreFailureKind.Denied];
        yield return [KeychainStatus.NoSuchKeychain, UserSecretStoreFailureKind.Unavailable];
        yield return [KeychainStatus.InvalidKeychain, UserSecretStoreFailureKind.Unavailable];
        yield return [KeychainStatus.Param, UserSecretStoreFailureKind.Unavailable];
        yield return [-25243, UserSecretStoreFailureKind.Unavailable]; // errSecNoAccessForItem
        yield return [-34018, UserSecretStoreFailureKind.Unavailable]; // errSecMissingEntitlement
        yield return [-25292, UserSecretStoreFailureKind.Unavailable]; // errSecReadOnly
        yield return [-1, UserSecretStoreFailureKind.Unavailable];
        yield return [int.MinValue, UserSecretStoreFailureKind.Unavailable];
        yield return [int.MaxValue, UserSecretStoreFailureKind.Unavailable];
    }

    [Theory]
    [MemberData(nameof(FailureStatuses))]
    public void Read_MapsEveryRefusalToATypedFailure_NeverToNull(int status, UserSecretStoreFailureKind expected)
    {
        var (store, fake) = New();
        store.Write("p", "a", [1]);
        fake.CopyStatus = status;

        var read = () => store.Read("p", "a");

        var thrown = read.Should().Throw<UserSecretStoreException>().Which;
        thrown.FailureKind.Should().Be(expected);
        thrown.Message.Should().Contain($"Keychain status {status}").And.Contain("fake text for");
    }

    [Fact]
    public void Read_OnlyTheRealNotFoundStatus_IsNull()
    {
        var (store, fake) = New();
        store.Write("p", "a", [1]);

        fake.CopyStatus = KeychainStatus.ItemNotFound;
        store.Read("p", "a").Should().BeNull();

        // DuplicateItem is a status of Add; "item not found" must not be inferred from any other number.
        fake.CopyStatus = KeychainStatus.DuplicateItem;
        ((Action)(() => store.Read("p", "a"))).Should().Throw<UserSecretStoreException>()
            .Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
    }

    [Fact]
    public void Read_SuccessWithoutData_IsUnavailable_NotNull()
    {
        var (store, fake) = New();
        fake.CopyGivesNoData = true;

        var read = () => store.Read("p", "a");

        read.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
    }

    [Theory]
    [MemberData(nameof(FailureStatuses))]
    public void Write_MapsEveryRefusalOfTheAdd_AndLeavesNoItem(int status, UserSecretStoreFailureKind expected)
    {
        var (store, fake) = New();
        fake.AddStatus = status;

        var write = () => store.Write("p", "a", [1, 2, 3]);

        write.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(expected);
        fake.Keys.Should().BeEmpty("a failed write never leaves a partial item");
    }

    [Theory]
    [MemberData(nameof(FailureStatuses))]
    public void Write_MapsEveryRefusalOfTheUpdate_AndTheOldValueSurvives(int status, UserSecretStoreFailureKind expected)
    {
        var (store, fake) = New();
        store.Write("p", "a", [1, 2, 3]);
        fake.UpdateStatus = status;

        var write = () => store.Write("p", "a", [4, 5, 6]);

        write.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(expected);
        fake.UpdateStatus = null;
        store.Read("p", "a").Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Write_OfAnExistingItem_IsAnUpdateInPlace_NotADeleteAndAdd()
    {
        var (store, fake) = New();
        store.Write("p", "a", [1]);
        fake.Calls.Clear();

        store.Write("p", "a", [2]);

        fake.Calls.Should().Equal($"add {Service} p/a", $"update {Service} p/a");
        fake.Keys.Should().ContainSingle();
    }

    [Fact]
    public void Write_WhenAnotherProcessRemovedTheItemBetweenTheCalls_AddsAgain()
    {
        var (store, fake) = New();
        store.Write("p", "a", [1]);
        fake.BeforeUpdate = (service, account) => fake.RemoveRaw(service, account);

        store.Write("p", "a", [2]);

        fake.BeforeUpdate = null;
        store.Read("p", "a").Should().Equal(2);
    }

    [Fact]
    public void Write_WhenTheItemKeepsBeingADuplicate_IsAFailure_NotALoop()
    {
        var (store, fake) = New();
        fake.AddStatus = KeychainStatus.DuplicateItem;
        fake.UpdateStatus = KeychainStatus.ItemNotFound;

        var write = () => store.Write("p", "a", [1]);

        write.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
        fake.Calls.Count.Should().BeLessThanOrEqualTo(3, "add, update, and one more add - then it stops");
    }

    [Fact]
    public void Delete_MissingAndDeleted_AreSuccess_EverythingElseIsTyped()
    {
        var (store, fake) = New();
        store.Write("p", "a", [1]);

        store.Delete("p", "a");
        ((Action)(() => store.Delete("p", "a"))).Should().NotThrow();
        fake.DeleteStatus = KeychainStatus.ItemNotFound;
        ((Action)(() => store.Delete("p", "a"))).Should().NotThrow();

        foreach (var row in FailureStatuses())
        {
            fake.DeleteStatus = (int)row[0];
            var delete = () => store.Delete("p", "a");
            delete.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be((UserSecretStoreFailureKind)row[1]);
        }
    }

    [Fact]
    public void ANativeLibraryThatCannotBeLoaded_IsUnavailable_WithTheCauseAttached()
    {
        foreach (Exception cause in new Exception[] { new DllNotFoundException("x"), new EntryPointNotFoundException("x"), new PlatformNotSupportedException("x") })
        {
            var (store, fake) = New();
            fake.ThrowOnEveryCall = cause;

            foreach (var action in new Action[]
                     {
                         () => store.Read("p", "a"),
                         () => store.Write("p", "a", [1]),
                         () => store.Delete("p", "a"),
                     })
            {
                var thrown = action.Should().Throw<UserSecretStoreException>().Which;
                thrown.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
                thrown.InnerException.Should().BeSameAs(cause);
            }
        }
    }

    [Fact]
    public void AnUnexpectedBackendBug_IsNotSwallowedAsAKeychainFailure()
    {
        var (store, fake) = New();
        fake.ThrowOnEveryCall = new InvalidOperationException("a bug");

        ((Action)(() => store.Read("p", "a"))).Should().Throw<InvalidOperationException>();
    }

    // ── nothing sensitive in a message; every buffer zeroed ────────────────────────────────────────────────────

    [Fact]
    public void NoMessage_ContainsTheSecretTheAccountOrThePurpose()
    {
        var secret = Encoding.UTF8.GetBytes("the-secret-text-0f9e8d7c");
        var messages = new List<string>();

        foreach (var row in FailureStatuses())
        {
            var (store, fake) = New();
            store.Write(SentinelPurpose, SentinelAccount, secret);
            fake.CopyStatus = (int)row[0];
            fake.UpdateStatus = (int)row[0];
            fake.DeleteStatus = (int)row[0];
            foreach (var action in new Action[]
                     {
                         () => store.Read(SentinelPurpose, SentinelAccount),
                         () => store.Write(SentinelPurpose, SentinelAccount, secret),
                         () => store.Delete(SentinelPurpose, SentinelAccount),
                     })
                messages.Add(action.Should().Throw<UserSecretStoreException>().Which.Message);
        }
        var (damaged, damagedFake) = New();
        damaged.Write(SentinelPurpose, SentinelAccount, secret);
        damagedFake.SetRaw(Service, SentinelPurpose + "/" + SentinelAccount, [1, 2, 3]);
        messages.Add(((Action)(() => damaged.Read(SentinelPurpose, SentinelAccount))).Should().Throw<UserSecretStoreException>().Which.Message);
        var (off, _) = New(isSupported: false);
        messages.Add(((Action)(() => off.Read(SentinelPurpose, SentinelAccount))).Should().Throw<UserSecretStoreException>().Which.Message);

        messages.Should().HaveCountGreaterThan(30);
        foreach (var message in messages)
        {
            message.Should().NotContain(SentinelAccount).And.NotContain(SentinelPurpose).And.NotContain("the-secret-text")
                .And.NotContain(Convert.ToHexString(secret), because: "no secret in any encoding")
                .And.NotContain(Convert.ToBase64String(secret)).And.NotContain(Service);
        }
    }

    [Fact]
    public void EveryBufferTheStoreMakes_IsZeroedAfterUse()
    {
        var (store, fake) = New();
        var secret = RandomNumberGenerator.GetBytes(48);

        store.Write("p", "a", secret);
        store.Write("p", "a", secret); // the update path
        fake.PassedIn.Should().HaveCount(3, "an add, a refused add (duplicate), an update");
        fake.PassedIn.Should().OnlyContain(b => b.All(x => x == 0), "the sealed item is zeroed once the Keychain has it");

        var read = store.Read("p", "a");
        read.Should().Equal(secret);
        fake.HandedOut.Should().ContainSingle().Which.Should().OnlyContain(x => x == 0, "the item the Keychain handed over is zeroed once opened");
        read.Should().Equal(secret, "the secret returned to the caller is the caller's, and not zeroed");

        fake.SetRaw(Service, "p/a", [1, 2, 3]);
        ((Action)(() => store.Read("p", "a"))).Should().Throw<UserSecretStoreException>();
        fake.HandedOut.Should().OnlyContain(b => b.All(x => x == 0), "a damaged item is zeroed too");

        store.Write("p", "a", secret);
        var flipped = fake.GetRaw(Service, "p/a")!;
        flipped[^1] ^= 1;
        fake.SetRaw(Service, "p/a", flipped);
        ((Action)(() => store.Read("p", "a"))).Should().Throw<UserSecretStoreException>();
        fake.HandedOut.Should().OnlyContain(b => b.All(x => x == 0));
    }

    [Fact]
    public void ARefusedWrite_ZeroesTheSealedItemToo()
    {
        var (store, fake) = New();
        fake.AddStatus = KeychainStatus.InteractionNotAllowed;

        ((Action)(() => store.Write("p", "a", RandomNumberGenerator.GetBytes(32)))).Should().Throw<UserSecretStoreException>();

        fake.PassedIn.Should().ContainSingle().Which.Should().OnlyContain(x => x == 0);
    }

    // ── names, scope, platform ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a/b", "c")]
    [InlineData("a\0b", "c")]
    [InlineData("a", "b\0c")]
    public void ANameThatCouldMakeTwoSecretsShareAnItem_IsRefused(string purpose, string account)
    {
        var (store, fake) = New();

        ((Action)(() => store.Write(purpose, account, [1]))).Should().Throw<ArgumentException>();
        ((Action)(() => store.Read(purpose, account))).Should().Throw<ArgumentException>();
        ((Action)(() => store.Delete(purpose, account))).Should().Throw<ArgumentException>();
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public void AnAccountMayContainASlash_AndStillBelongsToItsPurposeOnly()
    {
        var (store, fake) = New();

        store.Write("a", "b/c", [1]);

        store.Read("a", "b/c").Should().Equal(1);
        store.Read("a", "b").Should().BeNull();
        fake.Keys.Should().ContainSingle().Which.Account.Should().Be("a/b/c");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a/b")]
    [InlineData("a\0b")]
    public void AnInvalidScope_OrService_IsRefusedAtConstruction(string value)
    {
        var fake = new FakeKeychainBackend();

        ((Action)(() => new MacOsKeychainUserSecretStore(fake, scope: value))).Should().Throw<ArgumentException>();
        if (value.Trim().Length == 0)
            ((Action)(() => new MacOsKeychainUserSecretStore(fake, service: value))).Should().Throw<ArgumentException>();
        ((Action)(() => new MacOsKeychainUserSecretStore(null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TwoScopes_OnOneKeychain_NeverShareASecret()
    {
        var fake = new FakeKeychainBackend();
        var one = new MacOsKeychainUserSecretStore(fake, Service, "1111111111111111", isSupported: true);
        var two = new MacOsKeychainUserSecretStore(fake, Service, "2222222222222222", isSupported: true);
        var none = new MacOsKeychainUserSecretStore(fake, Service, null, isSupported: true);

        one.Write("os-auto-unlock", "default", [1]);
        two.Write("os-auto-unlock", "default", [2]);
        none.Write("os-auto-unlock", "default", [3]);

        one.Read("os-auto-unlock", "default").Should().Equal(1);
        two.Read("os-auto-unlock", "default").Should().Equal(2);
        none.Read("os-auto-unlock", "default").Should().Equal(3);
        two.Delete("os-auto-unlock", "default");
        one.Read("os-auto-unlock", "default").Should().Equal(new byte[] { 1 }, "deleting a vault's secret does not touch another vault's");
        fake.Keys.Should().HaveCount(2);
    }

    [Fact]
    public void IsSupported_IsTheFlag_AndWithoutItTheBackendIsNeverCalled()
    {
        var (off, fake) = New(isSupported: false);

        off.IsSupported.Should().BeFalse();
        foreach (var action in new Action[]
                 {
                     () => off.Read("p", "a"),
                     () => off.Write("p", "a", [1]),
                     () => off.Delete("p", "a"),
                 })
            action.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
        fake.Calls.Should().BeEmpty("an unsupported store does not reach the Keychain");
    }

    [Fact]
    public void WithoutAFlag_IsSupported_FollowsTheOperatingSystem()
    {
        var (store, _) = New(isSupported: null);

        store.IsSupported.Should().Be(OperatingSystem.IsMacOS());
    }

    [NotMacFact]
    public void OffAMac_TheProductionStores_AreUnsupported_AndEveryCallIsUnavailable_NeverACrash()
    {
        using var login = MacOsKeychainUserSecretStore.ForDefaultKeychain();
        using var file = MacOsKeychainUserSecretStore.ForKeychainFile("/tmp/does-not-matter.keychain");

        foreach (var store in new[] { login, file })
        {
            store.IsSupported.Should().BeFalse();
            foreach (var action in new Action[]
                     {
                         () => store.Read("p", "a"),
                         () => store.Write("p", "a", [1]),
                         () => store.Delete("p", "a"),
                     })
                action.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
        }
    }

    [Fact]
    public void AnInjectedBackend_BelongsToTheCaller_AndIsNotDisposedByTheStore()
    {
        var (store, fake) = New();

        store.Dispose();

        fake.Disposed.Should().BeFalse();
    }

    [Fact]
    public void TheFactory_GivesTheKeychainStoreOnAMac_AndNeverTheDpapiStore()
    {
        var store = UserSecretStores.CreateDefault(Path.Combine(Path.GetTempPath(), "bmb-factory-test"));

        if (OperatingSystem.IsMacOS())
        {
            store.Should().BeOfType<MacOsKeychainUserSecretStore>();
            store.IsSupported.Should().BeTrue();
        }
        else
        {
            store.Should().NotBeOfType<MacOsKeychainUserSecretStore>();
        }
    }
}
