using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using GitLlama.Desktop.ViewModels;

namespace GitLlama.Desktop.Views;

public partial class RemoteTreeView : UserControl
{
    public RemoteTreeView() => AvaloniaXamlLoader.Load(this);

    private RemoteTreeNode? EventNode(object? source) =>
        (source as Control)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext
        as RemoteTreeNode;

    private void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && e.AddedItems.OfType<RemoteTreeNode>().FirstOrDefault() is { } node)
        {
            if (node.Remote is not null)
            {
                vm.SelectedRemote = node.Remote;
            }

            if (node.Branch is { } branch)
            {
                vm.SelectedRemote = vm.Remotes.FirstOrDefault(r =>
                    branch.StartsWith(r.Name + "/", StringComparison.Ordinal)
                );
                vm.SelectedBranchLine = branch;
                if (vm.SelectedRemote is { } remote)
                {
                    vm.UpstreamBranchName = branch[(remote.Name.Length + 1)..];
                }
            }
        }
    }

    private async void BranchTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && EventNode(e.Source)?.Branch is { } branch)
        {
            e.Handled = true;
            await vm.RevealBranchHistoryCommand.ExecuteAsync(branch);
        }
    }

    private async void BranchDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (EventNode(e.Source)?.Branch is { } branch)
        {
            e.Handled = true;
            await CheckoutAsync(branch);
        }
    }

    private async Task CheckoutAsync(string branch)
    {
        if (DataContext is not MainWindowViewModel vm || branch.EndsWith("/HEAD", StringComparison.Ordinal))
        {
            return;
        }

        await PromptAsync(
            "Checkout " + branch,
            "Name for the new local tracking branch",
            "Checkout",
            value =>
            {
                vm.ContextBranchReference = branch;
                vm.RemoteLocalBranchName = value;
            },
            () => vm.CheckoutContextRemoteCommand.ExecuteAsync(null)
        );
    }

    private void TreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var node =
            EventNode(e.Source) ?? this.FindControl<TreeView>("RemoteTreeControl")?.SelectedItem as RemoteTreeNode;
        if (node is null || (node.Remote is null && node.Branch is null))
        {
            return;
        }

        var control =
            (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() as Control
            ?? this;
        control.ContextMenu = BuildMenu(node);
        control.ContextMenu.Open(control);
        e.Handled = true;
    }

    internal ContextMenu BuildMenu(RemoteTreeNode node)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return new ContextMenu();
        }

        var items = new List<MenuItem>();
        var hasNavigationMenu = false;
        if (
            node.Branch is { } branch
            && this.GetVisualAncestors().OfType<NavigationView>().FirstOrDefault() is { } navigation
        )
        {
            items.AddRange(navigation.BuildBranchMenu(branch).ItemsSource!.OfType<MenuItem>());
            hasNavigationMenu = true;
        }

        void Add(string label, Func<Task> action)
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await action();
            items.Add(item);
        }

        if (node.Remote is { } remote)
        {
            Add(
                "Fetch " + remote.Name,
                () =>
                {
                    vm.SelectedRemote = remote;
                    return vm.FetchRemoteCommand.ExecuteAsync(null);
                }
            );
            Add(
                "Push " + vm.CurrentBranch + " to " + remote.Name,
                () =>
                {
                    vm.SelectedRemote = remote;
                    return vm.PushRemoteCommand.ExecuteAsync(null);
                }
            );
            Add(
                "Create remote branch…",
                () => EditBranchAsync(remote.Name, null, GitLlama.Git.RemoteBranchOperation.Create)
            );
            Add("Edit remote URL…", () => EditConnectionAsync(remote));
            Add("Remove " + remote.Name + "…", () => EditConnectionAsync(remote, remove: true));
            Add("Copy remote URL", () => CopyAsync(remote.Url));
        }
        else if (node.Branch is { } reference)
        {
            if (!hasNavigationMenu)
            {
                Add("Checkout " + reference + "…", () => CheckoutAsync(reference));
                Add("Show branch history", () => vm.RevealBranchHistoryCommand.ExecuteAsync(reference));
                Add("Copy branch name", () => CopyAsync(reference));
            }

            var branchRemote = vm.Remotes.FirstOrDefault(candidate =>
                reference.StartsWith(candidate.Name + "/", StringComparison.Ordinal)
            );
            if (branchRemote is not null && !reference.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                var name = reference[(branchRemote.Name.Length + 1)..];
                Add(
                    "Create remote branch here…",
                    () => EditBranchAsync(branchRemote.Name, name, GitLlama.Git.RemoteBranchOperation.Create)
                );
                Add(
                    "Rename remote branch…",
                    () => EditBranchAsync(branchRemote.Name, name, GitLlama.Git.RemoteBranchOperation.Rename)
                );
                Add(
                    "Delete remote branch…",
                    () => EditBranchAsync(branchRemote.Name, name, GitLlama.Git.RemoteBranchOperation.Delete)
                );
            }
        }

        return new ContextMenu { ItemsSource = items };
    }

    private Task EditConnectionAsync(GitLlama.Git.GitRemoteInfo remote, bool remove = false) =>
        TopLevel.GetTopLevel(this) is Window owner && DataContext is MainWindowViewModel vm
            ? RemoteEditDialog.ConnectionAsync(owner, vm, remote, remove)
            : Task.CompletedTask;

    private Task EditBranchAsync(string remote, string? branch, GitLlama.Git.RemoteBranchOperation operation) =>
        TopLevel.GetTopLevel(this) is Window owner && DataContext is MainWindowViewModel vm
            ? RemoteEditDialog.BranchAsync(owner, vm, remote, branch, operation)
            : Task.CompletedTask;

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private async Task PromptAsync(
        string title,
        string description,
        string action,
        Action<string> setValue,
        Func<Task> execute
    )
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var path = vm.SelectedRepository?.Path;
        var input = new TextBox { Watermark = "Local branch name" };
        var feedback = new TextBlock();
        var button = new Button { Content = action, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 240,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    input,
                    feedback,
                    button,
                },
            },
        };
        button.Click += async (_, _) =>
        {
            if (path != vm.SelectedRepository?.Path)
            {
                feedback.Text = "Repository changed. Close and review the action again.";
                return;
            }

            if (string.IsNullOrWhiteSpace(input.Text))
            {
                feedback.Text = "Enter a local branch name.";
                return;
            }

            setValue(input.Text!);
            dialog.Close();
            await execute();
        };
        await dialog.ShowDialog(owner);
    }
}
