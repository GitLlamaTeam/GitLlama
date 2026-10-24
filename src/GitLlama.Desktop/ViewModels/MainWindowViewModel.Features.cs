using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;
using GitLlama.Git;
using GitLlama.Infrastructure;
using GitLlama.Providers;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly GitWorktreeService _worktrees = new();
    private readonly GitPartialStageService _partialStage = new();
    private readonly IHostingProvider _hostingProvider;
    private readonly bool _ownsHostingProvider;
    private long _hostingRevision;
    private long _featureRevision;
    private long _historyPageRevision;
    private readonly List<GitCommitNode> _loadedGraphNodes = [];
    public ObservableCollection<GitWorktreeInfo> Worktrees { get; } = [];
    public ObservableCollection<GitReflogInfo> Reflog { get; } = [];
    public ObservableCollection<GitSubmoduleInfo> Submodules { get; } = [];

    [ObservableProperty]
    private GitSubmoduleInfo? selectedSubmodule;
    public ObservableCollection<GitFileInfo> RepositoryFiles { get; } = new BatchObservableCollection<GitFileInfo>();
    public ObservableCollection<GitFileInfo> FilteredRepositoryFiles { get; } = new BatchObservableCollection<GitFileInfo>();
    public ObservableCollection<GitPatchLine> PatchLines { get; } = [];
    public ObservableCollection<string> HistoryScopes { get; } = ["All branches", "Current branch"];
    public ObservableCollection<HostingItem> PullRequests { get; } = [];
    public ObservableCollection<HostingItem> Issues { get; } = [];
    public ObservableCollection<HostingComment> HostingComments { get; } = [];

    [ObservableProperty]
    private string cloneUrl = "";

    [ObservableProperty]
    private string newRepositoryDestination = "";

    /// <summary>The branch a new repository starts on.</summary>
    [ObservableProperty]
    private string newRepositoryBranch = "main";

    /// <summary>Whether a new repository gets a first commit with a README, so its branch exists straight away.</summary>
    [ObservableProperty]
    private bool newRepositoryInitialCommit = true;

    [ObservableProperty]
    private string worktreeName = "";

    [ObservableProperty]
    private string worktreeDestination = "";

    [ObservableProperty]
    private string worktreeBranch = "";

    [ObservableProperty]
    private GitWorktreeInfo? selectedWorktree;

    [ObservableProperty]
    private GitReflogInfo? selectedReflog;

    [ObservableProperty]
    private string recoveredBranchName = "";

    [ObservableProperty]
    private GitFileInfo? selectedRepositoryFile;

    [ObservableProperty]
    private string fileSearch = "";

    [ObservableProperty]
    private string selectedFilePreview = "Choose a file to view its committed content.";

    [ObservableProperty]
    private string selectedHistoryScope = "All branches";

    [ObservableProperty]
    private string historyAuthorFilter = "";

    [ObservableProperty]
    private DateTimeOffset? historySince;

    [ObservableProperty]
    private DateTimeOffset? historyUntil;

    [ObservableProperty]
    private bool hasMoreHistory;

    [ObservableProperty]
    private bool isFeatureBusy;

    [ObservableProperty]
    private bool isNetworkBusy;

    [ObservableProperty]
    private string networkProgressText = "";

    [ObservableProperty]
    private string networkUsername = "";

    [ObservableProperty]
    private string networkSecret = "";

    [ObservableProperty]
    private string networkCredentialHost = "";

    [ObservableProperty]
    private string remoteLocalBranchName = "";

    [ObservableProperty]
    private bool reviewStagedPatch;

    [ObservableProperty]
    private GitPatchLine? selectedPatchLine;

    [ObservableProperty]
    private bool amendStagedCommit;

    [ObservableProperty]
    private string conflictResultText = "";

    [ObservableProperty]
    private string githubOwner = "";

    [ObservableProperty]
    private string githubRepository = "";

    [ObservableProperty]
    private string githubToken = "";

    [ObservableProperty]
    private string hostingState = "open";

    [ObservableProperty]
    private bool isHostingBusy;

    [ObservableProperty]
    private string hostingStatus = "Choose a GitHub repository and refresh to load items.";

    [ObservableProperty]
    private HostingItem? selectedPullRequest;

    [ObservableProperty]
    private HostingItem? selectedIssue;

    [ObservableProperty]
    private string hostingDraftTitle = "";

    [ObservableProperty]
    private string hostingDraftBody = "";

    [ObservableProperty]
    private string hostingComment = "";

    [ObservableProperty]
    private string pullRequestHead = "";

    [ObservableProperty]
    private string pullRequestBase = "main";

    [ObservableProperty]
    private string hostingDetails = "Select a pull request or issue.";
    private int _pullPage = 1,
        _issuePage = 1;

    [ObservableProperty]
    private bool hasMorePullRequests;

    [ObservableProperty]
    private bool hasMoreIssues;

    [ObservableProperty]
    private bool isPullRequestWorkspace = true;
    public IReadOnlyList<string> HostingStates { get; } = ["open", "closed", "all"];
    public string HistoryReference =>
        SelectedHistoryScope == "All branches" ? ""
        : SelectedHistoryScope == "Current branch" ? "HEAD"
        : SelectedHistoryScope;

    private void ResetFeatures()
    {
        Interlocked.Increment(ref _featureRevision);
        Interlocked.Increment(ref _hostingRevision);
        Interlocked.Increment(ref _historyPageRevision);
        CancelNetwork();
        Worktrees.Clear();
        Reflog.Clear();
        RepositoryFiles.Clear();
        FilteredRepositoryFiles.Clear();
        PatchLines.Clear();
        Submodules.Clear();
        SelectedSubmodule = null;
        OnPropertyChanged(nameof(HasSubmodules));
        ResetLfs();
        SelectedWorktree = null;
        SelectedReflog = null;
        SelectedRepositoryFile = null;
        _loadedGraphNodes.Clear();
        HasMoreHistory = false;
        SelectedHistoryScope = "All branches";
        HistoryAuthorFilter = "";
        HistorySince = HistoryUntil = null;
        NetworkSecret = "";
        NetworkUsername = "";
        NetworkCredentialHost = "";
        PullRequests.Clear();
        Issues.Clear();
        HostingComments.Clear();
        SelectedPullRequest = SelectedIssue = null;
        HostingDetails = "Select a pull request or issue.";
        HostingStatus = "Choose a GitHub repository and refresh to load items.";
        GithubOwner = GithubRepository = "";
        _pullPage = _issuePage = 1;
    }

    private void UpdateNetworkAndHostingTarget(GitRemoteInfo? remote)
    {
        var host = Uri.TryCreate(remote?.Url, UriKind.Absolute, out var uri) ? uri.Authority : "";
        if (!string.Equals(host, NetworkCredentialHost, StringComparison.OrdinalIgnoreCase))
        {
            NetworkSecret = "";
            NetworkUsername = "";
        }

        NetworkCredentialHost = host;
        var repository = GitHubProvider.FromRemote(remote?.Url ?? "");
        if (remote is not null && AzureDevOpsRemote.Parse(remote.Url) is not null)
        {
            GithubOwner = GithubRepository = "";
            InvalidateHosting(clearPulls: false);
            if (RemotePullRequestRepositories().Length == 0)
                HostingStatus = "Azure pull requests and Boards are not connected yet. Use Integrations for Azure repository access.";
        }

        if (repository is not null && (GithubOwner != repository.Owner || GithubRepository != repository.Name))
        {
            Interlocked.Increment(ref _hostingRevision);
            GithubOwner = repository.Owner;
            GithubRepository = repository.Name;
            Issues.Clear();
            if (!IsPullRequestWorkspace) HostingComments.Clear();
            SelectedIssue = null;
            HasMoreIssues = false;
            _pullPage = _issuePage = 1;
            if (!IsPullRequestWorkspace) HostingDetails = "Select a pull request or issue.";
        }
    }

    internal GitNetworkOptions NetworkOptions(long version, bool cloning = false, string? remoteUrl = null) =>
        new()
        {
            AzurePersonalAccessToken = AzurePatFor(remoteUrl ?? (cloning ? CloneUrl : SelectedRemote?.Url ?? "")),
            AzureOAuthTokenProvider = AzureOAuthProviderFor(remoteUrl ?? (cloning ? CloneUrl : SelectedRemote?.Url ?? "")),
            AllowedAzureOrganizations = AzureOrganizations.Select(org => org.Name).ToArray(),
            AzureCredentialScopeUrl = AzureScopeFor(remoteUrl ?? (cloning ? CloneUrl : SelectedRemote?.Url ?? "")),
            Username =
                string.IsNullOrWhiteSpace(NetworkSecret)
                && NetworkCredentialHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(GithubUsableToken)
                    ? "x-access-token"
                    : NetworkUsername,
            Secret =
                string.IsNullOrWhiteSpace(NetworkSecret)
                && NetworkCredentialHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    ? GithubUsableToken
                    : NetworkSecret,
            CredentialHost = NetworkCredentialHost,
            UseCredentialManager = UseCredentialManager,
            RepositoryPath = cloning ? "" : SelectedRepository?.Path ?? "",
            UseSshAgent = UseSshAgent,
            SshPrivateKeyPath = SshPrivateKeyPath,
            SshSession = SshSession,
            SshHelperCommand =
                GitSystemTransport.QuoteSshArgument(Environment.ProcessPath!)
                + (
                    string.Equals(
                        Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                        "dotnet",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? " "
                            + GitSystemTransport.QuoteSshArgument(
                                Path.Combine(AppContext.BaseDirectory, "GitLlama.dll")
                            )
                        : ""
                ),
            Progress = new Progress<GitNetworkProgress>(p =>
            {
                if (IsNetworkBusy && version == Interlocked.Read(ref _repositorySelectionVersion))
                {
                    NetworkProgressText = p.Description;
                }
            }),
        };

    partial void OnCloneUrlChanged(string value)
    {
        var host = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Authority : "";
        if (!string.Equals(host, NetworkCredentialHost, StringComparison.OrdinalIgnoreCase))
        {
            NetworkSecret = "";
            NetworkUsername = "";
        }

        NetworkCredentialHost = host;
    }

    partial void OnAmendStagedCommitChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(CommitMessage) && SelectedRepository is { } repository)
        {
            try
            {
                using var repo = new LibGit2Sharp.Repository(repository.Path);
                CommitMessage = repo.Head.Tip?.Message ?? "";
            }
            catch (Exception error) { Status = PresentError(error); }
        }
        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(CanCommit));
        CommitCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CancelNetwork()
    {
        FetchRemoteCommand.Cancel();
        PushRemoteCommand.Cancel();
        PullFastForwardCommand.Cancel();
        CloneRepositoryCommand.Cancel();
        PushSelectedTagCommand.Cancel();
        DeleteSelectedRemoteTagCommand.Cancel();
        UpdateSubmoduleCommand.Cancel();
        OpenSubmoduleCommand.Cancel();
        SwitchSubmoduleCommand.Cancel();
        MergePullRequestIntoCurrentCommand.Cancel();
        RefreshPullRequestsCommand.Cancel();
        AddSubmoduleCommand.Cancel();
        DownloadLfsFilesCommand.Cancel();
        ChangeRemoteBranchCommand.Cancel();
    }

    public bool RepositoryCreationSucceeded { get; private set; }
    public string RepositoryCreationFeedback { get; private set; } = "";

    [RelayCommand]
    private async Task InitializeRepositoryAsync()
    {
        if (IsFeatureBusy)
        {
            return;
        }

        RepositoryCreationSucceeded = false;
        RepositoryCreationFeedback = "";
        IsFeatureBusy = true;
        try
        {
            var created = await new GitRepositoryLifecycleService().InitializeAsync(
                NewRepositoryDestination,
                NewRepositoryBranch,
                NewRepositoryInitialCommit,
                ProfileOrGitIdentityName(),
                ProfileOrGitIdentityEmail()
            );
            RepositoryPath = created.Path;
            await AddRepositoryCommand.ExecuteAsync(null);
            RepositoryCreationSucceeded =
                string.IsNullOrWhiteSpace(RepositoryPath)
                && SelectedRepository?.Path == Path.GetFullPath(NewRepositoryDestination.Trim());
            if (RepositoryCreationSucceeded)
            {
                Status = RepositoryCreationFeedback =
                    created.Committed ? "Repository initialized with a first commit."
                    : NewRepositoryInitialCommit
                        ? "Repository initialized. Set your name and email in Settings to make the first commit."
                    : "Repository initialized. Make a first commit to create the branch.";
            }
        }
        catch (Exception e)
        {
            Status = RepositoryCreationFeedback = PresentError(e);
        }
        finally
        {
            IsFeatureBusy = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CloneRepositoryAsync(CancellationToken ct)
    {
        if (IsNetworkBusy)
        {
            return;
        }

        RepositoryCreationSucceeded = false;
        RepositoryCreationFeedback = "";
        IsNetworkBusy = true;
        try
        {
            var network = NetworkOptions(Interlocked.Read(ref _repositorySelectionVersion), cloning: true);
            RepositoryPath = await new GitRepositoryLifecycleService().CloneAsync(
                CloneUrl,
                NewRepositoryDestination,
                network,
                ct
            );
            await AddRepositoryCommand.ExecuteAsync(null);
            RepositoryCreationSucceeded =
                string.IsNullOrWhiteSpace(RepositoryPath)
                && SelectedRepository?.Path == Path.GetFullPath(NewRepositoryDestination.Trim());
            if (RepositoryCreationSucceeded)
            {
                Status = RepositoryCreationFeedback = "Clone complete.";
            }
        }
        catch (OperationCanceledException)
        {
            Status = RepositoryCreationFeedback = "Clone canceled; the destination may contain downloaded files.";
        }
        catch (Exception e)
        {
            Status = RepositoryCreationFeedback = PresentError(e);
        }
        finally
        {
            IsNetworkBusy = false;
        }
    }

    private async Task RunFeatureMutationAsync(Func<string, Task> action, string success)
    {
        var repository = SelectedRepository;
        if (repository is null)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        IsFeatureBusy = true;
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await action(repository.Path);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await RefreshAsync();
            Status = success;
        }
        catch (Exception e)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            IsFeatureBusy = false;
            _gitMutationLock.Release();
        }
    }

    private string SafeMessage(Exception e)
    {
        var message = e.Message;
        foreach (
            var secret in new[] { NetworkSecret, GithubToken, AzurePatInput }.Concat(_azurePersonalAccessTokens.Values)
        )
        {
            if (!string.IsNullOrEmpty(secret))
            {
                message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        return SecretRedactor.Redact(message);
    }

    [RelayCommand]
    private Task CreateWorktreeAsync()
    {
        var name = WorktreeName;
        var destination = WorktreeDestination;
        var branch = WorktreeBranch;
        return RunFeatureMutationAsync(
            path => _worktrees.CreateAsync(path, name, destination, branch),
            "Worktree created."
        );
    }

    [RelayCommand]
    private async Task OpenWorktreeAsync()
    {
        if (SelectedWorktree is not { } worktree)
        {
            return;
        }

        RepositoryPath = worktree.Path;
        await AddRepositoryCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task ToggleWorktreeLockAsync()
    {
        var selected = SelectedWorktree;
        return selected is null
            ? Task.CompletedTask
            : RunFeatureMutationAsync(
                path => _worktrees.SetLockedAsync(path, selected.Name, !selected.IsLocked),
                "Worktree lock updated."
            );
    }

    [RelayCommand]
    private async Task RemoveWorktreeAsync()
    {
        if (SelectedWorktree is not { } selected)
        {
            return;
        }

        if (!await ConfirmActionAsync("Remove worktree?",
            $"Remove worktree {selected.Name}? Its folder will be preserved in a backup location. The main worktree and locked worktrees cannot be removed.",
            "Remove worktree", () => SelectedWorktree == selected))
            return;
        var confirmation = selected.Name;
        var backup = "";
        await RunFeatureMutationAsync(
            async path => backup = await _worktrees.RemoveAsync(path, selected.Name, confirmation),
            "Worktree removed."
        );
        if (backup.Length > 0)
        {
            Status = $"Worktree removed. Folder preserved at {backup}";
        }
    }

    [RelayCommand]
    private Task RecoverReflogBranchAsync()
    {
        var selected = SelectedReflog;
        var name = RecoveredBranchName;
        return selected is null
            ? Task.CompletedTask
            : RunFeatureMutationAsync(
                path => new GitReflogService().RecoverBranchAsync(path, selected.Sha, name),
                "Recovery branch created without changing working files."
            );
    }

    [RelayCommand]
    private Task SaveConflictResultAsync()
    {
        var path = ConflictPath;
        var result = ConflictResultText;
        return RunFeatureMutationAsync(
            repo => _conflictResolution.WriteAndResolveAsync(repo, path, result),
            "Conflict result saved and staged."
        );
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task UpdateSubmoduleAsync(CancellationToken ct)
    {
        var module = SelectedSubmodule;
        if (module is null)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        IsNetworkBusy = true;
        try
        {
            await RunFeatureMutationAsync(
                repo => new GitSubmoduleService().UpdateAsync(repo, module.Name, NetworkOptions(version), ct),
                "Submodule updated."
            );
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
        }
    }

    [RelayCommand]
    private Task StageAllAsync() => StageFilesAsync(false);

    [RelayCommand]
    private Task UnstageAllAsync() => StageFilesAsync(true);

    private Task StageFilesAsync(bool unstage)
    {
        var files = WorkingFiles.Where(file => !unstage || file.IsStaged).Select(file => file.Path).ToArray();
        return RunFeatureMutationAsync(
            async path =>
            {
                foreach (var file in files)
                {
                    if (unstage)
                    {
                        await _gitActions.UnstageAsync(path, file);
                    }
                    else
                    {
                        await _gitActions.StageAsync(path, file);
                    }
                }
            },
            unstage ? "Files unstaged." : "Files staged."
        );
    }

    partial void OnReviewStagedPatchChanged(bool value)
    {
        UpdatePatchLines();
        NotifyCenterReview();
        NotifyWorkingSelection();
        if (IsWorkingFileReview && FileReviewMode == "File")
        {
            _ = LoadWorkingContentAsync();
        }
    }

    private void UpdatePatchLines()
    {
        PatchLines.Clear();
        SelectedPatchLine = null;
        foreach (var line in GitPartialStageService.Lines(ReviewStagedPatch ? StagedDiffText : UnstagedDiffText))
        {
            PatchLines.Add(line);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplySelectedLine))]
    private Task ApplySelectedLineAsync() => ApplyPartialAsync(false);

    [RelayCommand(CanExecute = nameof(CanApplySelectedHunk))]
    private Task ApplySelectedHunkAsync() => ApplyPartialAsync(true);

    private async Task ApplyPartialAsync(bool hunk)
    {
        if (SelectedWorkingFile is not { } file || SelectedPatchLine is not { } selected)
        {
            return;
        }

        var indexes = hunk
            ? PatchLines.Where(line => line.Hunk == selected.Hunk && line.IsChange).Select(line => line.Index).ToArray()
            : [selected.Index];
        var expected = ReviewStagedPatch ? StagedDiffText : UnstagedDiffText;
        var unstage = ReviewStagedPatch;
        await RunFeatureMutationAsync(
            path => _partialStage.ApplyAsync(path, file.Path, expected, indexes, unstage),
            unstage ? "Selected changes unstaged." : "Selected changes staged."
        );
        SelectedWorkingFile = WorkingFiles.FirstOrDefault(f => f.Path == file.Path);
        IsWorkingTreeInspector = true;
    }

    partial void OnFileSearchChanged(string value) => FilterRepositoryFiles();

    private void FilterRepositoryFiles()
    {
        CollectionUpdates.Sync(FilteredRepositoryFiles,
            RepositoryFiles.Where(f => f.Path.Contains(FileSearch, StringComparison.OrdinalIgnoreCase)), file => file.Path);
    }

    partial void OnSelectedRepositoryFileChanged(GitFileInfo? value) => _ = PreviewRepositoryFileAsync(value);

    private async Task PreviewRepositoryFileAsync(GitFileInfo? file)
    {
        var revision = Interlocked.Increment(ref _featureRevision);
        var repo = SelectedRepository;
        SelectedFilePreview = "Choose a file to view its committed content.";
        if (file is null || repo is null)
        {
            return;
        }

        FileHistoryPath = BlamePath = file.Path;
        OpenFileReview("Repository", file.Path);
        try
        {
            var content = await _historyContent.GetAsync(repo.Path, "HEAD", file.Path);
            if (revision == Interlocked.Read(ref _featureRevision))
            {
                SelectedFilePreview = content.Content;
            }
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _featureRevision))
            {
                SelectedFilePreview = PresentError(e);
            }
        }
    }

    partial void OnHistoryAuthorFilterChanged(string value) => FilterPresentationHistory();

    partial void OnHistorySinceChanged(DateTimeOffset? value) => FilterPresentationHistory();

    partial void OnHistoryUntilChanged(DateTimeOffset? value) => FilterPresentationHistory();

    partial void OnSelectedHistoryScopeChanged(string value)
    {
        if (!_changingScopeForBranchReveal && HasRepository && _loadedGraphNodes.Count > 0)
        {
            _ = ReloadHistoryAsync();
        }
    }

    [RelayCommand]
    private Task ReloadHistoryAsync() => ReadHistoryPageAsync(true);

    [RelayCommand]
    private Task LoadMoreHistoryAsync() => ReadHistoryPageAsync(false);

    private async Task ReadHistoryPageAsync(bool reset)
    {
        var repo = SelectedRepository;
        if (repo is null)
        {
            return;
        }

        if (!reset && (_gitMutationLock.CurrentCount == 0 || RefreshCommand.IsRunning))
        {
            return;
        }

        var refreshRevision = Interlocked.Read(ref _refreshRevision);
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var scope = SelectedHistoryScope;
        var revision = Interlocked.Increment(ref _historyPageRevision);
        try
        {
            var page = await _commitGraph.GetPageAsync(
                repo.Path,
                reset ? 0 : _loadedGraphNodes.Count,
                200,
                string.IsNullOrEmpty(HistoryReference) ? null : HistoryReference
            );
            if (
                version != Interlocked.Read(ref _repositorySelectionVersion)
                || scope != SelectedHistoryScope
                || revision != Interlocked.Read(ref _historyPageRevision)
                || refreshRevision != Interlocked.Read(ref _refreshRevision)
            )
            {
                return;
            }

            if (reset)
            {
                _loadedGraphNodes.Clear();
            }

            _loadedGraphNodes.AddRange(page.Commits.Where(node => !_loadedGraphNodes.Any(n => n.Sha == node.Sha)));
            HasMoreHistory = page.HasMore;
            RebuildPagedHistory();
        }
        catch (Exception e)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
    }

    private async Task<GitHistoryPage> GetRefreshHistoryAsync(string path)
    {
        var desired = Math.Max(200, _loadedGraphNodes.Count);
        var nodes = new List<GitCommitNode>();
        var more = true;
        var reference = string.IsNullOrEmpty(HistoryReference) ? null : HistoryReference;
        while (more && nodes.Count < desired)
        {
            var page = await _commitGraph.GetPageAsync(
                path,
                nodes.Count,
                Math.Min(1000, desired - nodes.Count),
                reference
            );
            nodes.AddRange(page.Commits);
            more = page.HasMore;
        }

        return new(nodes, more);
    }

    private void RebuildPagedHistory()
    {
        _historyGraphShas.Clear();
        CommitLines.Clear();
        _commitDetails.Clear();
        foreach (var node in _loadedGraphNodes)
        {
            _historyGraphShas[$"{node.Sha}  {node.Subject}  {string.Join(' ', node.Labels)}"] = node.Sha;
            var line = $"{node.Sha}  {node.Subject} — {node.Author}";
            CommitLines.Add(line);
            _commitDetails[line] = new GitCommitSummary(node.Sha, node.Subject, node.Author, node.Timestamp);
        }

        FilterHistoryGraph();
        SetHistoryRows(_loadedGraphNodes);
        OnPropertyChanged(nameof(HistoryCaption));
    }

    private async Task RefreshFeatureListsAsync(
        string path, long version, long refreshVersion, IReadOnlyList<ChangedFile> changes)
    {
        var worktrees = await _worktrees.ListAsync(path);
        var reflog = await new GitReflogService().ListAsync(path);
        var files = await new GitFileBrowserService().ListAsync(path, changes);
        var conflicts = await new GitConflictDetectionService().ListAsync(path);
        var submodules = await new GitSubmoduleService().ListAsync(path);
        var lfs = await new GitLfsService().GetStatusAsync(path);
        if (
            version != Interlocked.Read(ref _repositorySelectionVersion)
            || refreshVersion != Interlocked.Read(ref _refreshRevision)
        )
        {
            return;
        }

        var selectedWorktreeName = SelectedWorktree?.Name;
        Worktrees.Clear();
        foreach (var tree in worktrees)
        {
            Worktrees.Add(tree);
        }

        SelectedWorktree = Worktrees.FirstOrDefault(w => w.Name == selectedWorktreeName);
        Reflog.Clear();
        foreach (var entry in reflog)
        {
            Reflog.Add(entry);
        }

        CollectionUpdates.Sync(RepositoryFiles, files, file => file.Path);

        FilterRepositoryFiles();
        MergeConflicts.Clear();
        foreach (var conflict in conflicts)
        {
            MergeConflicts.Add(conflict.Path);
        }

        ConflictSummary = $"{MergeConflicts.Count} unresolved conflicts.";
        var selectedSubmoduleName = SelectedSubmodule?.Name;
        Submodules.Clear();
        foreach (var module in submodules)
        {
            Submodules.Add(module);
        }

        SelectedSubmodule = Submodules.FirstOrDefault(m => m.Name == selectedSubmoduleName);
        OnPropertyChanged(nameof(HasSubmodules));
        ApplyLfsStatus(lfs);

        if (
            SelectedHistoryScope is not ("All branches" or "Current branch")
            && !BranchLines.Contains(SelectedHistoryScope)
        )
        {
            SelectedHistoryScope = "All branches";
        }

        for (var i = HistoryScopes.Count - 1; i >= 2; i--)
        {
            if (!BranchLines.Contains(HistoryScopes[i]))
            {
                HistoryScopes.RemoveAt(i);
            }
        }

        foreach (var branch in BranchLines)
        {
            if (!HistoryScopes.Contains(branch))
            {
                HistoryScopes.Add(branch);
            }
        }
    }
}
