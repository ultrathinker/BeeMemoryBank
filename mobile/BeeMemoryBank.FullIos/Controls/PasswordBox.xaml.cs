using BeeMemoryBank.FullIos.Localization;

namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// A password field of the design: the rounded input shell with the eye button that shows or
/// hides the text. <see cref="Text"/> and <see cref="Placeholder"/> bind like an Entry's; the
/// masking is the control's own, the page never toggles it. Copied from the Android app.
/// </summary>
public partial class PasswordBox : ContentView
{
    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(PasswordBox), "", BindingMode.TwoWay,
            propertyChanged: (b, _, n) => ((PasswordBox)b).OnTextChanged((string)n));

    public static readonly BindableProperty PlaceholderProperty =
        BindableProperty.Create(nameof(Placeholder), typeof(string), typeof(PasswordBox), "",
            propertyChanged: (b, _, n) => ((PasswordBox)b).InnerEntry.Placeholder = (string)n);

    public static readonly BindableProperty ReturnTypeProperty =
        BindableProperty.Create(nameof(ReturnType), typeof(ReturnType), typeof(PasswordBox), ReturnType.Default,
            propertyChanged: (b, _, n) => ((PasswordBox)b).InnerEntry.ReturnType = (ReturnType)n);

    public static readonly BindableProperty ReturnCommandProperty =
        BindableProperty.Create(nameof(ReturnCommand), typeof(System.Windows.Input.ICommand), typeof(PasswordBox), null,
            propertyChanged: (b, _, n) => ((PasswordBox)b).InnerEntry.ReturnCommand = (System.Windows.Input.ICommand?)n);

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public ReturnType ReturnType
    {
        get => (ReturnType)GetValue(ReturnTypeProperty);
        set => SetValue(ReturnTypeProperty, value);
    }

    public System.Windows.Input.ICommand? ReturnCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(ReturnCommandProperty);
        set => SetValue(ReturnCommandProperty, value);
    }

    /// <summary>The Enter key of the field, forwarded from the inner Entry.</summary>
    public event EventHandler? Completed
    {
        add => InnerEntry.Completed += value;
        remove => InnerEntry.Completed -= value;
    }

    private bool _updating;

    public PasswordBox()
    {
        InitializeComponent();
        InnerEntry.TextChanged += (_, e) =>
        {
            if (_updating) return;
            _updating = true;
            Text = e.NewTextValue;
            _updating = false;
        };
        UpdateToggleIcon();
        SemanticProperties.SetDescription(ToggleButton, AppStrings.Get("ShowPassword"));
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler != null && Application.Current is { } app)
            app.RequestedThemeChanged += OnRequestedThemeChanged;
        else if (Application.Current is { } gone)
            gone.RequestedThemeChanged -= OnRequestedThemeChanged;
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => UpdateToggleIcon();

    private void OnTextChanged(string value)
    {
        if (_updating || InnerEntry.Text == value) return;
        _updating = true;
        InnerEntry.Text = value;
        _updating = false;
    }

    private void OnToggleClicked(object? sender, EventArgs e)
    {
        InnerEntry.IsPassword = !InnerEntry.IsPassword;
        UpdateToggleIcon();
        SemanticProperties.SetDescription(ToggleButton,
            AppStrings.Get(InnerEntry.IsPassword ? "ShowPassword" : "HidePassword"));
    }

    private void UpdateToggleIcon()
    {
        ToggleButton.ImageSource = InnerEntry.IsPassword
            ? ThemeManager.Icon("icon_eye")
            : ThemeManager.Icon("icon_eye_off");
    }
}
