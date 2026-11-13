using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

/// <summary>Where the checked-out branch pushes to and pulls from, shown and changed from the toolbar.</summary>
public partial class MainWindowViewModel
{
    private readonly GitUpstreamService _upstreams = new();

    /// <summary>The checked-out branch's upstream, or null when it has none.</summary>
    [ObservableProperty]
    private GitUpstream? upstream;

    /// <summary>The remote the toolbar pushes to and fetches from: the upstream's, or origin, or the only remote.</summary>
    public string? PushRemoteName =>
        Upstream?.Remote ?? GitUpstreamService.DefaultRemote(Remotes.Select(remote => remote.Name).ToArray());

    /// <summary>The branch on that remote that Push updates.</summary>
    public string PushBranchName => Upstream?.Branch ?? CurrentBranch;

    public bool HasUpstream => Upstream is not null;

    public string UpstreamCaption =>
        Upstream?.Name
        ?? (
            PushRemoteName is not null ? "Not set"
            : Remotes.Count == 0 ? "No remote"
            : "Choose…"
        );

    public string UpstreamDetail =>
        Upstream is { Published: false } pending ? $"{pending.Name} will be created by your next push."
        : Upstream is { } upstream
            ? (
                upstream.Ahead == 0 && upstream.Behind == 0
                    ? $"{CurrentBranch} is up to date with {upstream.Name}."
                    : $"{CurrentBranch} is {upstream.Ahead} ahead and {upstream.Behind} behind {upstream.Name}."
            )
        : PushRemoteName is { } remote ? $"Push will create {remote}/{CurrentBranch} and track it."
        : Remotes.Count == 0 ? "Add a remote to push and pull."
        : "Choose a remote branch to push to and pull from.";

    /// <summary>Marks a destination that will be created on the next push.</summary>
    public string UpstreamCounts =>
        Upstream is { Published: false } ? "new"
        : "";

    public int PushCommitCount => Upstream is { Published: true } value ? value.Ahead : 0;
    public int PullCommitCount => Upstream is { Published: true } value ? value.Behind : 0;
    public bool HasPushCommits => PushCommitCount > 0;
    public bool HasPullCommits => PullCommitCount > 0;

    public string PushToolTip =>
        PushRemoteName is { } remote
            ? $"Push {CurrentBranch} to {remote}/{PushBranchName}"
            : "Choose where to push with the upstream picker";

    public string PullToolTip =>
        Upstream is { Published: true } upstream ? $"Pull {upstream.Name} into {CurrentBranch} ({PullModeCaption})"
        : Upstream is { } pending ? $"{pending.Name} isn't on the server yet, so there's nothing to pull"
        : "Choose an upstream to pull from with the upstream picker";

    public string FetchToolTip =>
        PushRemoteName is { } remote ? $"Fetch {remote}" : "Choose a remote with the upstream picker to fetch it";

    partial void OnUpstreamChanged(GitUpstream? value) => NotifyUpstream();

    partial void OnCurrentBranchChanged(string value) => NotifyUpstream();

    private void NotifyUpstream()
    {
        foreach (
            var name in new[]
            {
                nameof(PushRemoteName),
                nameof(PushBranchName),
                nameof(HasUpstream),
                nameof(UpstreamCaption),
                nameof(UpstreamDetail),
                nameof(UpstreamCounts),
                nameof(PushCommitCount),
                nameof(PullCommitCount),
                nameof(HasPushCommits),
                nameof(HasPullCommits),
                nameof(PushToolTip),
                nameof(PullToolTip),
                nameof(FetchToolTip),
            }
        )
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Pushes the checked-out branch to its upstream, or to origin when it has none yet.</summary>
    [RelayCommand]
    private async Task PushToUpstreamAsync()
    {
        if (PushRemoteName is not { } name || Remotes.FirstOrDefault(remote => remote.Name == name) is not { } target)
        {
            Status = RemoteOperationFeedback =
                Remotes.Count == 0
                    ? "Add a remote to push to."
                    : "Choose where to push with the upstream picker next to the branch.";
            return;
        }

        SelectedRemote = target;
        await PushRemoteCommand.ExecuteAsync(null);
    }

    /// <summary>Fetches the upstream's remote, or origin when the branch has no upstream yet.</summary>
    [RelayCommand]
    private async Task FetchUpstreamAsync()
    {
        if (PushRemoteName is not { } name || Remotes.FirstOrDefault(remote => remote.Name == name) is not { } target)
        {
            Status =
                Remotes.Count == 0
                    ? "Add a remote to fetch from."
                    : "Choose a remote with the upstream picker next to the branch.";
            return;
        }

        SelectedRemote = target;
        await FetchRemoteCommand.ExecuteAsync(null);
    }

    /// <summary>Makes the checked-out branch push to and pull from a branch on a remote.</summary>
    public Task SetUpstreamToAsync(string remote, string branch) =>
        RunFeatureMutationAsync(
            repo => _upstreams.SetAsync(repo, remote, branch),
            $"{CurrentBranch} now pushes to and pulls from {remote}/{branch.Trim()}."
        );

    /// <summary>Stops the checked-out branch tracking a remote branch.</summary>
    public Task StopTrackingAsync() =>
        RunFeatureMutationAsync(
            repo => _remotes.ClearCurrentBranchUpstreamAsync(repo),
            $"{CurrentBranch} no longer tracks a remote branch."
        );

    /// <summary>Remote branch names on a remote, without the remote prefix, from the last fetch.</summary>
    public IReadOnlyList<string> RemoteBranchesOf(string remote) =>
        BranchLines
            .Where(line =>
                line.StartsWith(remote + "/", StringComparison.Ordinal)
                && !line.EndsWith("/HEAD", StringComparison.Ordinal)
            )
            .Select(line => line[(remote.Length + 1)..])
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
}
