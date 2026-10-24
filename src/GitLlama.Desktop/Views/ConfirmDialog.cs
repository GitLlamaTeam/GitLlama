using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitLlama.Desktop.Views;

/// <summary>A small question with a cancel button and a button that goes ahead.</summary>
public static class ConfirmDialog
{
    /// <summary>
    /// Asks before an action and returns whether to go ahead. Enter confirms unless the action is destructive, and
    /// Escape always cancels.
    /// </summary>
    public static async Task<bool> ShowAsync(
        Window owner,
        string title,
        string message,
        string confirm,
        string cancel = "Cancel",
        bool destructive = false
    )
    {
        var dialog = RemoteEditDialog.Create(title, out var fields);
        fields.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var no = new Button { Content = cancel, IsCancel = true };
        var yes = new Button
        {
            Content = confirm,
            Classes = { "workspace", "primary" },
            IsDefault = !destructive,
        };
        var confirmed = false;
        if (destructive)
        {
            yes.Background = new SolidColorBrush(Color.Parse("#91434D"));
            yes.Foreground = Brushes.White;
        }
        no.Click += (_, _) => dialog.Close();
        yes.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                dialog.Close();
            }
        };
        dialog.Opened += (_, _) => (destructive ? no : yes).Focus();
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        fields.Children.Add(buttons);
        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
