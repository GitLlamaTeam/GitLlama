using Avalonia.Controls;
using Avalonia.Media;
using GitLlama.Desktop.ViewModels;
using GitLlama.Git;

namespace GitLlama.Desktop.Views;

/// <summary>Lock, unlock and force-unlock menu items for a file in a repository that uses Git LFS.</summary>
public static class LfsLockMenu
{
    public static IEnumerable<MenuItem> Items(Control owner, MainWindowViewModel vm, string path)
    {
        var current = vm.LfsLockFor(path);
        if (current is null)
        {
            var lockItem = new MenuItem { Header = "Lock file" };
            lockItem.Click += async (_, _) => await vm.LockLfsFileCommand.ExecuteAsync(path);
            yield return lockItem;
        }
        else if (current.IsOurs)
        {
            var unlock = new MenuItem { Header = "Unlock file" };
            unlock.Click += async (_, _) => await vm.UnlockLfsFileCommand.ExecuteAsync(path);
            yield return unlock;
        }

        if (current is not null)
        {
            var force = new MenuItem
            {
                Header = current.IsOurs ? "Force unlock…" : $"Force unlock {current.Owner}'s lock…",
            };
            force.Click += async (_, _) => await ConfirmForceUnlockAsync(owner, vm, current);
            yield return force;
        }
    }

    private static async Task ConfirmForceUnlockAsync(Control owner, MainWindowViewModel vm, GitLfsLock current)
    {
        if (TopLevel.GetTopLevel(owner) is not Window window)
        {
            return;
        }

        var name = Path.GetFileName(current.Path);
        var dialog = RemoteEditDialog.Create("Force unlock " + name, out var fields);
        fields.Children.Add(
            new TextBlock
            {
                Text = current.IsOurs
                    ? $"Release your lock on {current.Path} even though it has uncommitted changes?"
                    : $"{current.Owner} holds the lock on {current.Path}. Releasing it lets anyone push changes to the file, "
                        + "which can overwrite their work. Continue?",
                TextWrapping = TextWrapping.Wrap,
            }
        );
        await RemoteEditDialog.SubmitAsync(
            dialog,
            window,
            fields,
            "Force unlock",
            async () =>
            {
                await vm.ForceUnlockLfsFileCommand.ExecuteAsync(current.Path);
                return vm.LfsLockFor(current.Path) is null ? null : vm.Status;
            },
            destructive: true
        );
    }
}
