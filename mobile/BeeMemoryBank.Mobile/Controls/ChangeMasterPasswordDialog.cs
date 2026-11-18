using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The mock-up's "change master password" dialog: the current password, the new one with its repeat
/// and a hint, then Save. The change itself is the core service the iPhone uses
/// (<see cref="UserService.ChangePasswordAsync"/>): the key is re-wrapped under the new password and
/// the hint is kept under the same policy. A centered card over the dim, like the app's other dialogs.
/// </summary>
public sealed class ChangeMasterPasswordDialog : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly PasswordBox _current;
    private readonly PasswordBox _new;
    private readonly PasswordBox _repeat;
    private readonly Entry _hint;
    private readonly Label _error;

    public Task<bool> ResultTask => _result.Task;

    public ChangeMasterPasswordDialog(IServiceProvider services)
    {
        _services = services;
        BackgroundColor = ThemeManager.Token("Scrim");
        AutomationId = "ChangeMasterDialog";
        var resources = Application.Current?.Resources;

        _current = new PasswordBox { Placeholder = AppStrings.Get("CurrentPasswordPlaceholder") };
        _new = new PasswordBox { Placeholder = AppStrings.Get("NewPasswordPlaceholder") };
        _repeat = new PasswordBox { Placeholder = AppStrings.Get("RepeatNewPasswordPlaceholder") };
        _hint = new Entry { Placeholder = AppStrings.Get("MasterHintPlaceholder") };
        _error = new Label { Style = (Style?)resources?["FieldError"], IsVisible = false };

        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Children.Add(new Label { Text = AppStrings.Get("ChangeMasterTitle"), Style = (Style?)resources?["DialogTitle"] });
        stack.Children.Add(Field(AppStrings.Get("CurrentPasswordLabel"), _current));
        stack.Children.Add(Field(AppStrings.Get("NewPasswordLabel"), _new));
        stack.Children.Add(Field(AppStrings.Get("RepeatNewPasswordLabel"), _repeat));
        stack.Children.Add(Field(AppStrings.Get("HintLabel"), _hint));
        stack.Children.Add(_error);

        var cancel = new Button { Text = AppStrings.Get("Cancel"), Style = (Style?)resources?["SecondaryButton"] };
        cancel.Clicked += (_, _) => _ = CloseAsync(false);
        var save = new Button { Text = AppStrings.Get("Save"), Style = (Style?)resources?["PrimaryButton"] };
        save.Clicked += OnSaveClicked;
        var row = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 10 };
        row.Children.Add(cancel);
        Grid.SetColumn(cancel, 0);
        row.Children.Add(save);
        Grid.SetColumn(save, 1);
        stack.Children.Add(row);

        var panel = new Border
        {
            BackgroundColor = ThemeManager.Token("Surface"),
            Stroke = ThemeManager.Token("BorderStrong"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(24) },
            Padding = new Thickness(20),
            Margin = new Thickness(16),
            MaximumWidthRequest = 400,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = new ScrollView { Content = stack },
        };
        panel.GestureRecognizers.Add(new TapGestureRecognizer());

        var dim = new Grid();
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => _ = CloseAsync(false);
        dim.GestureRecognizers.Add(tap);
        dim.Children.Add(panel);
        Content = dim;
    }

    private static VerticalStackLayout Field(string label, View input)
    {
        var resources = Application.Current?.Resources;
        return new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = label, Style = (Style?)resources?["FieldLabel"] },
                input,
            },
        };
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        _error.IsVisible = false;
        var oldPwd = _current.Text ?? "";
        var newPwd = _new.Text ?? "";
        var repeat = _repeat.Text ?? "";
        var hint = _hint.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(oldPwd) || string.IsNullOrEmpty(newPwd))
        {
            ShowError(AppStrings.Get("ChangeMasterErrorFill"));
            return;
        }
        if (newPwd.Length < 8 || newPwd != repeat)
        {
            ShowError(AppStrings.Get("ChangeMasterErrorMismatch"));
            return;
        }
        if (PasswordHintPolicy.Validate(hint, newPwd, username: null) is { } hintProblem)
        {
            ShowError(string.IsNullOrEmpty(hint) ? AppStrings.Get("ChangeMasterErrorHint") : hintProblem);
            return;
        }

        // A long operation (BMB-311): the key is derived and re-wrapped; the result is written under the buttons.
        var ran = await BusyAction.RunAsync(null, async () =>
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var userService = scope.ServiceProvider.GetRequiredService<UserService>();
                var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var users = await userRepo.ListActiveAsync();
                var currentUser = users.FirstOrDefault(u => u.Role == UserRoles.Superadmin)
                    ?? throw new InvalidOperationException("No superadmin user found");
                await userService.ChangePasswordAsync(currentUser.Id, oldPwd, newPwd, passwordHint: hint);
            });
        }, new BusyOptions
        {
            Long = true,
            Title = AppStrings.Get("ChangingPasswordTitle"),
            Message = AppStrings.Get("ChangingPasswordMessage"),
            ResultLabel = _error,
        });
        if (ran) await CloseAsync(true);
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private bool _closing;

    private async Task CloseAsync(bool answer)
    {
        if (_closing) return;   // a second tap while closing answers nothing new
        _closing = true;
        // Pop FIRST, then give the answer: the caller pushes the "password changed" alert right
        // after the result, and a synchronous continuation must see this dialog already gone.
        try
        {
            if (Navigation.ModalStack.LastOrDefault() == this)
                await Navigation.PopModalAsync();
        }
        catch (InvalidOperationException)
        {
            // the modal stack changed under us: nothing left to pop
        }
        _result.TrySetResult(answer);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(false);
        return true;
    }
}
