using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Service to monitor Windows power events (specifically system sleep)
/// using a hidden native message window and a dedicated background message pump.
/// </summary>
public sealed class PowerEventsService : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMSUSPEND = 0x0004;
    private const uint WM_CLOSE = 0x0010;

    private readonly Action _onSleep;
    private Thread? _messageThread;
    private CancellationTokenSource? _cts;
    private IntPtr _hwnd;
    private string? _className;
    private WndProc? _wndProcDelegate;

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
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public PowerEventsService(Action onSleep)
    {
        _onSleep = onSleep ?? throw new ArgumentNullException(nameof(onSleep));
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("[PowerEventsService] Unsupported platform. Windows only.");
            return;
        }

        if (_messageThread != null) return;

        _cts = new CancellationTokenSource();
        _messageThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "PowerEventsMonitorThread"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    private void RunMessageLoop()
    {
        _className = $"BmbPowerMonitorClass_{Guid.NewGuid():N}";
        var hInst = GetModuleHandle(null);

        // Keep delegate alive
        _wndProcDelegate = CustomWndProc;

        var wndClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = _wndProcDelegate,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInst,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = "",
            lpszClassName = _className,
            hIconSm = IntPtr.Zero
        };

        if (RegisterClassEx(ref wndClass) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"[PowerEventsService] Failed to register window class. Error: {err}");
            return;
        }

        // Create a standard window, but without WS_VISIBLE, so it is hidden.
        // We do NOT use HWND_MESSAGE (-3) because message-only windows do not receive broadcast messages (WM_POWERBROADCAST).
        _hwnd = CreateWindowEx(
            0,
            _className,
            "BmbPowerMonitorWindow",
            0, // Hidden window
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInst,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"[PowerEventsService] Failed to create hidden power monitor window. Error: {err}");
            UnregisterClass(_className, hInst);
            return;
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (_cts?.IsCancellationRequested == true)
            {
                break;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClass(_className, hInst);
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_POWERBROADCAST)
        {
            if ((int)wParam == PBT_APMSUSPEND)
            {
                Console.WriteLine("[PowerEventsService] System is going to sleep (PBT_APMSUSPEND)!");
                try
                {
                    _onSleep();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PowerEventsService] Error invoking sleep callback: {ex.Message}");
                }
            }
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        if (_messageThread != null && _messageThread.IsAlive)
        {
            _messageThread.Join(TimeSpan.FromSeconds(2));
        }

        _cts?.Dispose();
    }
}
