namespace BeeMemoryBank.Mobile.Controls;

public static class PopupExtensions
{
    public static async Task<string?> ShowInputPopupAsync(this ContentPage page,
        string title, string message,
        string accept = "OK", string cancel = "Cancel",
        string? placeholder = null, string? initialValue = null, int maxLength = 200)
    {
        var popup = new InputPopup(title, message, accept, cancel, placeholder, initialValue, maxLength);

        if (page.Content is Grid grid)
        {
            // Span all rows and columns to overlay the entire page
            Grid.SetRowSpan(popup, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(popup, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.Children.Add(popup);
            try
            {
                return await popup.ResultTask;
            }
            finally
            {
                grid.Children.Remove(popup);
            }
        }

        // Fallback: wrap existing content in a Grid
        var wrapper = new Grid();
        var existingContent = page.Content;
        page.Content = wrapper;
        if (existingContent != null)
            wrapper.Children.Add(existingContent);
        wrapper.Children.Add(popup);

        try
        {
            return await popup.ResultTask;
        }
        finally
        {
            wrapper.Children.Remove(popup);
            page.Content = existingContent;
        }
    }
}
