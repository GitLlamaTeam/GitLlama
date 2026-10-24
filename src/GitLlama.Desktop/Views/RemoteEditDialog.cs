using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitLlama.Desktop.ViewModels;
using GitLlama.Git;

namespace GitLlama.Desktop.Views;

public static class RemoteEditDialog
{
    internal static Window Create(string title, out StackPanel fields)
    {
        fields = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 12 };
        fields.Children.Add(
            new TextBlock
            {
                Text = title,
                FontSize = 22,
                FontWeight = FontWeight.SemiBold,
            }
        );
        return new Window
        {
            Title = title,
            Width = 580,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#313338")),
            Content = fields,
        };
    }

    public static async Task ConnectionAsync(
        Window owner,
        MainWindowViewModel vm,
        GitRemoteInfo? remote,
        bool remove = false
    )
    {
        if (vm.IsNetworkBusy || vm.IsFeatureBusy || vm.PrepareRemoteConnectionEdit(remote) is not { } snapshot)
        {
            return;
        }

        var dialog = Create(
            remove ? "Remove " + remote!.Name
                : remote is null ? "Add remote"
                : "Edit " + remote.Name,
            out var fields
        );
        fields.Children.Add(
            new TextBlock
            {
                Text = remove
                    ? $"Remove remote {snapshot.Name} and its remote-tracking references from this repository? The remote server and its repositories will remain available."
                    : "Configure the repository's remote connection.",
                TextWrapping = TextWrapping.Wrap,
            }
        );
        var name = new TextBox
        {
            Name = "RemoteNameInput",
            Text = remove ? "" : snapshot.Name,
            IsReadOnly = remote is not null && !remove,
            Watermark = "Remote name (for example origin)",
            IsVisible = !remove,
        };
        fields.Children.Add(new TextBlock { Text = "Remote name", IsVisible = !remove });
        fields.Children.Add(name);
        var url = new TextBox
        {
            Name = "RemoteUrlInput",
            Text = snapshot.Url,
            Watermark = "Repository URL or local path",
            IsVisible = !remove,
        };
        fields.Children.Add(new TextBlock { Text = "Repository URL", IsVisible = !remove });
        fields.Children.Add(url);
        var push = new TextBox
        {
            Name = "RemotePushUrlInput",
            Text = snapshot.PushUrl == snapshot.Url ? "" : snapshot.PushUrl,
            Watermark = "Separate push URL (optional)",
            IsVisible = !remove && remote is not null,
        };
        fields.Children.Add(
            new TextBlock { Text = "Separate push URL (optional)", IsVisible = !remove && remote is not null }
        );
        fields.Children.Add(push);
        await SubmitAsync(
            dialog,
            owner,
            fields,
            remove ? "Remove remote" : "Save remote",
            async () =>
            {
                if (!remove && (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(url.Text)))
                {
                    return "Enter a remote name and repository URL.";
                }

                await vm.EditRemoteConnectionCommand.ExecuteAsync(
                    snapshot with
                    {
                        Name = remove ? snapshot.Name : name.Text!.Trim(),
                        Url = url.Text ?? "",
                        PushUrl = string.IsNullOrWhiteSpace(push.Text) ? url.Text ?? "" : push.Text.Trim(),
                        Remove = remove,
                    }
                );
                return vm.RemoteEditSucceeded ? null : vm.RemoteOperationFeedback;
            },
            destructive: remove
        );
    }

    public static async Task BranchAsync(
        Window owner,
        MainWindowViewModel vm,
        string remoteName,
        string? branchName,
        RemoteBranchOperation operation
    )
    {
        var preview = await vm.PrepareRemoteBranchEditAsync(remoteName, branchName);
        if (preview is null)
        {
            return;
        }

        var (draft, version) = preview.Value;
        var title =
            operation == RemoteBranchOperation.Create ? "Create remote branch"
            : operation == RemoteBranchOperation.Rename ? "Rename remote branch"
            : "Delete remote branch";
        var dialog = Create(title, out var fields);
        fields.Children.Add(
            new TextBlock { Text = $"Remote: {remoteName}\n{draft.RemoteUrl}", TextWrapping = TextWrapping.Wrap }
        );
        fields.Children.Add(
            new TextBlock
            {
                Text = operation switch
                {
                    RemoteBranchOperation.Create =>
                        $"Create a branch from {draft.SourceName} at {draft.CommitSha[..7]}.",
                    RemoteBranchOperation.Rename =>
                        $"Rename {draft.SourceName} on the server. Local branch names stay the same; their tracking configuration follows the renamed remote branch.",
                    _ =>
                        $"Delete {draft.SourceName} on the server? Local branches stay available.",
                },
                TextWrapping = TextWrapping.Wrap,
            }
        );
        var input = new TextBox
        {
            Name = "RemoteBranchNameInput",
            Text = operation == RemoteBranchOperation.Rename ? draft.BranchName : "",
            Watermark = "Branch name",
            IsVisible = operation != RemoteBranchOperation.Delete,
        };
        fields.Children.Add(input);
        await SubmitAsync(
            dialog,
            owner,
            fields,
            operation == RemoteBranchOperation.Delete ? "Delete branch" : "Save branch",
            async () =>
            {
                if (operation != RemoteBranchOperation.Delete && string.IsNullOrWhiteSpace(input.Text))
                {
                    return "Enter a branch name.";
                }

                await vm.ChangeRemoteBranchCommand.ExecuteAsync(
                    new RemoteBranchEditRequest(draft, version, operation,
                        operation == RemoteBranchOperation.Delete ? draft.BranchName! : input.Text!.Trim())
                );
                return vm.RemoteEditSucceeded ? null : vm.RemoteOperationFeedback;
            },
            destructive: operation == RemoteBranchOperation.Delete
        );
    }

    internal static async Task SubmitAsync(
        Window dialog,
        Window owner,
        StackPanel fields,
        string action,
        Func<Task<string?>> submit,
        bool destructive = false
    )
    {
        var feedback = new TextBlock { Name = "RemoteEditFeedback", TextWrapping = TextWrapping.Wrap };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var save = new Button { Content = action, Classes = { "workspace", "primary" } };
        if (destructive)
        {
            save.Background = new SolidColorBrush(Color.Parse("#91434D"));
            save.Foreground = Brushes.White;
            dialog.Opened += (_, _) => cancel.Focus();
        }
        var busy = false;
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            if (busy)
            {
                return;
            }

            busy = true;
            save.IsEnabled = cancel.IsEnabled = false;
            try
            {
                var error = await submit();
                if (error is null)
                {
                    busy = false;
                    dialog.Close();
                }
                else
                {
                    feedback.Text = error;
                }
            }
            catch
            {
                feedback.Text = "The operation could not be completed. Review the connection and retry.";
            }
            finally
            {
                busy = false;
                save.IsEnabled = cancel.IsEnabled = true;
            }
        };
        dialog.Closing += (_, e) =>
        {
            if (busy)
            {
                e.Cancel = true;
            }
        };
        dialog.KeyDown += (_, e) =>
        {
            if (!busy && e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };
        fields.Children.Add(feedback);
        row.Children.Add(cancel);
        row.Children.Add(save);
        fields.Children.Add(row);
        await dialog.ShowDialog(owner);
    }
}
