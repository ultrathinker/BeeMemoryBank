using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Infrastructure.Acme.Tests;

public sealed class UserSecretStoreTests
{
    [Fact]
    public void Round_trip_returns_a_copy()
    {
        var store = new InMemoryUserSecretStore();
        store.Write("test", "account", new byte[] { 1, 2, 3 });

        var result = store.Read("test", "account");

        result.Should().Equal(1, 2, 3);
        result![0] = 9;
        store.Read("test", "account").Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Not_found_is_null()
    {
        new InMemoryUserSecretStore().Read("test", "missing").Should().BeNull();
    }

    [Fact]
    public void Failure_is_typed()
    {
        var store = new InMemoryUserSecretStore
        {
            ReadFailure = new UserSecretStoreException(UserSecretStoreFailureKind.Locked, "locked")
        };

        var action = () => store.Read("test", "account");

        action.Should().Throw<UserSecretStoreException>()
            .Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Locked);
    }

    [Fact]
    public void Purpose_binding_prevents_a_secret_being_read_as_another_purpose()
    {
        var store = new InMemoryUserSecretStore();
        store.Write("one", "account", new byte[] { 1 });

        store.Read("two", "account").Should().BeNull();
    }

    [Fact]
    public void Delete_zeros_the_removed_test_value()
    {
        var store = new InMemoryUserSecretStore();
        store.Write("test", "account", new byte[] { 1 });

        store.Delete("test", "account");

        store.Read("test", "account").Should().BeNull();
    }
}
