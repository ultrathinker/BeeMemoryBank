using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BeeMemoryBank.BlindDesktop.ViewModels;

/// <summary>Minimal change notification for the view model (no UI-toolkit types, so the view model is testable without a window).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// A command over a delegate; <see cref="RaiseCanExecuteChanged"/> tells the buttons to ask again. A command runs on the UI thread, where an
/// exception that escapes is an unhandled UI exception: with <paramref name="onError"/> it is handed over (the view model shows it as a
/// notice) and does not escape. Without a handler it is not swallowed (that is for tests only).
/// </summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute(), onError)
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        try
        {
            return canExecute?.Invoke(parameter) ?? true;
        }
        catch (Exception ex) when (onError is not null && ex is not OutOfMemoryException)
        {
            onError(ex);
            return false;
        }
    }

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try
        {
            execute(parameter);
        }
        catch (Exception ex) when (onError is not null && ex is not OutOfMemoryException)
        {
            onError(ex);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
