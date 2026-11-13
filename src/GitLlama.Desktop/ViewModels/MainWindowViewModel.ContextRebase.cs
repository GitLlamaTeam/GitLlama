namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    internal bool CanRebaseFromContext => HasRepository && !string.IsNullOrWhiteSpace(CurrentBranch)
        && CurrentBranch != "Detached HEAD" && RepositoryOperation == "None";

    internal async Task RebaseOntoContextAsync(string reference, string targetLabel)
    {
        if (!CanRebaseFromContext || SelectedRepository is not { } repository)
            return;

        var branch = CurrentBranch;
        if (!await ConfirmActionAsync(
                $"Rebase {branch} onto {targetLabel}?",
                "This replays local commits and can change their SHAs. Commit or stash working changes first.",
                "Rebase"))
            return;

        if (SelectedRepository?.Path != repository.Path || CurrentBranch != branch)
            return;

        await RunFeatureMutationAsync(
            path => _linearRebase.RebaseOntoReferenceAsync(path, reference, branch),
            $"Rebase onto {targetLabel} complete.");
    }
}
