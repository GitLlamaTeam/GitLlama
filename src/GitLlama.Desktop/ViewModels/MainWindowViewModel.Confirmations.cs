namespace GitLlama.Desktop.ViewModels;

public sealed record ActionConfirmation(string Title, string Message, string Action);

public partial class MainWindowViewModel
{
    public Func<ActionConfirmation, Task<bool>>? RequestConfirmation { get; set; }

    private async Task<bool> ConfirmActionAsync(string title, string message, string action, Func<bool>? stillSelected = null)
    {
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var repository = SelectedRepository?.Path;
        var branch = CurrentBranch;
        // No UI host (or a closed host) must never imply approval.
        if (RequestConfirmation is not { } request || !await request(new(title, message, action)))
            return false;
        if (version == Interlocked.Read(ref _repositorySelectionVersion)
            && SelectedRepository?.Path == repository
            && CurrentBranch == branch
            && (stillSelected?.Invoke() ?? true))
            return true;

        Status = PresentError("The repository, branch or selected target changed. Review the action again.",
            "Action canceled");
        return false;
    }
}
