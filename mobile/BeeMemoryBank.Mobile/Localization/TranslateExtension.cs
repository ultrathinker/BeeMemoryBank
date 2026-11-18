namespace BeeMemoryBank.Mobile.Localization;

/// <summary>
/// XAML access to <see cref="AppStrings"/>: <c>Text="{loc:Translate TabTree}"</c> with
/// <c>xmlns:loc="clr-namespace:BeeMemoryBank.Mobile.Localization"</c>. The key can also be the
/// content of the markup: <c>{loc:Translate TabTree}</c>.
/// </summary>
[ContentProperty(nameof(Key))]
[AcceptEmptyServiceProvider]
public sealed class TranslateExtension : IMarkupExtension<string>
{
    public string Key { get; set; } = "";

    public string ProvideValue(IServiceProvider serviceProvider) => AppStrings.Get(Key);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
