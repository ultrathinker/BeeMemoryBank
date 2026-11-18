using BeeMemoryBank.Mobile.Localization;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The mock-up's row action sheet (item-actions-dialog): the item's name as the title, then
/// Rename / Move / Delete and a quiet Cancel. Opened by the row's ⋮ button or a double tap
/// (the long press of the mock-up; MAUI has no long-press gesture of its own).
/// </summary>
public static class ItemActionsSheet
{
    public enum RowAction { None, Rename, Move, Delete }

    public static async Task<RowAction> ShowAsync(Page parent, string itemName)
    {
        var chosen = new TaskCompletionSource<RowAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = false;
        var resources = Application.Current?.Resources;

        var stack = new VerticalStackLayout { Spacing = 8 };
        BottomSheet.BottomSheetPage? sheet = null;

        void Add(string text, string styleKey, RowAction action)
        {
            var button = new Button { Text = text, Style = (Style?)resources?[styleKey] };
            button.Clicked += async (_, _) =>
            {
                // Close the sheet FIRST, then answer: the caller may navigate or push another modal
                // right after the answer, and must do so with the sheet already off the stack.
                answered = true;
                if (sheet != null) await sheet.CloseAsync();
                chosen.TrySetResult(action);
            };
            stack.Children.Add(button);
        }

        Add(AppStrings.Get("Rename"), "SecondaryButton", RowAction.Rename);
        Add(AppStrings.Get("Move"), "SecondaryButton", RowAction.Move);
        Add(AppStrings.Get("Delete"), "DangerButton", RowAction.Delete);

        var cancel = new Button
        {
            Text = AppStrings.Get("Cancel"),
            Style = (Style?)resources?["TextButton"],
            HorizontalOptions = LayoutOptions.Fill,
        };
        cancel.Clicked += async (_, _) =>
        {
            answered = true;
            if (sheet != null) await sheet.CloseAsync();
            chosen.TrySetResult(RowAction.None);
        };
        stack.Children.Add(cancel);

        sheet = new BottomSheet.BottomSheetPage(stack, itemName);
        // The dim or the system back closes the sheet without an action (no button answered).
        sheet.Disappearing += (_, _) => { if (!answered) chosen.TrySetResult(RowAction.None); };
        await parent.Navigation.PushModalAsync(sheet);
        return await chosen.Task;
    }
}
