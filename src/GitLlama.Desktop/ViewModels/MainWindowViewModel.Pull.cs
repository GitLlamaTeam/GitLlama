using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private GitPullMode pullMode = GitPullMode.Merge;

    public string PullModeCaption => PullMode switch
    {
        GitPullMode.FastForwardOnly => "fast-forward only",
        GitPullMode.Rebase => "rebase",
        _ => "fast-forward if possible"
    };

    partial void OnPullModeChanged(GitPullMode value)
    {
        OnPropertyChanged(nameof(PullModeCaption));
        OnPropertyChanged(nameof(PullToolTip));
        if (!_loadingSettings) _ = PersistAsync();
    }

    [RelayCommand]
    private void SelectPullMode(GitPullMode mode) => PullMode = mode;

    [RelayCommand]
    private Task ContinuePullRebaseAsync() => RunFeatureMutationAsync(async path =>
    {
        await _remotes.ContinuePullRebaseAsync(path, PullCommitter());
    }, "Rebase continued.");

    [RelayCommand]
    private Task AbortPullRebaseAsync() => RunFeatureMutationAsync(
        path => _remotes.AbortPullRebaseAsync(path), "Rebase aborted.");

    private LibGit2Sharp.Signature PullCommitter() =>
        string.IsNullOrWhiteSpace(AuthorName) || string.IsNullOrWhiteSpace(AuthorEmail)
            ? new("GitLlama", "gitllama@localhost", DateTimeOffset.Now)
            : new(AuthorName, AuthorEmail, DateTimeOffset.Now);
}
