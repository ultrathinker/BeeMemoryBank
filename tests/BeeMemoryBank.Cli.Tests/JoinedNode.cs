using BeeMemoryBank.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// A node that has joined is encrypted since 2.6.2 (BMB-306): the tests of <c>bmb join</c> that look at what the join left open the folder with the master
/// password, the way every other verb does. The password goes through the static of <see cref="CliSealed"/>, so these classes share the collection
/// <c>CliSealedStatics</c> with the other tests that use it.
/// </summary>
internal static class JoinedNode
{
    public static async Task<ServiceProvider> OpenAsync(string dataPath, string password)
    {
        CliSealed.Error = new StringWriter();
        CliSealed.StdinIsRedirected = () => true;   // a wrong password is an error, never a prompt that waits for a person
        CliSealed.Set(password, false);
        return await CliServiceProvider.CreateAsync(dataPath);
    }

    public static void Reset()
    {
        CliSealed.Error = Console.Error;
        CliSealed.StdinIsRedirected = () => Console.IsInputRedirected;
        CliSealed.Set(null, false);
        SqliteConnection.ClearAllPools();
    }
}
