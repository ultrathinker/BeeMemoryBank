using System.Text.RegularExpressions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The model providers' code reaches a provider only through <c>ProviderEgress.SendAsync</c> (docs/ai-providers.md, the egress guard): no
/// protocol client, router or Dream model client creates an <c>HttpClient</c> or a handler of its own, asks a client factory for one, or
/// sends with an <c>HttpClient</c> method; the guard's own file is the one place that does. Every <c>IModelProtocolClient</c> lives in the
/// providers' folder and sends through the egress, and the Api takes no provider SDK as a package (an SDK brings its own HttpClient). This
/// test reads the sources: a new client either follows the rule or fails here.
/// </summary>
public class ProviderEgressArchitectureTests
{
    private const string ProvidersFolder = "server/BeeMemoryBank.Api/Services/Providers";
    private const string GuardFile = ProvidersFolder + "/ProviderEgress.cs";

    /// <summary>The other places that talk to a model through the providers' code (Dream's model client).</summary>
    private static readonly string[] AlsoChecked = ["server/BeeMemoryBank.Api/Dream/Model"];

    private static readonly (Regex Pattern, string What)[] Forbidden =
    [
        (new(@"\bnew\s+(?:System\.Net\.Http\.)?HttpClient\s*\("), "creates an HttpClient"),
        (new(@"\bnew\s+(?:System\.Net\.Http\.)?(?:SocketsHttpHandler|HttpClientHandler|HttpMessageInvoker)\b"), "creates a handler"),
        (new(@"\bIHttpClientFactory\b"), "takes a client factory"),
        (new(@"\.CreateClient\s*\("), "asks for a client"),
        (new(@"\bHttpClient\b"), "holds an HttpClient"),
        (new(@"(?<!egress)\.SendAsync\s*\("), "sends with something other than the egress"),
        (new(@"\.(?:GetStringAsync|GetStreamAsync|GetByteArrayAsync|PostAsJsonAsync|PutAsJsonAsync|PatchAsJsonAsync|GetFromJsonAsync)\s*\("),
            "sends with an HttpClient method"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    private static readonly Regex LineComment = new(@"(?<!:)//.*$", RegexOptions.Multiline);

    /// <summary>The code of a file without its comments (a remark that names HttpClient is not a use of it).</summary>
    private static string Code(string root, string relative) => LineComment.Replace(File.ReadAllText(Path.Combine(root, relative)), "");

    private static IEnumerable<string> Files(string root, string folder) =>
        Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(r => !r.Contains("/obj/") && !r.Contains("/bin/"));

    [Fact]
    public void NothingInTheProvidersCode_ReachesTheNetworkExceptThroughTheEgressGuard()
    {
        var root = RepoRoot();
        var files = Files(root, ProvidersFolder).Concat(AlsoChecked.SelectMany(f => Files(root, f))).Where(f => f != GuardFile).ToList();
        files.Should().Contain(ProvidersFolder + "/AnthropicMessagesClient.cs").And.Contain(ProvidersFolder + "/GeminiClient.cs")
            .And.Contain(ProvidersFolder + "/OpenAiChatClient.cs");

        var offences = (from file in files
                        let code = Code(root, file)
                        from rule in Forbidden
                        where rule.Pattern.IsMatch(code)
                        select $"{file} {rule.What}").ToList();
        offences.Should().BeEmpty("only ProviderEgress.cs opens connections to a provider; a protocol client sends with _egress.SendAsync");

        Code(root, GuardFile).Should().Contain("CreateClient(", "the guard itself is where the named clients are used");
    }

    [Fact]
    public void EveryProtocolClient_LivesWithTheProviders_AndSendsThroughTheEgress()
    {
        var root = RepoRoot();
        var implementing = Files(root, "server")
            .Where(f => Regex.IsMatch(Code(root, f), @"class\s+\w+\s*(?:\([^)]*\))?\s*:\s*(?:[\w.]+\s*,\s*)*IModelProtocolClient\b"))
            .ToList();

        implementing.Should().BeEquivalentTo(
            [ProvidersFolder + "/OpenAiChatClient.cs", ProvidersFolder + "/AnthropicMessagesClient.cs", ProvidersFolder + "/GeminiClient.cs"]);
        foreach (var file in implementing)
            Code(root, file).Should().Contain("_egress.SendAsync(", $"{file} sends through ProviderEgress");
    }

    [Fact]
    public void TheApiTakesNoProviderSdk()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "server/BeeMemoryBank.Api/BeeMemoryBank.Api.csproj"));
        Regex.Matches(csproj, "PackageReference\\s+Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value)
            .Should().NotContain(p => p.StartsWith("Anthropic", StringComparison.OrdinalIgnoreCase)
                                      || p.StartsWith("Google.GenAI", StringComparison.OrdinalIgnoreCase)
                                      || p.StartsWith("Google.Cloud.AIPlatform", StringComparison.OrdinalIgnoreCase)
                                      || p.StartsWith("OpenAI", StringComparison.OrdinalIgnoreCase)
                                      || p.StartsWith("Microsoft.Extensions.AI", StringComparison.OrdinalIgnoreCase),
                "a provider SDK brings its own HttpClient, past the egress guard");
    }
}
