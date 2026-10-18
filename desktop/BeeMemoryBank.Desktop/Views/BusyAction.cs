using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>What a button's work is called and what is said when it ends (BMB-311).</summary>
public sealed class BusyOptions
{
    /// <summary>
    /// A long operation (creating or moving a profile, a re-key, an update ...): a modal pop-up with a loader and "please wait" covers the window
    /// until the work ends, so nothing can be clicked a second time. A short one only disables its button and shows a loader inside it.
    /// </summary>
    public bool Long { get; init; }

    public string Title { get; init; } = "Working...";

    public string Message { get; init; } = "Please wait. This can take a while. Do not close the app.";

    /// <summary>Said in a message dialog when the work ends well; nothing is said when it is null.</summary>
    public string? Success { get; init; }

    /// <summary>The headline of the dialog that shows a failure (the whole error text is its body).</summary>
    public string FailureTitle { get; init; } = "That did not work";

    /// <summary>
    /// Where the result goes when the window has a place for it next to the button (an error line): called with (ok, text) instead of the
    /// message dialog. The text of a failure is the whole error.
    /// </summary>
    public Action<bool, string>? Result { get; init; }

    /// <summary>For a button that is to stay off when the work ends well (a one-way step): it is not switched back on.</summary>
    public bool KeepDisabled { get; init; }

    /// <summary>Other controls of the window that are off while the work runs (a Cancel button, a field).</summary>
    public IReadOnlyList<Control> AlsoDisable { get; init; } = [];
}

/// <summary>
/// THE one way a button of the desktop shell runs work that waits for something (BMB-311): a second click while it runs is ignored, the button
/// is disabled at once and shows a loader, a long operation puts up a modal "please wait", and the result - or the full error - is shown in
/// a message dialog (the shell's own place for a result: a row of a list has no room for a line of text).
/// A click handler that bypasses it fails BusyHelperGuardTests unless it says why with a <c>busy-exempt:</c> comment.
/// </summary>
public static class BusyAction
{
    // The buttons (or, without one, the work) in flight. UI thread only.
    private static readonly HashSet<object> Running = [];

    /// <summary>Runs <paramref name="work"/> behind <paramref name="button"/>. True when it ended well; false when it failed, was cancelled, or was already running.</summary>
    public static async Task<bool> RunAsync(Window owner, Button? button, Func<Task> work, BusyOptions? options = null)
    {
        options ??= new BusyOptions();
        object key = (object?)button ?? work.Method;
        if (!Running.Add(key)) return false;   // a second click: nothing starts

        var original = button?.Content;
        var wasEnabled = button?.IsEnabled ?? true;
        var others = new List<Control>();
        foreach (var other in options.AlsoDisable) if (other.IsEnabled) others.Add(other);
        BusyDialog? modal = null;
        Control? loader = null;
        var ended = false;
        try
        {
            if (button != null)
            {
                button.IsEnabled = false;
                button.Content = loader = Loader(original);
            }
            foreach (var other in others) other.IsEnabled = false;
            if (options.Long)
            {
                modal = new BusyDialog(options.Title, options.Message);
                _ = modal.ShowDialog(owner);   // the work below runs while it is up; Release() closes it
            }

            await work();
            modal?.Release();
            modal = null;

            ended = true;
            if (options.Success is { } done) await ReportAsync(owner, options, ok: true, done);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;   // stopped on purpose: nothing to report
        }
        catch (Exception ex)
        {
            modal?.Release();   // before the dialog, or the dialog would sit under the pop-up
            modal = null;
            await ReportAsync(owner, options, ok: false, Describe(ex));
            return false;
        }
        finally
        {
            modal?.Release();
            if (button != null)
            {
                // What the work itself put on the button (a new caption, "Try again") is not overwritten.
                if (ReferenceEquals(button.Content, loader)) button.Content = original;
                if (!button.IsEnabled && !(ended && options.KeepDisabled)) button.IsEnabled = wasEnabled;
            }
            foreach (var other in others) other.IsEnabled = true;
            Running.Remove(key);
        }
    }

    /// <summary>True while <paramref name="button"/> has work in flight.</summary>
    public static bool IsRunning(Button button) => Running.Contains(button);

    /// <summary>The message of an exception and of everything inside it, so the whole story is on the screen.</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        Collect(ex, parts);
        return parts.Count == 0 ? ex.GetType().Name : string.Join(" ", parts);
    }

    private static void Collect(Exception e, List<string> parts)
    {
        // An aggregate says "One or more errors occurred": what it holds is the news.
        if (e is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions) Collect(inner, parts);
            return;
        }
        if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message)) parts.Add(e.Message);
        if (e.InnerException != null) Collect(e.InnerException, parts);
    }

    private static Task ReportAsync(Window owner, BusyOptions options, bool ok, string text)
    {
        if (options.Result is { } inline)
        {
            inline(ok, text);
            return Task.CompletedTask;
        }
        return new MessageDialog(ok ? options.Title : options.FailureTitle, text).ShowDialog(owner);
    }

    /// <summary>What the button shows while it works: a loader and its own words.</summary>
    private static Control Loader(object? original) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new ProgressBar { IsIndeterminate = true, Width = 40, Height = 4, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = original as string ?? "Working...", VerticalAlignment = VerticalAlignment.Center },
        },
    };

    /// <summary>The pop-up of a long operation: a loader and a line, no way out but the end of the work.</summary>
    private sealed class BusyDialog : Window
    {
        private bool _released;

        public BusyDialog(string title, string message)
        {
            Title = "Bee Memory Bank";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SystemDecorations = Avalonia.Controls.WindowDecorations.BorderOnly;
            Background = new SolidColorBrush(Color.Parse("#111119"));
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = message, FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#D1D5DB")), TextWrapping = TextWrapping.Wrap },
                    new ProgressBar { IsIndeterminate = true },
                },
            };
            Closing += (_, e) => { if (!_released) e.Cancel = true; };   // not Alt+F4, not anything but the end of the work
        }

        public void Release()
        {
            if (_released) return;
            _released = true;
            Close();
        }
    }
}
