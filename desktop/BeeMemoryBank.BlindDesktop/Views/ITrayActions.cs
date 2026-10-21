namespace BeeMemoryBank.BlindDesktop.Views;

/// <summary>What the tray menu can do; the shell implements it.</summary>
public interface ITrayActions
{
    void Open();

    /// <summary>Asks every active account for a sync round (the device loop runs them one at a time).</summary>
    void SyncNow();

    /// <summary>Asks every active account for a backup.</summary>
    void BackupNow();

    /// <summary>Opens the window on the Add account page.</summary>
    void AddAccount();

    void OpenSettings();
    void Quit();
}
