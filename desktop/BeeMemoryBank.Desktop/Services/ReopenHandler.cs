using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// The system's "reopen" request (macOS: a click on the Dock icon, or opening the app again from Finder, while it already runs) shows the
/// main window. The window is usually hidden: closing it only hides it, and a menu-bar app starts without one. Without this the click
/// answered nothing.
/// </summary>
public static class ReopenHandler
{
    /// <summary>Shows the window for a reopen request and for nothing else.</summary>
    public static void Handle(ActivationKind kind, Action showWindow)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        if (kind == ActivationKind.Reopen) showWindow();
    }

    /// <summary>
    /// Subscribes to the application's activation events. Where the platform has none (the lifetime does not offer the feature) nothing is
    /// subscribed. <paramref name="showWindow"/> runs on the thread the event comes on (the UI thread).
    /// </summary>
    public static void Attach(Application application, Action showWindow)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (application.TryGetFeature<IActivatableLifetime>() is { } lifetime)
            lifetime.Activated += (_, e) => Handle(e.Kind, showWindow);
    }
}
