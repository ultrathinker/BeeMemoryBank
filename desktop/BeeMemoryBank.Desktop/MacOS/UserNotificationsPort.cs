using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.MacOS.Interop;
using BeeMemoryBank.Desktop.Services;
using BeeMemoryBank.Platforms.Apple.Interop;

namespace BeeMemoryBank.Desktop.MacOS;

/// <summary>
/// The calls into <c>UNUserNotificationCenter</c> (UserNotifications.framework) through the Objective-C runtime: permission, categories with
/// their buttons, a request with a title and a text, and the delegate that hears which button was chosen. It runs only on a Mac, and only in a
/// process run from a .app bundle: the notification centre raises an Objective-C exception for any other (an exception cannot be caught from
/// managed code), so <see cref="IsAvailable"/> looks at the main bundle first and every other member assumes it was true.
/// </summary>
internal sealed class UserNotificationsPort : IUserNotificationsPort
{
    private const string UserNotifications = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string DelegateClass = "BmbNotificationDelegate";

    // UNAuthorizationOptionAlert; UNNotificationActionOptionForeground; UNNotificationPresentationOptionList | ...Banner
    private const nuint AuthorizationAlert = 1 << 2;
    private const nuint ActionForeground = 1 << 2;
    private const nuint PresentBannerAndList = (1 << 3) | (1 << 4);

    private static readonly TimeSpan PostWait = TimeSpan.FromSeconds(10);

    // The delegate is one per process (the notification centre has one), so what it hears goes to a static event the port forwards.
    private static event Action<string, string>? Heard;

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate void DidReceive(IntPtr self, IntPtr command, IntPtr center, IntPtr response, IntPtr completion);

    private static readonly DidReceive DidReceiveBody = OnDidReceive;
    private static readonly DidReceive WillPresentBody = OnWillPresent;

    private readonly object _gate = new();
    private readonly HashSet<string> _categoryIds = [];
    private readonly List<IntPtr> _categories = [];
    private IntPtr _center;
    private IntPtr _delegateInstance;
    private bool _authorized;
    private bool? _available;

    public UserNotificationsPort() => Heard += Forward;

    public event Action<string, string>? ActionChosen;

    private void Forward(string requestId, string action) => ActionChosen?.Invoke(requestId, action);

    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return false;
            lock (_gate) return _available ??= Probe();
        }
    }

    /// <summary>The main bundle is a .app with an identifier, and the framework has its class. Nothing is sent to the notification centre yet.</summary>
    private static bool Probe()
    {
        try
        {
            ObjC.LoadFramework(Foundation);
            using var pool = AutoreleasePool.Push();
            var bundle = ObjC.Send(ObjC.Class("NSBundle"), ObjC.Sel("mainBundle"));
            if (bundle == IntPtr.Zero) return false;
            var path = ObjC.ToManaged(ObjC.Send(bundle, ObjC.Sel("bundlePath")));
            var identifier = ObjC.ToManaged(ObjC.Send(bundle, ObjC.Sel("bundleIdentifier")));
            if (path is null || !path.EndsWith(".app", StringComparison.Ordinal) || string.IsNullOrEmpty(identifier)) return false;
            return ObjC.LoadFramework(UserNotifications) && ObjC.Class("UNUserNotificationCenter") != IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>The notification centre, with our delegate set (before anything is requested, so no answer is lost). Zero when it cannot be had.</summary>
    private IntPtr Center()
    {
        lock (_gate)
        {
            if (_center != IntPtr.Zero) return _center;
            if (!IsAvailable) return IntPtr.Zero;
            using var pool = AutoreleasePool.Push();
            var center = ObjC.Send(ObjC.Class("UNUserNotificationCenter"), ObjC.Sel("currentNotificationCenter"));
            if (center == IntPtr.Zero) return IntPtr.Zero;
            _delegateInstance = MakeDelegate();
            if (_delegateInstance != IntPtr.Zero) ObjC.Send(center, ObjC.Sel("setDelegate:"), _delegateInstance);
            _center = center;
            return center;
        }
    }

    public async Task<bool> RequestAuthorizationAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (_authorized) return true;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Off the caller's thread: the first call may be the one that loads the frameworks.
        await Task.Run(() =>
        {
            var center = Center();
            if (center == IntPtr.Zero) { completed.TrySetResult(false); return; }
            using var pool = AutoreleasePool.Push();
            var block = ObjCBlocks.Create((granted, _) => completed.TrySetResult(granted));
            ObjC.Send(center, ObjC.Sel("requestAuthorizationWithOptions:completionHandler:"), AuthorizationAlert, block);
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            // Called again, the system answers at once with what the person said the first time.
            _authorized = await completed.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            return _authorized;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task<bool> PostAsync(string requestId, string title, string body, string categoryId, IReadOnlyList<NoticeAction> actions, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(() =>
        {
            var center = Center();
            if (center == IntPtr.Zero) { completed.TrySetResult(false); return; }
            using var pool = AutoreleasePool.Push();
            using var scope = new CfScope();

            EnsureCategory(center, categoryId, actions, scope);

            var content = ObjC.Send(ObjC.Send(ObjC.Class("UNMutableNotificationContent"), ObjC.Sel("alloc")), ObjC.Sel("init"));
            try
            {
                ObjC.Send(content, ObjC.Sel("setTitle:"), scope.NewString(NonEmpty(title)));
                ObjC.Send(content, ObjC.Sel("setBody:"), scope.NewString(NonEmpty(body)));
                ObjC.Send(content, ObjC.Sel("setCategoryIdentifier:"), scope.NewString(categoryId));
                // No trigger: the notice is delivered at once.
                var request = ObjC.Send(ObjC.Class("UNNotificationRequest"), ObjC.Sel("requestWithIdentifier:content:trigger:"),
                    scope.NewString(requestId), content, IntPtr.Zero);
                var block = ObjCBlocks.Create(error => completed.TrySetResult(error == IntPtr.Zero));
                ObjC.Send(center, ObjC.Sel("addNotificationRequest:withCompletionHandler:"), request, block);
            }
            finally
            {
                ObjC.Send(content, ObjC.Sel("release"));
            }
        }, cancellationToken).ConfigureAwait(false);

        try { return await completed.Task.WaitAsync(PostWait, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Registers the buttons of a category with the centre the first time it is used (the centre keeps all of them, so the set grows).</summary>
    private void EnsureCategory(IntPtr center, string categoryId, IReadOnlyList<NoticeAction> actions, CfScope scope)
    {
        lock (_gate)
        {
            if (!_categoryIds.Add(categoryId)) return;
            var buttons = new List<IntPtr>();
            foreach (var action in actions)
                buttons.Add(ObjC.Send(ObjC.Class("UNNotificationAction"), ObjC.Sel("actionWithIdentifier:title:options:"),
                    scope.NewString(action.Id), scope.NewString(NonEmpty(action.Title)), action.OpensApp ? ActionForeground : (nuint)0));
            var category = ObjC.Send(ObjC.Class("UNNotificationCategory"), ObjC.Sel("categoryWithIdentifier:actions:intentIdentifiers:options:"),
                scope.NewString(categoryId), scope.NewArray(buttons.ToArray()), scope.NewArray(), (nuint)0);
            // Ours for good: the pool of this call releases what the factory methods returned.
            _categories.Add(ObjC.Send(category, ObjC.Sel("retain")));
            var set = ObjC.Send(ObjC.Class("NSSet"), ObjC.Sel("setWithArray:"), scope.NewArray(_categories.ToArray()));
            ObjC.Send(center, ObjC.Sel("setNotificationCategories:"), set);
        }
    }

    private static string NonEmpty(string text) => string.IsNullOrEmpty(text) ? " " : text;

    // ── the delegate ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The delegate object: a class of our own with the two methods of <c>UNUserNotificationCenterDelegate</c> the shell needs.</summary>
    internal static IntPtr MakeDelegate()
    {
        if (ObjC.Class(DelegateClass) == IntPtr.Zero)
        {
            var cls = ObjCClass.Create(DelegateClass, "UNUserNotificationCenterDelegate");
            if (cls is null) return IntPtr.Zero;
            // v = void, @ = id, : = SEL, @? = block
            cls.AddMethod("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:", "v@:@@@?", DidReceiveBody);
            cls.AddMethod("userNotificationCenter:willPresentNotification:withCompletionHandler:", "v@:@@@?", WillPresentBody);
            cls.Register();
        }
        return ObjC.Send(ObjC.Send(ObjC.Class(DelegateClass), ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    /// <summary>The person chose a button (or clicked the notice, or dismissed it).</summary>
    private static void OnDidReceive(IntPtr self, IntPtr command, IntPtr center, IntPtr response, IntPtr completion)
    {
        try
        {
            var action = ObjC.ToManaged(ObjC.Send(response, ObjC.Sel("actionIdentifier")));
            var request = ObjC.Send(ObjC.Send(response, ObjC.Sel("notification")), ObjC.Sel("request"));
            var id = ObjC.ToManaged(ObjC.Send(request, ObjC.Sel("identifier")));
            // Off the system's thread: the handler may post to the node.
            if (action != null && id != null) _ = Task.Run(() => Heard?.Invoke(id, action));
        }
        catch (Exception)
        {
            // An exception must not unwind into the system's code.
        }
        finally
        {
            try { ObjCBlocks.Invoke(completion); } catch (Exception) { /* the same */ }
        }
    }

    /// <summary>A notice that arrives while the app is the front one is shown as a banner all the same.</summary>
    private static void OnWillPresent(IntPtr self, IntPtr command, IntPtr center, IntPtr notification, IntPtr completion)
    {
        try { ObjCBlocks.Invoke(completion, PresentBannerAndList); } catch (Exception) { /* the same */ }
    }
}
