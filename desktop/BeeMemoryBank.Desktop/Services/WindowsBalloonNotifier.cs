using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// The Windows notice outside the app's windows: a balloon from a temporary notification-area icon (<c>Shell_NotifyIcon</c> with
/// <c>NIF_INFO</c>), which Windows 10 and 11 show as a toast in the Action Center. It was the "about to sleep" notice of
/// <see cref="PowerEventsService"/>, which keeps using it; it is a notifier of its own so the blind-node alarms can use it too (BMB-77).
///
/// <para>The icon belongs to a window: the power-broadcast window of <see cref="PowerEventsService"/> when it passes one, else a hidden
/// window of the notifier's own, made on its own thread the first time something is shown. The icon is removed again
/// <see cref="DefaultShownFor"/> after the last notice. There is no click action and no registered app id (a real WinRT toast needs
/// both, a larger piece). Presentation only: it never throws, and off Windows it does nothing.</para>
/// </summary>
public sealed class WindowsBalloonNotifier : IUserNotifier, IDisposable
{
    /// <summary>The longest title: <c>szInfoTitle</c> holds 64 UTF-16 units with the terminating NUL.</summary>
    public const int TitleMax = 63;

    /// <summary>The longest message: <c>szInfo</c> holds 256 UTF-16 units with the terminating NUL.</summary>
    public const int MessageMax = 255;

    /// <summary>How long the temporary icon stays after the last notice.</summary>
    public static readonly TimeSpan DefaultShownFor = TimeSpan.FromSeconds(10);

    /// <summary>The calls the notifier makes on the shell; the real one is <c>Shell_NotifyIcon</c>, tests record them.</summary>
    internal interface IBalloonShell
    {
        /// <summary>Shows the balloon on the icon (<paramref name="iconShown"/>: the icon is there already, so it is modified, not added).</summary>
        bool Show(IntPtr window, int iconId, string title, string message, bool iconShown);

        void Remove(IntPtr window, int iconId);
    }

    private readonly Func<IntPtr> _window;
    private readonly OwnWindow? _ownWindow;
    private readonly IBalloonShell _shell;
    private readonly int _iconId;
    private readonly TimeSpan _shownFor;
    private readonly object _gate = new();
    private IntPtr _iconWindow;
    private long _generation;
    private bool _disposed;

    /// <summary>A notifier with a hidden window of its own (the shell's notifier, <see cref="IShellPlatform.CreateNotifier"/>).</summary>
    public WindowsBalloonNotifier() : this(null, 1002) { }

    /// <param name="ownerWindow">The window the icon belongs to, read at each notice; <see cref="IntPtr.Zero"/> means none yet, and then nothing
    /// is shown. Null means a hidden window of the notifier's own.</param>
    /// <param name="iconId">The icon's id under that window.</param>
    public WindowsBalloonNotifier(Func<IntPtr>? ownerWindow, int iconId)
        : this(ownerWindow, iconId, new NativeShell(), DefaultShownFor) { }

    internal WindowsBalloonNotifier(Func<IntPtr>? ownerWindow, int iconId, IBalloonShell shell, TimeSpan shownFor)
    {
        if (ownerWindow is null)
        {
            _ownWindow = new OwnWindow();
            _window = () => _ownWindow.Handle;
        }
        else
        {
            _window = ownerWindow;
        }
        _shell = shell;
        _iconId = iconId;
        _shownFor = shownFor;
    }

    /// <summary>The title and the message cut to what the balloon's fields hold, with "..." where something was cut.</summary>
    public static (string Title, string Message) Fit(string? title, string? message) => (Cut(title, TitleMax), Cut(message, MessageMax));

    private static string Cut(string? text, int max)
    {
        // A control character has no place in a one-line balloon field: a space instead.
        var clean = new string((text ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (clean.Length <= max) return clean;
        var keep = max - 3;
        if (char.IsHighSurrogate(clean[keep - 1])) keep--; // never cut a surrogate pair in half
        return clean[..keep] + "...";
    }

    public void Notify(string title, string message)
    {
        try
        {
            // Before the window is asked for: a notice after Dispose must not make a window of its own that nobody closes.
            lock (_gate) if (_disposed) return;
            var window = _window();
            if (window == IntPtr.Zero) return;
            var (fittedTitle, fittedMessage) = Fit(title, message);

            long generation;
            lock (_gate)
            {
                if (_disposed) return;
                var iconShown = _iconWindow == window;
                if (!_shell.Show(window, _iconId, fittedTitle, fittedMessage, iconShown))
                {
                    Console.Error.WriteLine("[WindowsBalloonNotifier] The notification could not be shown.");
                    return;
                }
                _iconWindow = window;
                generation = ++_generation;
            }
            _ = RemoveLaterAsync(generation);
        }
        catch (Exception ex)
        {
            // The type only: the text of a notice is not repeated into a log.
            Console.Error.WriteLine($"[WindowsBalloonNotifier] The notification failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>Removes the temporary icon <see cref="_shownFor"/> after the last notice (a later notice moves the removal on).</summary>
    private async Task RemoveLaterAsync(long generation)
    {
        try
        {
            await Task.Delay(_shownFor).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation || _iconWindow == IntPtr.Zero) return;
                RemoveIcon();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WindowsBalloonNotifier] Removing the notification icon failed ({ex.GetType().Name}).");
        }
    }

    private void RemoveIcon()
    {
        var window = _iconWindow;
        _iconWindow = IntPtr.Zero;
        _shell.Remove(window, _iconId);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_iconWindow != IntPtr.Zero) RemoveIcon();
            }
            catch { /* the icon goes with its window anyway */ }
        }
        _ownWindow?.Dispose();
    }

    // ── Shell_NotifyIcon ────────────────────────────────────────────────────────────────────────────────

    internal sealed class NativeShell : IBalloonShell
    {
        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_INFO = 0x00000010;
        private const int NIIF_WARNING = 0x00000002;
        private const int IDI_INFORMATION = 32516;

        // One handle for the life of the process: the icon the balloon hangs from.
        private static readonly Lazy<IntPtr> AppIcon = new(LoadAppIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string file, int index);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr name);

        /// <summary>The program's own icon, else the system's information icon (shared, never destroyed). Zero if neither can be had.</summary>
        private static IntPtr LoadAppIcon()
        {
            try
            {
                var path = Environment.ProcessPath;
                // ExtractIcon answers 0 for "no icon" and 1 for "not a file with icons".
                if (!string.IsNullOrEmpty(path) && ExtractIcon(IntPtr.Zero, path, 0) is var own && own.ToInt64() > 1) return own;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* the stock icon below */ }
            return LoadIcon(IntPtr.Zero, (IntPtr)IDI_INFORMATION);
        }

        /// <summary>
        /// The flags of a balloon. Windows 10 and 11 turn a balloon into a toast only when its icon is shown, and an icon with no image
        /// (no <c>NIF_ICON</c>) is not: the call succeeds and nothing appears. That was the state of the "about to sleep" notice before.
        /// </summary>
        internal static int FlagsFor(IntPtr icon) => NIF_INFO | NIF_TIP | (icon != IntPtr.Zero ? NIF_ICON : 0);

        internal static IntPtr ApplicationIcon() => AppIcon.Value;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        public bool Show(IntPtr window, int iconId, string title, string message, bool iconShown)
        {
            if (!OperatingSystem.IsWindows()) return false;
            var icon = AppIcon.Value;
            var nid = new NOTIFYICONDATA
            {
                hWnd = window,
                uID = iconId,
                uFlags = FlagsFor(icon),
                hIcon = icon,
                szTip = "BeeMemoryBank",
                szInfo = message,
                szInfoTitle = title,
                dwInfoFlags = NIIF_WARNING,
                uTimeoutOrVersion = 10000
            };
            nid.cbSize = Marshal.SizeOf(nid);
            // The icon may still be there from a notice a moment ago (then it is modified), or gone with an Explorer restart (then added).
            return iconShown
                ? Shell_NotifyIcon(NIM_MODIFY, ref nid) || Shell_NotifyIcon(NIM_ADD, ref nid)
                : Shell_NotifyIcon(NIM_ADD, ref nid) || Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }

        public void Remove(IntPtr window, int iconId)
        {
            if (!OperatingSystem.IsWindows()) return;
            var nid = new NOTIFYICONDATA { hWnd = window, uID = iconId };
            nid.cbSize = Marshal.SizeOf(nid);
            Shell_NotifyIcon(NIM_DELETE, ref nid);
        }
    }

    // ── a hidden window of its own ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A hidden top-level window (not shown, not message-only: the same kind <see cref="PowerEventsService"/> uses) on a background thread
    /// with its own message loop, made on first use. Off Windows its handle is always <see cref="IntPtr.Zero"/>.
    /// </summary>
    private sealed class OwnWindow : IDisposable
    {
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_DESTROY = 0x0002;

        private readonly object _gate = new();
        private readonly ManualResetEventSlim _ready = new();
        private Thread? _thread;
        private IntPtr _hwnd;
        private WndProc? _wndProc;

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
            public uint lPrivate;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        public IntPtr Handle
        {
            get
            {
                if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
                lock (_gate)
                {
                    if (_thread == null)
                    {
                        _thread = new Thread(Run) { IsBackground = true, Name = "BalloonNotifierWindowThread" };
                        _thread.SetApartmentState(ApartmentState.STA);
                        _thread.Start();
                    }
                }
                _ready.Wait(TimeSpan.FromSeconds(2));
                return _hwnd;
            }
        }

        private void Run()
        {
            var className = $"BmbNotifierClass_{Guid.NewGuid():N}";
            var hInst = GetModuleHandle(null);
            try
            {
                _wndProc = (hWnd, msg, wParam, lParam) =>
                {
                    if (msg == WM_DESTROY) PostQuitMessage(0);
                    return DefWindowProc(hWnd, msg, wParam, lParam);
                };
                var wndClass = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = _wndProc,
                    hInstance = hInst,
                    lpszMenuName = "",
                    lpszClassName = className,
                };
                if (RegisterClassEx(ref wndClass) == 0)
                {
                    Console.Error.WriteLine($"[WindowsBalloonNotifier] Failed to register the window class ({Marshal.GetLastWin32Error()}).");
                    return;
                }
                _hwnd = CreateWindowEx(0, className, "BmbNotifierWindow", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    Console.Error.WriteLine($"[WindowsBalloonNotifier] Failed to create the hidden window ({Marshal.GetLastWin32Error()}).");
                    UnregisterClass(className, hInst);
                    return;
                }
            }
            finally
            {
                _ready.Set();
            }

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            _hwnd = IntPtr.Zero;
            UnregisterClass(className, hInst);
        }

        public void Dispose()
        {
            if (!OperatingSystem.IsWindows()) return;
            Thread? thread;
            lock (_gate) thread = _thread;
            if (thread == null) return;
            if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
