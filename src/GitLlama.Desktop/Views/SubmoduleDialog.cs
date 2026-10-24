using Avalonia.Controls;
using Avalonia.Media;
using GitLlama.Desktop.ViewModels;
using GitLlama.Git;

namespace GitLlama.Desktop.Views;

public static class SubmoduleDialog
{
    public static async Task AddAsync(Window owner, MainWindowViewModel vm)
    {
        if (vm.IsNetworkBusy || vm.IsFeatureBusy || vm.SelectedRepository is null)
        {
            return;
        }

        var dialog = RemoteEditDialog.Create("Add submodule", out var fields);
        fields.Children.Add(
            new TextBlock
            {
                Text =
                    "Clone a repository into a folder of "
                    + vm.SelectedRepository.Name
                    + " and record it as a submodule.",
                TextWrapping = TextWrapping.Wrap,
            }
        );
        var url = new TextBox { Name = "SubmoduleUrlInput", Watermark = "https://github.com/owner/repository.git" };
        var path = new TextBox { Name = "SubmodulePathInput", Watermark = "Folder inside this repository" };
        var branch = new TextBox { Name = "SubmoduleBranchInput", Watermark = "Default branch" };
        // Suggest a folder from the URL until the user types their own.
        var suggested = "";
        url.TextChanged += (_, _) =>
        {
            if (path.Text is null or "" || path.Text == suggested)
            {
                path.Text = suggested = GitSubmoduleService.SuggestPath(url.Text ?? "");
            }
        };
        fields.Children.Add(new TextBlock { Text = "Repository URL" });
        fields.Children.Add(url);
        fields.Children.Add(new TextBlock { Text = "Folder" });
        fields.Children.Add(path);
        fields.Children.Add(new TextBlock { Text = "Branch (optional)" });
        fields.Children.Add(branch);
        await RemoteEditDialog.SubmitAsync(
            dialog,
            owner,
            fields,
            "Add submodule",
            async () =>
            {
                if (string.IsNullOrWhiteSpace(url.Text) || string.IsNullOrWhiteSpace(path.Text))
                {
                    return "Enter a repository URL and a folder.";
                }

                await vm.AddSubmoduleCommand.ExecuteAsync(
                    new SubmoduleAddRequest(url.Text.Trim(), path.Text.Trim(), branch.Text?.Trim())
                );
                return vm.SubmoduleEditSucceeded ? null : vm.SubmoduleEditFeedback;
            }
        );
    }

    public static async Task RemoveAsync(Window owner, MainWindowViewModel vm, GitSubmoduleInfo module)
    {
        if (vm.IsNetworkBusy || vm.IsFeatureBusy)
        {
            return;
        }

        var dialog = RemoteEditDialog.Create("Remove " + module.DisplayName, out var fields);
        fields.Children.Add(
            new TextBlock
            {
                Text =
                    $"Remove the submodule at {module.Path} from this repository: its folder, its .gitmodules entry and its configuration. "
                    + "Its cloned history is kept in .git/modules, and the change is staged for you to commit.",
                TextWrapping = TextWrapping.Wrap,
            }
        );
        await RemoteEditDialog.SubmitAsync(
            dialog,
            owner,
            fields,
            "Remove submodule",
            async () =>
            {
                await vm.RemoveSubmoduleCommand.ExecuteAsync(module);
                return vm.SubmoduleEditSucceeded ? null : vm.SubmoduleEditFeedback;
            },
            destructive: true
        );
    }
}
