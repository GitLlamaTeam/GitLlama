using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Desktop.ViewModels;
using GitLlama.Git;

namespace GitLlama.Desktop.Views;

public partial class NavigationView
{
    private async void Branch_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var item = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is not string branch || !list.GetVisualDescendants().Contains(item))
        {
            return;
        }

        e.Handled = true;
        await vm.RevealBranchHistoryCommand.ExecuteAsync(branch);
    }

    private async void Branch_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        var item = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is not string branch || !list.GetVisualDescendants().Contains(item))
        {
            return;
        }

        e.Handled = true;
        await CheckoutSidebarBranchAsync(branch);
    }

    private async Task CheckoutSidebarBranchAsync(string branch)
    {
        if (
            DataContext is not MainWindowViewModel vm
            || branch == vm.CurrentBranch
            || branch.EndsWith("/HEAD", StringComparison.Ordinal)
        )
        {
            return;
        }

        vm.SelectedBranchLine = branch;
        if (vm.Remotes.Any(r => branch.StartsWith(r.Name + "/", StringComparison.Ordinal)))
        {
            await PromptAction(
                "Checkout " + branch,
                "Name for the new local tracking branch",
                "Checkout",
                "",
                value =>
                {
                    vm.ContextBranchReference = branch;
                    vm.RemoteLocalBranchName = value;
                },
                vm.CheckoutContextRemoteCommand
            );
        }
        else
        {
            await vm.CheckoutBranchCommand.ExecuteAsync(null);
        }
    }

    private async void Submodule_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var item = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is not GitSubmoduleInfo module || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        e.Handled = true;
        await vm.OpenSubmoduleCommand.ExecuteAsync(module);
    }

    private async void AddSubmodule_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is MainWindowViewModel vm && TopLevel.GetTopLevel(this) is Window owner)
        {
            await SubmoduleDialog.AddAsync(owner, vm);
        }
    }

    private async void TrackLfs_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is MainWindowViewModel vm)
        {
            await PromptAction(
                "Track with Git LFS",
                "Store files matching this pattern in Git LFS, for example *.psd, *.wav or Assets/**/*.uasset. Files already committed keep their history; stage them again to move them to LFS.",
                "Track",
                "",
                value => vm.LfsPatternInput = value,
                vm.TrackLfsPatternCommand
            );
        }
    }

    private void LfsPatternContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var row =
            (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext
                as LfsPatternRow
            ?? list.SelectedItem as LfsPatternRow;
        if (row is null)
        {
            return;
        }

        var locking = new MenuItem
        {
            Header = row.Lockable ? "Stop requiring locks for " + row.Pattern : "Require locks for " + row.Pattern,
        };
        locking.Click += async (_, _) =>
        {
            if (row.Lockable)
            {
                await vm.StopRequiringLfsLockingCommand.ExecuteAsync(row.Pattern);
            }
            else
            {
                await vm.RequireLfsLockingCommand.ExecuteAsync(row.Pattern);
            }
        };
        var untrack = new MenuItem { Header = "Stop tracking " + row.Pattern };
        untrack.Click += async (_, _) => await vm.UntrackLfsPatternCommand.ExecuteAsync(row.Pattern);
        list.ContextMenu = new ContextMenu { ItemsSource = new[] { locking, untrack } };
        list.ContextMenu.Open(list);
        e.Handled = true;
    }

    private async void PruneLfs_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        vm.Status = "Checking which LFS files can be removed…";
        if (await vm.PreviewLfsPruneAsync() is not { } preview)
        {
            return;
        }

        if (preview.Objects == 0)
        {
            vm.Status = "There are no old LFS files to remove. Recent and unpushed files are always kept.";
            return;
        }

        var dialog = RemoteEditDialog.Create("Free up disk space", out var fields);
        fields.Children.Add(
            new TextBlock
            {
                Text =
                    $"Remove {preview.Objects} old LFS {(preview.Objects == 1 ? "file" : "files")} from this computer. "
                    + $"Your LFS storage currently uses {MainWindowViewModel.FormatSize(preview.StorageBytes)}. "
                    + "The server already has these files, and they're downloaded again if you check out a commit "
                    + "that needs them. Files for recent commits and anything you haven't pushed are kept.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            }
        );
        vm.Status = "";
        await RemoteEditDialog.SubmitAsync(
            dialog,
            owner,
            fields,
            preview.Objects == 1 ? "Remove 1 file" : $"Remove {preview.Objects} files",
            async () =>
            {
                await vm.PruneLfsCommand.ExecuteAsync(null);
                return null;
            }
        );
    }

    private async void LockLfsFile_Click(object? sender, RoutedEventArgs e)
    {
        if (
            DataContext is not MainWindowViewModel { SelectedRepository: { } repository } vm
            || TopLevel.GetTopLevel(this) is not { } top
        )
        {
            return;
        }

        var start = await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(repository.Path));
        var picked = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Choose a file to lock",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            }
        );
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } file)
        {
            return;
        }

        var relative = Path.GetRelativePath(repository.Path, file).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            vm.Status = "Choose a file inside this repository.";
            return;
        }

        await vm.LockLfsFileCommand.ExecuteAsync(relative);
    }

    private void LfsLockContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var row =
            (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext
                as LfsLockRow
            ?? list.SelectedItem as LfsLockRow;
        if (row is null)
        {
            return;
        }

        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(row.Path);
            }
        };
        list.ContextMenu = new ContextMenu
        {
            ItemsSource = LfsLockMenu
                .Items(this, vm, row.Path)
                .Cast<object>()
                .Append(new Separator())
                .Append(copy)
                .ToArray(),
        };
        list.ContextMenu.Open(list);
        e.Handled = true;
    }

    private void SubmoduleContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var module =
            (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext
                as GitSubmoduleInfo
            ?? list.SelectedItem as GitSubmoduleInfo;
        if (module is null)
        {
            return;
        }

        vm.SelectedSubmodule = module;
        var open = new MenuItem { Header = module.IsCheckedOut ? "Open submodule" : "Initialize and open submodule" };
        open.Click += async (_, _) => await vm.OpenSubmoduleCommand.ExecuteAsync(module);
        var update = new MenuItem
        {
            Header = module.IsCheckedOut ? "Update to recorded commit" : "Initialize submodule",
        };
        update.Click += async (_, _) =>
        {
            vm.SelectedSubmodule = module;
            await vm.UpdateSubmoduleCommand.ExecuteAsync(null);
        };
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(module.Path);
            }
        };
        var remove = new MenuItem { Header = "Remove submodule…" };
        remove.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await SubmoduleDialog.RemoveAsync(owner, vm, module);
            }
        };
        var details = new MenuItem { Header = "Submodule details…" };
        details.Click += (_, _) => (TopLevel.GetTopLevel(this) as MainWindow)?.ShowDocument("Submodules");
        list.ContextMenu = new ContextMenu
        {
            ItemsSource = new object[] { open, update, copy, details, new Separator(), remove },
        };
        list.ContextMenu.Open(list);
        e.Handled = true;
    }

    private async Task SaveStashAsync()
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = RemoteEditDialog.Create("Stash staged changes", out var fields);
        fields.Children.Add(
            new TextBlock
            {
                Text = "Set your staged changes aside. Unstaged edits and untracked files stay in the working tree.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            }
        );
        var message = new TextBox { Watermark = "Message (optional)" };
        fields.Children.Add(message);
        await RemoteEditDialog.SubmitAsync(
            dialog,
            owner,
            fields,
            "Stash",
            async () =>
            {
                var before = vm.Stashes.Count;
                vm.StashMessage = message.Text ?? "";
                if (vm.StashBlockedReason is { } blocked)
                {
                    return blocked;
                }

                await vm.SaveStagedStashCommand.ExecuteAsync(null);
                return vm.Stashes.Count > before ? null : vm.Status;
            }
        );
    }

    private void Stash_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<StashTreeNode>().FirstOrDefault() is { Stash: { } stash })
        {
            InspectStash(stash);
        }
    }

    private void Stash_Tapped(object? sender, TappedEventArgs e)
    {
        // Selecting the same stash again after viewing a commit should bring it back.
        if (
            (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext
            is StashTreeNode { Stash: { } stash }
        )
        {
            InspectStash(stash);
        }
    }

    private void InspectStash(GitStashInfo stash)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SelectedStash = stash;
            vm.InspectStash(stash);
        }
    }

    private void StashContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not TreeView tree || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var node =
            (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext
                as StashTreeNode
            ?? tree.SelectedItem as StashTreeNode;
        if (node is null)
        {
            return;
        }

        var items = new List<object>();
        void Add(string label, Func<Task> action)
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await action();
            items.Add(item);
        }

        if (node.Stash is { } stash)
        {
            vm.SelectedStash = stash;
            Add("Apply stash", () => RestoreStashAsync(stash, vm.ApplySelectedStashCommand, "Apply"));
            Add("Pop stash", () => RestoreStashAsync(stash, vm.PopSelectedStashCommand, "Pop"));
            Add(
                "Copy message",
                async () =>
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    {
                        await clipboard.SetTextAsync(stash.Message);
                    }
                }
            );
            items.Add(new Separator());
            Add("Drop stash…", () => DropStashAsync(node));
        }
        else
        {
            Add("Stash changes…", SaveStashAsync);
            ((MenuItem)items[^1]).IsEnabled = !vm.HasNoChanges;
        }

        var control =
            (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() as Control
            ?? tree;
        control.ContextMenu = new ContextMenu { ItemsSource = items };
        control.ContextMenu.Open(control);
        e.Handled = true;
    }

    /// <summary>Applies or pops a stash, asking first when it would land on top of uncommitted changes.</summary>
    private async Task RestoreStashAsync(GitStashInfo stash, IAsyncRelayCommand<bool?> command, string action)
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        vm.SelectedStash = stash;
        if (vm.HasNoChanges)
        {
            await command.ExecuteAsync(false);
            return;
        }

        var changes = vm.ChangeCount == 1 ? "1 uncommitted change" : $"{vm.ChangeCount} uncommitted changes";
        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            action + " stash over your changes?",
            $"You have {changes}. The stash will be added on top of them, which can make the two harder to tell "
                + "apart. If the stash changes a file you've also changed, nothing is applied and the stash is kept.",
            "Yes, " + action.ToLowerInvariant(),
            "No"
        );
        if (confirmed && vm.SelectedStash == stash)
        {
            await command.ExecuteAsync(true);
        }
    }

    /// <summary>Merges a branch into the current one after a quick confirmation.</summary>
    private async Task MergeBranchAsync(string branch)
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var current = vm.CurrentBranch;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            $"Merge {branch} into {current}?",
            $"The changes from {branch} are merged into {current}. If any files conflict, the merge stops so you "
                + "can resolve them before it's committed.",
            "Merge"
        );
        if (confirmed && vm.SelectedBranchLine == branch && vm.CurrentBranch == current)
        {
            await vm.MergeSelectedBranchCommand.ExecuteAsync(null);
        }
    }

    private async Task DropStashAsync(StashTreeNode node)
    {
        if (
            DataContext is not MainWindowViewModel vm
            || TopLevel.GetTopLevel(this) is not Window owner
            || node.Stash is not { } stash
        )
        {
            return;
        }

        vm.SelectedStash = stash;
        await vm.DropSelectedStashCommand.ExecuteAsync(null);
    }

    internal ContextMenu BuildBranchMenu(string branch)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return new ContextMenu();
        }

        var remote = vm.Remotes.Any(r => branch.StartsWith(r.Name + "/", StringComparison.Ordinal));
        var items = new List<MenuItem>();
        void Add(string label, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += async (_, _) =>
            {
                vm.SelectedBranchLine = branch;
                await action();
            };
            items.Add(item);
        }

        Add(
            "Checkout " + branch,
            () => CheckoutSidebarBranchAsync(branch),
            branch != vm.CurrentBranch && !branch.EndsWith("/HEAD", StringComparison.Ordinal)
        );
        Add("Merge into " + vm.CurrentBranch + "…", () => MergeBranchAsync(branch), branch != vm.CurrentBranch);
        Add("Create branch here…", () => CreateBranchAtAsync(branch));
        Add(
            "Create worktree here…",
            () =>
            {
                vm.WorktreeBranch = branch;
                (TopLevel.GetTopLevel(this) as MainWindow)?.ShowDocument("Worktrees");
                return Task.CompletedTask;
            },
            !remote
        );
        if (!remote)
        {
            Add(
                "Rename branch…",
                () =>
                    PromptAction(
                        "Rename " + branch,
                        "New branch name",
                        "Rename",
                        "",
                        value => vm.RenameBranchName = value,
                        vm.RenameBranchCommand
                    )
            );
            Add(
                "Delete merged branch…",
                () =>
                    PromptAction(
                        "Delete " + branch,
                        "Only a merged, non-current branch can be deleted.",
                        "Delete",
                        branch,
                        _ => { },
                        vm.DeleteBranchCommand
                    ),
                branch != vm.CurrentBranch
            );
            Add(
                "Rebase current branch onto this…",
                () =>
                {
                    vm.RebaseTargetBranch = branch;
                    (TopLevel.GetTopLevel(this) as MainWindow)?.ShowDocument("Branches");
                    return Task.CompletedTask;
                },
                branch != vm.CurrentBranch
            );
        }

        Add(
            "Show branch history",
            async () =>
            {
                vm.SelectedHistoryScope = branch;
                await vm.ReloadHistoryCommand.ExecuteAsync(null);
                (TopLevel.GetTopLevel(this) as MainWindow)?.ShowDocument("History");
            }
        );
        Add(
            "Copy branch name",
            async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(branch);
                }
            }
        );
        return new ContextMenu { ItemsSource = items };
    }

    private void BranchContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var branch =
            (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext
                as string
            ?? list.SelectedItem as string;
        if (branch is null)
        {
            return;
        }

        vm.SelectedBranchLine = branch;
        list.ContextMenu = BuildBranchMenu(branch);
        list.ContextMenu.Open(list);
        e.Handled = true;
    }

    private async Task CreateBranchAtAsync(string reference)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        await PromptAction(
            "Create branch",
            "New branch at " + reference,
            "Create",
            "",
            value =>
            {
                vm.NewBranchName = value;
                vm.ContextBranchReference = reference;
            },
            vm.CreateContextBranchCommand
        );
    }

    private async void CreateBranch_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await CreateBranchAtAsync("HEAD");
    }

    private async void AddRemote_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is MainWindowViewModel vm && TopLevel.GetTopLevel(this) is Window owner)
        {
            await RemoteEditDialog.ConnectionAsync(owner, vm, null);
        }
    }

    private async void CreateAnnotatedTag_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await TagCreationDialog.ShowAsync(this, vm, "HEAD");
        }
    }

    private async void CreateTag_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await PromptAction(
                "Create tag at HEAD",
                "New lightweight tag name",
                "Create tag",
                "",
                value => vm.NewTagName = value,
                vm.CreateLightweightTagCommand
            );
        }
    }

    private void TagContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var tag =
            (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext
                as GitTagInfo
            ?? list.SelectedItem as GitTagInfo;
        if (tag is null)
        {
            return;
        }

        vm.SelectedTag = tag;
        var copy = new MenuItem { Header = "Copy tag name" };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(tag.Name);
            }
        };
        var push = new MenuItem { Header = "Push tag to selected remote", IsEnabled = vm.SelectedRemote is not null };
        push.Click += async (_, _) =>
        {
            vm.SelectedTag = tag;
            await vm.PushSelectedTagCommand.ExecuteAsync(null);
        };
        var delete = new MenuItem { Header = "Delete local tag…" };
        delete.Click += async (_, _) =>
        {
            vm.SelectedTag = tag;
            await vm.DeleteSelectedTagCommand.ExecuteAsync(null);
        };
        var history = new MenuItem { Header = "Show tag history" };
        history.Click += (_, _) =>
        {
            vm.SelectedHistoryScope = "refs/tags/" + tag.Name;
            (TopLevel.GetTopLevel(this) as MainWindow)?.ShowDocument("History");
        };
        list.ContextMenu = new ContextMenu { ItemsSource = new[] { history, push, delete, copy } };
        list.ContextMenu.Open(list);
        e.Handled = true;
    }

    private async Task PromptAction(
        string title,
        string description,
        string action,
        string expected,
        Action<string> setValue,
        IAsyncRelayCommand command
    )
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var repository = vm.SelectedRepository?.Path;
        var branch = vm.SelectedBranchLine;
        var tag = vm.SelectedTag;
        if (expected.Length > 0)
        {
            var current = vm.CurrentBranch;
            if (await ConfirmDialog.ShowAsync(owner, title, description + " Delete " + expected + "?", action, destructive: true)
                && vm.SelectedRepository?.Path == repository && vm.SelectedBranchLine == branch
                && vm.SelectedTag == tag && vm.CurrentBranch == current)
            {
                setValue(expected);
                await command.ExecuteAsync(null);
            }
            return;
        }
        var input = new TextBox { Watermark = "Name" };
        var button = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Right };
        button.Classes.Add("workspace");
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var feedback = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                input,
                feedback,
                button,
            },
        };
        button.Click += async (_, _) =>
        {
            if (vm.SelectedRepository?.Path != repository || vm.SelectedBranchLine != branch || vm.SelectedTag != tag)
            {
                feedback.Text = "Selection changed. Close and review the action again.";
                return;
            }

            if (string.IsNullOrWhiteSpace(input.Text))
            {
                feedback.Text = "Enter a name.";
                return;
            }

            setValue(input.Text!);
            dialog.Close();
            await command.ExecuteAsync(null);
        };
        await dialog.ShowDialog(owner);
    }
}
