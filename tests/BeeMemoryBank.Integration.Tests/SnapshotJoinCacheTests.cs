using BeeMemoryBank.Api.Services;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>The join snapshot cache is keyed by the DEK fingerprint (docs/design/step-a-spec.md 11 step 2).</summary>
public class SnapshotJoinCacheTests
{
    private static SnapshotJoinCache Filled(string? fingerprint)
    {
        var cache = new SnapshotJoinCache();
        cache.Set("a.tar.gz", "a.sig", 5, Guid.NewGuid(), 7, fingerprint);
        return cache;
    }

    [Fact]
    public void A_plain_entry_serves_a_plain_request_as_before()
    {
        var cache = Filled(null);

        cache.TryGet().Should().NotBeNull();
        cache.TryGet(null).Should().NotBeNull();
    }

    [Fact]
    public void A_plain_entry_never_serves_an_activated_node()
    {
        var cache = Filled(null);

        cache.TryGet("fp1").Should().BeNull();
        cache.TryGet().Should().BeNull("the entry that did not fit is dropped");
    }

    [Fact]
    public void A_sealed_entry_serves_only_the_key_it_is_sealed_under()
    {
        var cache = Filled("fp1");

        cache.TryGet("fp1").Should().NotBeNull();
        cache.TryGet("fp2").Should().BeNull("a DEK rotation changed the fingerprint: the old archive is not served");
        cache.TryGet("fp1").Should().BeNull("and it is gone");
    }

    [Fact]
    public void A_sealed_entry_never_serves_a_plain_request()
    {
        var cache = Filled("fp1");

        cache.TryGet().Should().BeNull();
    }

    [Fact]
    public void Invalidate_drops_the_entry()
    {
        var cache = Filled("fp1");

        cache.Invalidate();

        cache.TryGet("fp1").Should().BeNull();
    }
}
