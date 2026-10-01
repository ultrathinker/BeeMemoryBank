using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Tests
{
    /// <summary>
    /// The scanner behind the boundary guard has to fail for each way a type can get hold of a
    /// repository, or the guard proves nothing. Each fixture below is one way; <c>Clean</c> is the
    /// control that must stay unreported.
    /// </summary>
    public class BoundaryScannerTests
    {
        private static IReadOnlyList<BoundaryScanner.Finding> ScanFixtures() => BoundaryScanner.Scan(
            typeof(BoundaryScannerTests).Assembly.Location,
            owner => owner.StartsWith("BeeMemoryBank.BlindMobile.Tests.BoundaryFixtures.", StringComparison.Ordinal),
            ReceiveOnlyTypes.RestrictedNames);

        [Theory]
        [InlineData("CtorParameter", "constructor parameter")]
        [InlineData("HeldField", "field")]
        [InlineData("LazyParameter", "constructor parameter")]
        [InlineData("ReturnsRepository", "method signature")]
        [InlineData("ResolvesFromLocator", "method body")]
        [InlineData("TypeofRepository", "method body")]
        [InlineData("NewsConcreteRepository", "method body")]
        [InlineData("ResolvesInALambda", "method body")]
        public void EveryWayOfHoldingARepository_IsReported(string fixtureName, string where)
        {
            var findings = ScanFixtures().Where(f => f.Owner.Contains("." + fixtureName)).ToList();

            findings.Should().NotBeEmpty($"{fixtureName} reaches a restricted type and the scanner must say so");
            findings.Should().Contain(f => f.Where == where);
        }

        [Fact]
        public void ACleanTypeAndACommentNamingARepository_AreNotReported()
        {
            ScanFixtures().Should().NotContain(f => f.Owner.Contains("." + "Clean"));
        }
    }
}

namespace BeeMemoryBank.BlindMobile.Tests.BoundaryFixtures
{
    // Throw-away shapes for the scanner's own tests; nothing instantiates them.
    internal sealed class CtorParameter(IArticleRepository repository) { private readonly object _ = repository; }

    internal sealed class HeldField { public IBlobRepository? Blobs; }

    internal sealed class LazyParameter(Lazy<IFolderRepository> folders) { private readonly object _ = folders; }

    internal sealed class ReturnsRepository { public IEnumerable<IMediaRepository> List() => []; }

    internal sealed class ResolvesFromLocator(IServiceProvider services)
    {
        public object? Resolve() => services.GetRequiredService<IArticleBodyRepository>();
    }

    internal sealed class TypeofRepository { public Type Describe() => typeof(ICommentRepository); }

    internal sealed class NewsConcreteRepository
    {
        public object Make(DbConnectionFactory factory) => new TombstoneRepository(factory);
    }

    internal sealed class ResolvesInALambda(IServiceProvider services)
    {
        public Func<object?> Later() => () => services.GetService<IConceptTagRepository>();
    }

    // IArticleRepository IBlobRepository ArticleRepository: a comment that names the types is not a use.
    internal sealed class Clean(IWhitelistRepository whitelist)
    {
        public object Held => whitelist;
    }
}
