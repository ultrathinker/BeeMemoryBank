using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// Where the copy keeps its files: one folder of its own in the app's container (Library/Application Support on the phone), the database
/// <c>beememorybank.db</c> in it - the name Blind.AppCore's wipe looks for. The iOS part (file protection, no device backup) is applied by
/// the platform code when the folder is made.
/// </summary>
public sealed class IosBlindPaths : IBlindPaths
{
    public const string FolderName = "BeeMemoryBankBlind";
    public const string DatabaseFileName = "beememorybank.db";

    public IosBlindPaths(string applicationSupport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationSupport);
        DataDirectory = Path.Combine(Path.GetFullPath(applicationSupport), FolderName);
        DatabasePath = Path.Combine(DataDirectory, DatabaseFileName);
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
}
