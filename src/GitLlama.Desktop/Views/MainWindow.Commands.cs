using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using GitLlama.Desktop.Controls;
using GitLlama.Desktop.ViewModels;

namespace GitLlama.Desktop.Views;

public partial class MainWindow
{
    private void PullOptions_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control control)
            e.Handled = PullOptionsButton.OpenOptions(control);
    }

    private void CommandPalette_Click(object? sender, RoutedEventArgs e) => ShowCommandPalette();

    private void ShowCommandPalette()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var actions = new Dictionary<string, Func<Task>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Open repository…"] = () =>
            {
                OpenRepositoryFolder();
                return Task.CompletedTask;
            },
            ["Refresh repository"] = () => vm.RefreshCommand.ExecuteAsync(null),
            ["Stage selected file"] = () => vm.StageSelectedCommand.ExecuteAsync(null),
            ["Stage all changes"] = () => vm.StageAllCommand.ExecuteAsync(null),
            ["Commit staged changes"] = () =>
                vm.CommitCommand.CanExecute(null) ? vm.CommitCommand.ExecuteAsync(null) : Task.CompletedTask,
            ["Fetch selected remote"] = () => vm.FetchRemoteCommand.ExecuteAsync(null),
            ["Pull tracked branch"] = () => vm.PullCommand.ExecuteAsync(null),
            ["Push current branch"] = () => vm.PushRemoteCommand.ExecuteAsync(null),
            ["Reset workspace layout"] = () =>
            {
                ResetLayout_Click(null, new RoutedEventArgs());
                return Task.CompletedTask;
            },
        };
        foreach (var tab in vm.AvailableTabs)
        {
            actions["Show " + tab] = () =>
            {
                ShowDocument(tab);
                return Task.CompletedTask;
            };
        }

        var search = new TextBox { Watermark = "Type a command…" };
        var list = new ListBox { ItemsSource = actions.Keys.ToArray(), SelectedIndex = 0 };
        var dialog = new Window
        {
            Title = "Commands",
            Width = 550,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
            Margin = new Thickness(12),
        };
        grid.Children.Add(search);
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        dialog.Content = grid;
        search.TextChanged += (_, _) =>
        {
            list.ItemsSource = actions
                .Keys.Where(name => name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            list.SelectedIndex = 0;
        };
        async Task RunAsync()
        {
            if (list.SelectedItem is not string name || !actions.TryGetValue(name, out var action))
            {
                return;
            }

            dialog.Close();
            await action();
        }

        list.DoubleTapped += async (_, _) => await RunAsync();
        dialog.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RunAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.Close();
            }
            else if (e.Key == Key.Down && search.IsFocused)
            {
                list.Focus();
                e.Handled = true;
            }
        };
        dialog.Opened += (_, _) => search.Focus();
        _ = dialog.ShowDialog(this);
    }
}
