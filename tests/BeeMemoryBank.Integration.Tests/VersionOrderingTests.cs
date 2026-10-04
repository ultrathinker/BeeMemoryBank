using System.Reflection;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// 2.0.1 follows 1.0.17: the major number moves up while the patch number goes down. Every place that orders application versions must
/// still say 2.0.1 is newer (the update feed, the admin check) and that neither 2.0.1 nor 1.0.17 is "newer" than itself. Nothing on the
/// sync wire reads the application version (SyncProtocolPinTests).
/// </summary>
public class VersionOrderingTests
{
    private static T Call<T>(Type type, string name, params object[] args)
    {
        var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new InvalidOperationException(type.Name + "." + name + " not found");
        return (T)method.Invoke(null, args)!;
    }

    [Theory]
    [InlineData("2.0.1", "1.0.17", true)]
    [InlineData("1.0.17", "2.0.1", false)]
    [InlineData("2.0.1", "2.0.1", false)]
    [InlineData("2.0.2", "2.0.1", true)]
    [InlineData("2.1.0", "2.0.9", true)]
    [InlineData("1.0.18", "1.0.17", true)]
    public void UpdateService_OrdersTheApplicationVersions(string candidate, string current, bool newer) =>
        Call<bool>(typeof(UpdateService), "IsNewer", candidate, current).Should().Be(newer);

    [Theory]
    [InlineData("2.0.1", "1.0.17", 1)]
    [InlineData("1.0.17", "2.0.1", -1)]
    [InlineData("2.0.1", "2.0.1", 0)]
    [InlineData("v2.0.1", "2.0.1", 0)]
    [InlineData("2.0.1+abc", "2.0.1", 0)]
    public void AdminEndpoints_OrdersTheApplicationVersions(string a, string b, int sign) =>
        Math.Sign(Call<int>(typeof(AdminEndpoints), "CompareVersions", a, b)).Should().Be(sign);

    [Fact]
    public void TheBuildsReportTheVersionOfTheVersionFile()
    {
        var version = File.ReadAllText(Path.Combine(DiSnapshot.RepoRoot(), "VERSION")).Trim();
        version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        var informational = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        informational.Should().StartWith(version, "the Api assembly is stamped from VERSION by Directory.Build.props");
        // The shared libraries and the vault carry the same stamp: one version for the whole product.
        foreach (var assembly in new[]
                 {
                     typeof(BeeMemoryBank.Core.Models.NodeIdentity).Assembly, typeof(BeeMemoryBank.Sync.EventApplier).Assembly,
                     typeof(BeeMemoryBank.Core.Services.SessionService).Assembly
                 })
            (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "").Should().StartWith(version, assembly.GetName().Name);
    }
}
