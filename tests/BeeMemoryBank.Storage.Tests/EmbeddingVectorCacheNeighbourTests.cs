using System.Runtime.InteropServices;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// <see cref="EmbeddingVectorCache.Snapshot.GetNeighbours"/>: the articles whose vectors stand out from an
/// article's own similarity distribution. Checked against an independent reference computation on a seeded
/// corpus, and on the cases that must yield nothing (no vector, protected, identical vectors).
/// </summary>
public class EmbeddingVectorCacheNeighbourTests : IAsyncLifetime
{
    private const int Dim = 32;

    private DbConnectionFactory _factory = null!;
    private EmbeddingVectorCache _cache = null!;

    public Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_neighbours_{Guid.NewGuid():N}");
        _cache = new EmbeddingVectorCache(_factory);
        return new MigrationRunner(_factory).RunMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> InsertAsync(string title, float[]? projection, string status = "A")
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        byte[]? bytes = projection == null ? null : MemoryMarshal.AsBytes(projection.AsSpan()).ToArray();
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at, embedding_projection)
              VALUES (@id, @title, '/', @status, @now, @now, @bytes)",
            new { id, title, status, now, bytes });
        return id;
    }

    private static float[] Random(Random random)
    {
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++) v[i] = (float)(random.NextDouble() * 2 - 1);
        return v;
    }

    /// <summary>A vector close to <paramref name="basis"/>: the basis plus noise of the given relative size.</summary>
    private static float[] Around(float[] basis, Random random, double noise)
    {
        var v = Random(random);
        var result = new float[Dim];
        for (int i = 0; i < Dim; i++) result[i] = (float)(basis[i] + noise * v[i]);
        return result;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    [Fact]
    public async Task TheClosestArticlesComeFirst_WithTheZScoreOfAnIndependentComputation()
    {
        var random = new Random(7001);
        var subject = Random(random);
        var subjectId = await InsertAsync("subject", subject);
        var vectors = new Dictionary<Guid, float[]>();   // every article except the subject
        async Task<Guid> Add(string title, float[] v)
        {
            var id = await InsertAsync(title, v);
            vectors[id] = v;
            return id;
        }
        var twin = await Add("twin", Around(subject, random, 0.2));
        var cousin = await Add("cousin", Around(subject, random, 0.6));
        for (int i = 0; i < 60; i++) await Add($"other {i}", Random(random));

        var neighbours = _cache.GetOrRebuild().GetNeighbours(subjectId, topK: 50, minZ: 2.0f);

        neighbours.Should().NotBeEmpty();
        neighbours[0].Id.Should().Be(twin);
        neighbours.Select(n => n.Similarity).Should().BeInDescendingOrder();
        neighbours.Should().NotContain(n => n.Id == subjectId, "an article is not its own neighbour");

        // Reference: mean and population standard deviation of the similarity to every other article.
        var sims = vectors.ToDictionary(kv => kv.Key, kv => Cosine(subject, kv.Value));
        var mean = sims.Values.Average();
        var std = Math.Sqrt(sims.Values.Average(v => (v - mean) * (v - mean)));
        foreach (var n in neighbours)
        {
            ((double)n.Similarity).Should().BeApproximately(sims[n.Id], 1e-4);
            ((double)n.Z).Should().BeApproximately((sims[n.Id] - mean) / std, 1e-3);
            n.Z.Should().BeGreaterThanOrEqualTo(2.0f);
        }

        // The cousin (three times the noise) is further away than the twin.
        sims[cousin].Should().BeLessThan(sims[twin]);
    }

    [Fact]
    public async Task OnlyArticlesAboveTheZThresholdAreReturned_AndTopKCutsTheList()
    {
        var random = new Random(7002);
        var subject = Random(random);
        var subjectId = await InsertAsync("subject", subject);
        for (int i = 0; i < 8; i++) await InsertAsync($"near {i}", Around(subject, random, 0.15 + i * 0.01));
        for (int i = 0; i < 80; i++) await InsertAsync($"other {i}", Random(random));

        var snapshot = _cache.GetOrRebuild();
        var all = snapshot.GetNeighbours(subjectId, topK: 500, minZ: 2.0f);
        var top3 = snapshot.GetNeighbours(subjectId, topK: 3, minZ: 2.0f);
        var cut = all[all.Count / 2].Z;   // a threshold in the middle of what the looser one returned
        var strict = snapshot.GetNeighbours(subjectId, topK: 500, minZ: cut);

        all.Should().OnlyContain(n => n.Z >= 2.0f);
        all.Count.Should().BeGreaterThanOrEqualTo(8, "the eight planted neighbours stand out from 80 random vectors");
        top3.Select(n => n.Id).Should().Equal(all.Take(3).Select(n => n.Id));
        strict.Should().NotBeEmpty().And.OnlyContain(n => n.Z >= cut);
        strict.Count.Should().BeLessThan(all.Count);
        snapshot.GetNeighbours(subjectId, topK: 0, minZ: 2.0f).Should().BeEmpty();
    }

    [Fact]
    public async Task ProtectedAndUnembeddedArticlesNeitherAppearNorSkewTheDistribution()
    {
        var random = new Random(7003);
        var subject = Random(random);
        var subjectId = await InsertAsync("subject", subject);
        var twin = await InsertAsync("twin", Around(subject, random, 0.1));
        for (int i = 0; i < 30; i++) await InsertAsync($"other {i}", Random(random));

        var before = _cache.GetOrRebuild().GetNeighbours(subjectId, 50, 2.0f);

        // A protected article stores an empty (but not null) projection; another one has none yet.
        var protectedId = await InsertAsync("protected", []);
        var pendingId = await InsertAsync("pending", null);
        for (int i = 0; i < 40; i++) await InsertAsync($"protected {i}", []);
        _cache.Invalidate();

        var after = _cache.GetOrRebuild().GetNeighbours(subjectId, 50, 2.0f);

        after.Select(n => n.Id).Should().NotContain([protectedId, pendingId]);
        after.Select(n => n.Id).Should().Equal(before.Select(n => n.Id));
        for (int i = 0; i < after.Count; i++)
            after[i].Z.Should().BeApproximately(before[i].Z, 1e-4f, "zero-norm slots are left out of the mean and the deviation");
        after[0].Id.Should().Be(twin);
    }

    [Fact]
    public async Task AnArticleWithoutAVectorHasNoNeighbours()
    {
        var random = new Random(7004);
        for (int i = 0; i < 20; i++) await InsertAsync($"other {i}", Random(random));
        var protectedId = await InsertAsync("protected", []);
        var pendingId = await InsertAsync("pending", null);

        var snapshot = _cache.GetOrRebuild();

        snapshot.GetNeighbours(protectedId, 50, 2.0f).Should().BeEmpty();
        snapshot.GetNeighbours(pendingId, 50, 2.0f).Should().BeEmpty();
        snapshot.GetNeighbours(Guid.NewGuid(), 50, 2.0f).Should().BeEmpty();
    }

    [Fact]
    public async Task ADeletedArticleIsNotAnArticleToCompareWith()
    {
        var random = new Random(7005);
        var subject = Random(random);
        var subjectId = await InsertAsync("subject", subject);
        var deletedTwin = await InsertAsync("deleted twin", Around(subject, random, 0.05), status: "D");
        for (int i = 0; i < 30; i++) await InsertAsync($"other {i}", Random(random));

        _cache.GetOrRebuild().GetNeighbours(subjectId, 50, 2.0f).Select(n => n.Id).Should().NotContain(deletedTwin);
    }

    [Fact]
    public async Task IdenticalVectorsHaveNothingStandingOut()
    {
        var same = Enumerable.Repeat(0.5f, Dim).ToArray();
        var first = await InsertAsync("a", same);
        for (int i = 0; i < 10; i++) await InsertAsync($"copy {i}", same);

        // Every similarity is exactly 1: the deviation is zero, so no Z exists and nothing is a neighbour.
        _cache.GetOrRebuild().GetNeighbours(first, 50, 2.0f).Should().BeEmpty();
    }

    [Fact]
    public async Task TooFewOtherArticlesToStandardiseAgainstGiveNothing()
    {
        var random = new Random(7006);
        var a = await InsertAsync("a", Random(random));
        await InsertAsync("b", Random(random));

        _cache.GetOrRebuild().GetNeighbours(a, 50, 2.0f).Should().BeEmpty();
    }

    [Fact]
    public void AnEmptyVaultHasNoNeighbours()
    {
        _cache.GetOrRebuild().GetNeighbours(Guid.NewGuid(), 50, 2.0f).Should().BeEmpty();
    }
}
