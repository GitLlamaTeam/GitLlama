using Avalonia.Controls;
using Avalonia.Interactivity;
using GitLlama.Desktop.ViewModels;

namespace GitLlama.Desktop.Views;

public partial class InspectorView
{
    private async void DiscardAll_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.CanDiscardAllChanges
            || TopLevel.GetTopLevel(this) is not Window window)
            return;

        var repository = vm.SelectedRepository;
        var branch = vm.CurrentBranch;
        var paths = vm.InspectorDiscardPaths();
        var all = paths.Length == 0;
        var action = all ? "Discard all changes" : "Discard selected changes";
        var message = all
            ? "All staged and unstaged edits and untracked files will be discarded. Ignored files and nested repositories are kept. This can't be undone."
            : $"Discard all staged and unstaged edits in these {paths.Length} selected files? Selected new files will be deleted; renamed files return to their original paths. Unselected files are kept. This can't be undone.\n\n"
                + string.Join("\n", paths.Take(12)) + (paths.Length > 12 ? $"\n…and {paths.Length - 12} more files" : "");
        if (await ConfirmDialog.ShowAsync(window, action + "?", message, action, destructive: true)
            && ReferenceEquals(repository, vm.SelectedRepository)
            && branch == vm.CurrentBranch
            && paths.SequenceEqual(vm.InspectorDiscardPaths(), StringComparer.Ordinal))
            await vm.DiscardInspectorFilesAsync(paths);
    }
}
