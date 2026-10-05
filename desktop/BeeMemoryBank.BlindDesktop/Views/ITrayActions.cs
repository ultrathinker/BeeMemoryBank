namespace BeeMemoryBank.BlindDesktop.Views;

/// <summary>What the tray menu can do; the shell implements it.</summary>
public interface ITrayActions
{
    void Open();
    void SyncNow();
    void BackupNow();
    void RePair();
    void OpenSettings();
    void Wipe();
    void Quit();
}
