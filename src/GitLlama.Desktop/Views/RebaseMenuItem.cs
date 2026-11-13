using Avalonia.Controls;
using GitLlama.Desktop.ViewModels;

namespace GitLlama.Desktop.Views;

internal static class RebaseMenuItem
{
    internal static MenuItem Create(MainWindowViewModel vm, string reference, string label, bool validTarget = true)
    {
        var repository = vm.SelectedRepository?.Path;
        var branch = vm.CurrentBranch;
        var item = new MenuItem
        {
            Header = $"Rebase {branch} onto {label}…",
            IsEnabled = validTarget && vm.CanRebaseFromContext
        };
        item.Click += async (_, _) =>
        {
            if (vm.SelectedRepository?.Path == repository && vm.CurrentBranch == branch)
                await vm.RebaseOntoContextAsync(reference, label);
        };
        return item;
    }
}
