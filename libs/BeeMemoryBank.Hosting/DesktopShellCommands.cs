namespace BeeMemoryBank.Hosting;

/// <summary>
/// Addresses a page served by the Web host navigates to when it needs the Windows app to do something a
/// browser cannot — show a native picker. The Desktop shell cancels the navigation and acts; the .invalid
/// TLD can never resolve, so outside the app nothing is reached. One definition for both sides, so the
/// page's button and the shell's handler cannot drift apart.
/// </summary>
public static class DesktopShellCommands
{
    /// <summary>
    /// How the app shell's web view names itself in its user agent. The Windows app is recognised by
    /// <c>window.chrome.webview</c> (WebView2); WKWebView, which the Mac app hosts, has no such object, so the Mac
    /// shell appends this to its user agent and the Setup page looks for it. A browser, and Docker, never carry it.
    /// </summary>
    public const string UserAgentToken = "BeeMemoryBankDesktop";

    /// <summary>"Open an existing profile": the native folder picker.</summary>
    public const string OpenExistingProfile = "https://bmb-desktop.invalid/open-existing-profile";

    /// <summary>The restore form's "Choose…": the native file picker for an Android blind node's backup file.</summary>
    public const string PickBackupFile = "https://bmb-desktop.invalid/pick-backup-file";
}
