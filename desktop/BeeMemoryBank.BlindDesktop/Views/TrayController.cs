using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace BeeMemoryBank.BlindDesktop.Views;

/// <summary>The tray icon and its menu: Open, Sync now, Back up now, Re-pair, Settings, Disconnect and wipe, Quit. A left click opens the window.</summary>
public sealed class TrayController : IDisposable
{
    private readonly TrayIcon _icon;

    /// <param name="iconAsset">The <c>avares://</c> resource of the icon; the platform chooses it (a monochrome template image in the macOS menu bar).</param>
    /// <param name="isTemplate">The icon is a monochrome template image: macOS then tints it for the light and dark menu bar (without the flag it draws the black pixels as they are).</param>
    public TrayController(Application application, ITrayActions actions, string iconAsset = "avares://BeeMemoryBank.BlindDesktop/Assets/icon.png", bool isTemplate = false)
    {
        using var stream = AssetLoader.Open(new Uri(iconAsset));
        _icon = new TrayIcon
        {
            ToolTipText = "Bee Memory Bank - blind copy",
            Icon = new WindowIcon(stream),
            Menu = BuildMenu(actions),
        };
        if (isTemplate) MacOSProperties.SetIsTemplateIcon(_icon, true);
        _icon.Clicked += (_, _) => Guarded(actions.Open);
        TrayIcon.SetIcons(application, new TrayIcons { _icon });
    }

    /// <summary>A menu click or a click on the icon runs on the UI thread: a failure of the action is written to the error log, it does not end the app.</summary>
    private static void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("A tray action failed", ex);
        }
    }

    public void SetToolTip(string text) => _icon.ToolTipText = text.Length <= 120 ? text : text[..120];

    public void Dispose() => _icon.Dispose();

    private static NativeMenu BuildMenu(ITrayActions actions)
    {
        var menu = new NativeMenu();
        void Add(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => Guarded(action);
            menu.Items.Add(item);
        }

        Add("Open", actions.Open);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add("Sync now", actions.SyncNow);
        Add("Back up now", actions.BackupNow);
        Add("Re-pair...", actions.RePair);
        Add("Settings...", actions.OpenSettings);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add("Disconnect and wipe...", actions.Wipe);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add("Quit", actions.Quit);
        return menu;
    }
}
