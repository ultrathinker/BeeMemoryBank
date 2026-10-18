using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// A plain, initialized node folder for the tests of the other verbs. <c>bmb init</c> sets a node up ENCRYPTED since 2.6.2 (BMB-306), so a test that
/// needs the folder a node of the first months had - a plain database - writes it with the initialization service, which is all <c>bmb init</c> did then.
/// The tests of <c>bmb init</c> itself are in <c>InitSealedTests</c>.
/// </summary>
internal static class PlainNode
{
    public static async Task<int> InitAsync(string dataPath, string name, string password, TextWriter? output = null)
    {
        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var init = scope.ServiceProvider.GetRequiredService<InitializationService>();
        if (await init.IsInitializedAsync())
        {
            if (output is not null) await output.WriteLineAsync("Error: node is already initialized.");
            return 1;
        }
        await init.InitializeAsync(name, name, password);
        return 0;
    }
}
