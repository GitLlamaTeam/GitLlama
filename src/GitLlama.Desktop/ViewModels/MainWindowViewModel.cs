using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Application;
using GitLlama.Core;
using GitLlama.Git;
using GitLlama.Infrastructure;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly RepositorySessionManager _sessions;
    private readonly GitWorkingTreeService _gitActions;
    private readonly GitDiffService _diffs = new();
    private readonly GitWorkingTreeReviewService _workingReview = new();

    [ObservableProperty]
    private bool sideBySideDiff;

    [ObservableProperty]
    private string diffBeforeText = "";

    [ObservableProperty]
    private string diffAfterText = "";

    partial void OnSideBySideDiffChanged(bool value) => UpdateSideBySideDiff();

    private void UpdateSideBySideDiff()
    {
        var source = UnstagedDiffText == "No unstaged changes for this file." ? StagedDiffText : UnstagedDiffText;
        var projection = GitWorkingTreeReviewService.SideBySide(source);
        DiffBeforeText = projection.Before;
        DiffAfterText = projection.After;
    }

    [ObservableProperty]
    private string selectedDiffHunk = "";

    [ObservableProperty]
    private string workingTreeReviewSummary = "Select a changed file to review its hunks.";
    public ObservableCollection<string> UnstagedHunks { get; } = [];

    private readonly GitIdentityService _identity = new();
    private readonly GitTagService _tags = new();
    private readonly GitStashService _stashes = new();
    private readonly GitCommitAmendService _commitAmend = new();
    private readonly GitCherryPickService _cherryPick = new();
    private readonly GitLinearRebaseService _linearRebase = new();

    [ObservableProperty]
    private string editableRebasePlan = "";

    [RelayCommand]
    private async Task ExecuteEditableRebasePlanAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var target = RebaseTargetBranch;
        var plan = EditableRebasePlan;
        if (!await ConfirmActionAsync("Rewrite local history?",
            $"Execute the edited rebase plan for {CurrentBranch} onto {target}? This rewrites local commits. Review the plan before continuing.",
            "Execute rebase", () => RebaseTargetBranch == target && EditableRebasePlan == plan))
            return;
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            var lines = plan.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var steps = new List<GitRebaseStep>();
            var messages = new Dictionary<string, string>(StringComparer.Ordinal);
            var edits = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !Enum.TryParse<GitRebaseAction>(parts[0], true, out var action))
                {
                    throw new ArgumentException("Each plan line must be: action full-sha [message for reword].");
                }

                if (parts[1].Length != 40 || !parts[1].All(Uri.IsHexDigit))
                {
                    throw new ArgumentException("Use full 40-character commit SHAs.");
                }

                steps.Add(new GitRebaseStep(parts[1], parts.Length > 2 ? parts[2] : "", action));
                if (action == GitRebaseAction.Reword && parts.Length > 2)
                {
                    messages[parts[1]] = parts[2];
                }

                if (action == GitRebaseAction.Edit)
                {
                    // Format: edit FULL_SHA relative/path.txt|BASE64_UTF8
                    if (parts.Length < 3)
                    {
                        throw new ArgumentException("Edit needs path|base64-utf8-content.");
                    }

                    var separator = parts[2].IndexOf('|');
                    if (separator <= 0)
                    {
                        throw new ArgumentException("Edit needs path|base64-utf8-content.");
                    }

                    var relativePath = parts[2][..separator];
                    var content = System.Text.Encoding.UTF8.GetString(
                        Convert.FromBase64String(parts[2][(separator + 1)..])
                    );
                    edits[parts[1]] = new Dictionary<string, string> { [relativePath] = content };
                }
            }

            var sha = await _linearRebase.RebaseWithEditsAsync(repo.Path, target, steps, messages, edits);

            await RefreshAsync();
            Status = $"Rebase complete: {sha[..7]}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Execute editable rebase plan");
            Status = PresentError(e);
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [ObservableProperty]
    private string rebaseTargetBranch = "";

    [RelayCommand]
    private async Task RebaseCurrentOntoTargetAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var target = RebaseTargetBranch;
        if (!await ConfirmActionAsync("Rebase current branch?",
            $"Rebase {CurrentBranch} onto {target}? This rewrites local commit history. A clean working tree is required; published commits are protected.",
            "Rebase", () => RebaseTargetBranch == target))
            return;

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var sha = await _linearRebase.RebaseOntoAsync(repo.Path, target);

            await RefreshAsync();
            Status = $"Rebased current branch onto {RebaseTargetBranch}: {sha[..7]}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Rebase branch");
            Status = PresentError(e);
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    private readonly GitSequencerRecoveryService _sequencerRecovery = new();

    [ObservableProperty]
    private string sequencerCommitMessage = "Resolve operation";

    [RelayCommand]
    private async Task ContinueSequencerAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        await _gitMutationLock.WaitAsync();
        try
        {
            var sha = await _sequencerRecovery.ContinueAsync(repo.Path, SequencerCommitMessage);
            await RefreshAsync();
            Status = $"Operation committed: {sha[..7]}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Continue cherry-pick/revert");
            Status = PresentError(e);
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task AbortSequencerAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        await _gitMutationLock.WaitAsync();
        try
        {
            await _sequencerRecovery.AbortAsync(repo.Path);
            await RefreshAsync();
            Status = "Cherry-pick/revert aborted.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Abort cherry-pick/revert");
            Status = PresentError(e);
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    private readonly GitMergeService _mergeOperation = new();
    private readonly GitMergeLifecycleService _mergeLifecycle = new();
    private readonly GitConflictResolutionService _conflictResolution = new();
    private readonly GitThreeWayConflictService _threeWayConflict = new();
    private readonly GitConflictChoiceService _conflictChoices = new();

    [RelayCommand]
    private Task AcceptOursAsync() => AcceptConflictSideAsync(GitConflictChoice.Ours);

    [RelayCommand]
    private Task AcceptTheirsAsync() => AcceptConflictSideAsync(GitConflictChoice.Theirs);

    private async Task AcceptConflictSideAsync(GitConflictChoice choice)
    {
        if (SelectedRepository is not { } repo || string.IsNullOrWhiteSpace(ConflictPath))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _conflictChoices.ResolveAsync(repo.Path, ConflictPath, choice);
            await InspectMergeConflictsAsync();
            Status = $"Resolved {ConflictPath} using {choice}.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Accept conflict stage");
            Status = PresentError(e);
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [ObservableProperty]
    private string conflictBaseText = "";

    [ObservableProperty]
    private string conflictOursText = "";

    [ObservableProperty]
    private string conflictTheirsText = "";

    partial void OnConflictPathChanged(string value) => _ = InspectThreeWayConflictAsync(value);

    private async Task InspectThreeWayConflictAsync(string path)
    {
        ConflictBaseText = ConflictOursText = ConflictTheirsText = "";
        if (SelectedRepository is not { } repo || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var stages = await _threeWayConflict.InspectAsync(repo.Path, path);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || ConflictPath != path)
            {
                return;
            }

            ConflictBaseText = stages.Base ?? "(file absent in base)";
            ConflictOursText = stages.Ours ?? "(file absent in ours)";
            ConflictTheirsText = stages.Theirs ?? "(file absent in theirs)";
            ConflictResultText = stages.Ours ?? stages.Theirs ?? "";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect three-way conflict");
            if (version == Interlocked.Read(ref _repositorySelectionVersion) && ConflictPath == path)
            {
                ConflictBaseText = PresentError(e);
            }
        }
    }

    [ObservableProperty]
    private string mergeCommitMessage = "";

    [ObservableProperty]
    private string conflictPath = "";

    [ObservableProperty]
    private string conflictSummary = "No conflicts inspected.";
    public ObservableCollection<string> MergeConflicts { get; } = [];

    [RelayCommand]
    private async Task MergeSelectedBranchAsync()
    {
        if (SelectedRepository is not { } repo || SelectedBranchLine is not { } branch)
        {
            return;
        }

        var name = branch.Trim().TrimStart('*').Trim();
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var result = await _mergeOperation.MergeAsync(repo.Path, name);
            Status = $"Merge {name}: {result}";
            await RefreshAsync();
            await InspectMergeConflictsAsync();
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Merge branch");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }

            await InspectMergeConflictsAsync();
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task InspectMergeConflictsAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        try
        {
            var result = await new GitConflictDetectionService().ListAsync(repo.Path);
            MergeConflicts.Clear();
            foreach (var conflict in result)
            {
                MergeConflicts.Add(conflict.Path);
            }

            ConflictSummary = $"{MergeConflicts.Count} unresolved conflict(s).";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect merge conflicts");
            ConflictSummary = PresentError(e);
        }
    }

    [RelayCommand]
    private async Task ResolveConflictUsingWorkingTreeAsync()
    {
        if (SelectedRepository is not { } repo || string.IsNullOrWhiteSpace(ConflictPath))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _conflictResolution.ResolveUsingWorkingTreeAsync(repo.Path, ConflictPath);
            await InspectMergeConflictsAsync();
            Status = $"Marked {ConflictPath} resolved from working tree.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Resolve conflict");
            Status = PresentError(e);
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanFinishMerge))]
    private async Task ContinueMergeAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            var sha = await _mergeLifecycle.ContinueAsync(repo.Path, MergeCommitMessage, AuthorName, AuthorEmail);
            MergeCommitMessage = "";
            await RefreshAsync();
            Status = $"Merge completed: {sha[..7]}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Continue merge");
            Status = PresentError(e);
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(IsMergeInProgress))]
    private async Task AbortMergeAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _mergeLifecycle.AbortAsync(repo.Path);
            await RefreshAsync();
            Status = "Merge aborted.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Abort merge");
            Status = PresentError(e);
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    private readonly GitRevertService _revert = new();
    private readonly GitSoftResetService _softReset = new();
    private readonly GitMixedResetService _mixedReset = new();
    private readonly GitHardResetService _hardReset = new();
    private readonly GitBranchFromCommitService _branchFromCommit = new();

    [ObservableProperty]
    private string branchFromCommitName = "";

    [ObservableProperty]
    private string amendedCommitMessage = "";
    public ObservableCollection<GitStashInfo> Stashes { get; } = [];

    [ObservableProperty]
    private string stashMessage = "";

    [ObservableProperty]
    private GitStashInfo? selectedStash;

    public ObservableCollection<GitTagInfo> Tags { get; } = [];

    [ObservableProperty]
    private string newTagName = "";

    [ObservableProperty]
    private string annotatedTagName = "";

    [ObservableProperty]
    private string annotatedTagMessage = "";

    [ObservableProperty]
    private string annotatedTagAuthorName = "";

    [ObservableProperty]
    private string annotatedTagAuthorEmail = "";

    [ObservableProperty]
    private GitTagInfo? selectedTag;

    private readonly GitHistoryWorkspaceService _historyWorkspace = new();
    private readonly GitCommitFilePatchService _historyPatch = new();
    private readonly GitCommitFileContentService _historyContent = new();
    private long _historySelectionRevision;
    private long _historyFileRevision;
    private readonly Dictionary<string, string> _historyFilePaths = new(StringComparer.Ordinal);
    public ObservableCollection<string> HistoryGraphLines { get; } = [];
    public ObservableCollection<string> HistoryFiles { get; } = [];

    [ObservableProperty]
    private string historySearch = "";

    [ObservableProperty]
    private string? selectedHistoryGraphLine;

    [ObservableProperty]
    private string? selectedHistoryFile;

    [ObservableProperty]
    private string historyCommitDetails = "Select a commit in History.";

    [ObservableProperty]
    private string historyFilePreview = "Select a changed file to inspect its patch.";

    [ObservableProperty]
    private bool historyShowContent;
    private readonly Dictionary<string, string> _historyGraphShas = new(StringComparer.Ordinal);
    private GitHistoryCommit? _activeHistoryCommit;

    partial void OnHistorySearchChanged(string value)
    {
        FilterHistoryGraph();
        FilterPresentationHistory();
    }

    private void FilterHistoryGraph()
    {
        HistoryGraphLines.Clear();
        foreach (
            var line in _historyGraphShas.Keys.Where(line =>
                string.IsNullOrWhiteSpace(HistorySearch)
                || line.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            HistoryGraphLines.Add(line);
        }
    }

    partial void OnSelectedHistoryGraphLineChanged(string? value)
    {
        if (value is not null && _historyGraphShas.TryGetValue(value, out var sha))
        {
            var matching = _commitDetails.FirstOrDefault(entry => entry.Value.Sha == sha);
            if (matching.Key is not null)
            {
                SelectedCommitLine = matching.Key;
            }
            else
            {
                _ = LoadHistoryCommitAsync(sha);
            }
        }
    }

    partial void OnSelectedHistoryFileChanged(string? value) => _ = LoadHistoryFileAsync(value);

    partial void OnHistoryShowContentChanged(bool value) => _ = LoadHistoryFileAsync(SelectedHistoryFile);

    private async Task LoadHistoryCommitAsync(string sha)
    {
        var revision = Interlocked.Increment(ref _historySelectionRevision);
        var repository = SelectedRepository;
        var selectionVersion = Interlocked.Read(ref _repositorySelectionVersion);
        var sameCommit = _activeHistoryCommit?.Sha == sha;
        var previousFile = sameCommit ? SelectedHistoryFileRow?.Path : null;
        if (!sameCommit)
        {
            ClearInspectorFileSelection();
            _activeHistoryCommit = null;
            HistoryFiles.Clear();
            HistoryFileRows.Clear();
            SelectedHistoryFileRow = null;
            _historyFilePaths.Clear();
            SelectedHistoryFile = null;
            if (!IsUpdatingContextSnapshot)
            {
                HistoryFilePreview = "Select a changed file to inspect its patch.";
            }

            HistoryCommitDetails = "Loading commit...";
        }

        if (repository is null)
        {
            return;
        }

        try
        {
            var commit = await _historyWorkspace.InspectAsync(repository.Path, sha);
            if (
                revision != Interlocked.Read(ref _historySelectionRevision)
                || selectionVersion != Interlocked.Read(ref _repositorySelectionVersion)
            )
            {
                return;
            }

            _activeHistoryCommit = commit;
            HistorySubject = commit.Subject;
            HistoryAuthor = commit.Author;
            HistoryAvatar = _allHistoryRows.FirstOrDefault(row => row.Sha == commit.Sha)?.AuthorAvatar ?? HistoryAvatar;
            HistorySha = commit.Sha[..7];
            HistoryDate = commit.Date.ToString("yyyy-MM-dd HH:mm:ss zzz");
            HistoryMessage = CommitDescription(commit.Message);
            if (Stashes.FirstOrDefault(stash => stash.Sha == commit.Sha) is { } stash)
            {
                var (branch, description) = DescribeStash(stash.Message);
                HistorySubject = description;
                HistoryAvatar = AvatarCache.ForAuthor(commit.Author, stash.AuthorEmail, commit.Sha);
                HistoryMessage = $"Stashed on {branch} as stash@{{{stash.Index}}}.";
            }

            HistoryCommitDetails =
                $"Commit: {commit.Sha}{Environment.NewLine}Author: {commit.Author}{Environment.NewLine}"
                + $"Date: {commit.Date:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}"
                + $"Parents: {(commit.Parents.Count == 0 ? "(root)" : string.Join(", ", commit.Parents))}{Environment.NewLine}"
                + $"Refs: {(commit.Labels.Count == 0 ? "(none)" : string.Join(", ", commit.Labels))}{Environment.NewLine}"
                + $"Files: {commit.Files.Count}{Environment.NewLine}{commit.Message}";
            var rows = commit.Files.Select(HistoryFileRow.From).Select(WithLfs).ToArray();
            CollectionUpdates.Sync(HistoryFileRows, rows, file => file.Path);
            CollectionUpdates.Sync(HistoryFiles, rows.Select(file => file.Line), line => line);
            _historyFilePaths.Clear();
            foreach (var file in rows)
            {
                _historyFilePaths[file.Line] = file.Path;
            }

            if (previousFile is not null)
            {
                SelectedHistoryFileRow = HistoryFileRows.FirstOrDefault(file => file.Path == previousFile);
            }
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Load unified history commit");
            if (
                revision == Interlocked.Read(ref _historySelectionRevision)
                && selectionVersion == Interlocked.Read(ref _repositorySelectionVersion)
            )
            {
                HistoryCommitDetails = PresentError(e);
            }
        }
    }

    private async Task LoadHistoryFileAsync(string? line)
    {
        var revision = Interlocked.Increment(ref _historyFileRevision);
        var repository = SelectedRepository;
        var selectionVersion = Interlocked.Read(ref _repositorySelectionVersion);
        var commit = _activeHistoryCommit;
        if (!IsUpdatingContextSnapshot)
        {
            HistoryFilePreview = "Select a changed file to inspect its patch.";
        }

        if (repository is null || commit is null || line is null || !_historyFilePaths.TryGetValue(line, out var path))
        {
            return;
        }

        try
        {
            string result;
            if (HistoryShowContent)
            {
                var content = await _historyContent.GetAsync(repository.Path, commit.Sha, path);
                result = content.Content;
            }
            else
            {
                var patch = await _historyPatch.GetAsync(repository.Path, commit.Sha, path);
                result = patch.PatchText;
            }

            if (
                revision == Interlocked.Read(ref _historyFileRevision)
                && selectionVersion == Interlocked.Read(ref _repositorySelectionVersion)
            )
            {
                HistoryFilePreview = result;
            }
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Load history file preview");
            if (
                revision == Interlocked.Read(ref _historyFileRevision)
                && selectionVersion == Interlocked.Read(ref _repositorySelectionVersion)
            )
            {
                HistoryFilePreview = PresentError(e);
            }
        }
    }

    private readonly GitCommitInspectionService _commitInspection = new();
    private readonly Dictionary<string, GitCommitSummary> _commitDetails = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string configuredIdentity = "No repository selected";

    [ObservableProperty]
    private string localIdentityName = "";

    [ObservableProperty]
    private string localIdentityEmail = "";
    private readonly GitBranchService _branches = new();

    [ObservableProperty]
    private string tagInspectionSummary = "Select an action to inspect.";

    [ObservableProperty]
    private string fileHistoryPath = "";

    [ObservableProperty]
    private string fileHistorySummary = "Select an action to inspect.";

    [ObservableProperty]
    private string blameSummary = "Select an action to inspect.";

    [ObservableProperty]
    private string blamePath = "";

    [ObservableProperty]
    private string graphLaneSummary = "Select an action to inspect.";

    [ObservableProperty]
    private string commitSearchSummary = "Select an action to inspect.";

    [ObservableProperty]
    private string commitSearchQuery = "";

    [ObservableProperty]
    private string ancestryCommitSha = "";

    [ObservableProperty]
    private string ancestrySummary = "Enter a commit SHA to inspect its parents.";

    [ObservableProperty]
    private string descendantCommitSha = "";

    [ObservableProperty]
    private string descendantSummary = "Enter a commit SHA to inspect its descendants.";

    [ObservableProperty]
    private string mergeBaseFirstSha = "";

    [ObservableProperty]
    private string mergeBaseSecondSha = "";

    [ObservableProperty]
    private string mergeBaseSummary = "Enter two commit SHAs to find their common ancestor.";

    [ObservableProperty]
    private string relationshipFirstSha = "";

    [ObservableProperty]
    private string relationshipSecondSha = "";

    [ObservableProperty]
    private string relationshipSummary = "Enter two commit SHAs to compare their ancestry.";

    [ObservableProperty]
    private string diffStatisticsCommitSha = "";

    [ObservableProperty]
    private string diffStatisticsSummary = "Enter a commit SHA to inspect its change statistics.";

    [ObservableProperty]
    private string changedFilesCommitSha = "";

    [ObservableProperty]
    private string changedFilesSummary = "Enter a commit SHA to list changed files.";

    [ObservableProperty]
    private string fileStatisticsCommitSha = "";

    [ObservableProperty]
    private string fileStatisticsSummary = "Enter a commit SHA to inspect per-file changes.";

    [ObservableProperty]
    private string filePatchCommitSha = "";

    [ObservableProperty]
    private string filePatchPath = "";

    [ObservableProperty]
    private string filePatchSummary = "Enter a commit SHA and changed file path to inspect its patch.";

    [ObservableProperty]
    private string fileContentCommitSha = "";

    [ObservableProperty]
    private string fileContentPath = "";

    [ObservableProperty]
    private string fileContentSummary = "Enter a commit SHA and path to preview a historical file.";
    private readonly GitCommitGraphService _commitGraph = new();

    [ObservableProperty]
    private string commitGraphSummary = "Load the commit graph to inspect parent relationships and refs.";
    private readonly GitRebaseConflictService _rebaseConflicts = new();

    [ObservableProperty]
    private string rebaseConflictSummary = "Select Inspect to view the current rebase state and unresolved paths.";
    private readonly GitRebasePlanService _rebasePlanner = new();

    [ObservableProperty]
    private string rebaseUpstreamSha = "";

    [ObservableProperty]
    private string rebasePlanPreview = "Enter an ancestor SHA to preview commits. No history will be rewritten.";
    private readonly GitRemoteService _remotes = new();
    public ObservableCollection<GitRemoteInfo> Remotes { get; } = [];
    public ObservableCollection<GitRemoteBranchInfo> RemoteBranches { get; } = [];

    [ObservableProperty]
    private GitRemoteBranchInfo? selectedRemoteBranch;

    [ObservableProperty]
    private GitRemoteInfo? selectedRemote;

    [ObservableProperty]
    private string newRemoteName = "";

    [ObservableProperty]
    private string newRemoteUrl = "";

    [ObservableProperty]
    private string upstreamBranchName = "";

    partial void OnSelectedRemoteChanged(GitRemoteInfo? value)
    {
        if (_refreshingRemoteList)
        {
            return;
        }

        UpdateNetworkAndHostingTarget(value);
        _ = LoadRemoteBranchesAsync(value);
    }

    [ObservableProperty]
    private string newBranchName = "";

    [ObservableProperty]
    private string renameBranchName = "";
    private long _diffRevision;

    [ObservableProperty]
    private string stagedDiffText = "";

    [ObservableProperty]
    private string unstagedDiffText = "";
    private readonly Dictionary<string, string> _changePaths = new(StringComparer.Ordinal);
    public ObservableCollection<string> CommitLines { get; } = [];

    [ObservableProperty]
    private string? selectedCommitLine;

    [ObservableProperty]
    private string? selectedChangeLine;

    [ObservableProperty]
    private string? selectedBranchLine;

    [ObservableProperty]
    private string inspectorText = "Select a commit, changed file or branch to inspect it.";

    partial void OnSelectedCommitLineChanged(string? value)
    {
        if (value is not null && _commitDetails.TryGetValue(value, out var commit))
        {
            InspectorText =
                $"Commit: {commit.Sha}\nAuthor: {commit.Author}\nDate: {commit.When:yyyy-MM-dd HH:mm:ss zzz}\nSubject: {commit.Message}";
        }

        if (value is not null && _commitDetails.TryGetValue(value, out var historyCommit))
        {
            _ = LoadHistoryCommitAsync(historyCommit.Sha);
        }
        else if (value is not null)
        {
            InspectorText = value;
        }
    }

    partial void OnSelectedChangeLineChanged(string? value)
    {
        SelectedWorkingFile = WorkingFiles.FirstOrDefault(file => file.Line == value);
        if (value is not null)
        {
            InspectorText = value;
            if (!IsUpdatingContextSnapshot)
            {
                IsWorkingTreeInspector = true;
            }
        }

        _ = LoadSelectedDiffAsync(value);
    }

    private async Task LoadSelectedDiffAsync(string? selection)
    {
        var revision = Interlocked.Increment(ref _diffRevision);
        var repository = SelectedRepository;
        var selectionVersion = Interlocked.Read(ref _repositorySelectionVersion);
        if (!IsUpdatingContextSnapshot)
        {
            StagedDiffText = "";
            UnstagedDiffText = "";
        }

        if (repository is null || selection is null || !_changePaths.TryGetValue(selection, out var path))
        {
            return;
        }

        try
        {
            var result = await _diffs.GetFileDiffAsync(repository.Path, path);
            if (
                revision != Interlocked.Read(ref _diffRevision)
                || selectionVersion != Interlocked.Read(ref _repositorySelectionVersion)
            )
            {
                return;
            }

            StagedDiffText = string.IsNullOrWhiteSpace(result.StagedPatch)
                ? "No staged changes for this file."
                : result.StagedPatch;
            UnstagedDiffText = string.IsNullOrWhiteSpace(result.UnstagedPatch)
                ? "No unstaged changes for this file."
                : result.UnstagedPatch;
            UnstagedHunks.Clear();
            foreach (
                var hunk in result
                    .UnstagedPatch.Split('\n')
                    .Where(line => line.StartsWith("@@ ", StringComparison.Ordinal))
            )
            {
                UnstagedHunks.Add(hunk);
            }

            WorkingTreeReviewSummary = $"{UnstagedHunks.Count} unstaged hunk(s) · {path}";
            UpdateSideBySideDiff();
            UpdatePatchLines();
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Load file diff");
            if (
                revision == Interlocked.Read(ref _diffRevision)
                && selectionVersion == Interlocked.Read(ref _repositorySelectionVersion)
            )
            {
                UnstagedDiffText = PresentError(e);
            }
        }
    }

    partial void OnSelectedBranchLineChanged(string? value)
    {
        if (value is null || IsUpdatingContextSnapshot)
        {
            return;
        }

        InspectorText = "Branch: " + value;
    }

    public ObservableCollection<string> ChangeLines { get; } = new BatchObservableCollection<string>();
    public ObservableCollection<string> BranchLines { get; } = [];
    private readonly JsonSettingsStore _settings;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    public Task Initialization { get; }
    private bool _initialized;
    private bool _loadingSettings = true;
    private long _repositorySelectionVersion;
    private bool _updatingRepositoryFavorite;
    private bool _refreshingRemoteList;
    private long _settingsRevision;
    private long _refreshRevision;
    private readonly SemaphoreSlim _gitMutationLock = new(1, 1);
    public ObservableCollection<RepositoryInfo> Repositories { get; } = [];
    public ObservableCollection<string> Tabs { get; } = ["History"];
    public IReadOnlyList<string> LayoutNames { get; } = ["Standard", "Review", "Merge Resolution"];

    [ObservableProperty]
    private string selectedLayout = "Standard";
    public IReadOnlyList<string> AvailableTabs { get; } =
    [
        "History",
        "Files",
        "Pull Requests",
        "Issues",
        "Branches",
        "Tags",
        "Stashes",
        "Remotes",
        "Repository",
        "Worktrees",
        "Reflog",
        "Submodules",
        "Advanced History",
        "Working Tree Tools",
        "Remote Tools",
        "Identity",
        "Settings",
    ];

    [ObservableProperty]
    private RepositoryInfo? selectedRepository;

    [ObservableProperty]
    private string selectedTab = "History";

    [ObservableProperty]
    private string currentBranch = "No repository";

    [ObservableProperty]
    private string status = "Ready";

    [ObservableProperty]
    private string repositoryPath = "";

    [ObservableProperty]
    private string newTab = "History";

    [ObservableProperty]
    private string stagingSummary = "No staged changes";

    [ObservableProperty]
    private string commitMessage = "";

    [ObservableProperty]
    private string authorName = "";

    [ObservableProperty]
    private string authorEmail = "";

    public MainWindowViewModel(
        RepositorySessionManager sessions,
        JsonSettingsStore settings,
        GitWorkingTreeService gitActions,
        IHostingProvider? hostingProvider = null,
        Func<GitLlama.Providers.GitHubDeviceOAuth>? githubOAuthFactory = null,
        Func<
            string,
            string,
            CancellationToken,
            Task<IReadOnlyList<GitLlama.Providers.AzureRepository>>
        >? azureRepositoryLoader = null
    )
    {
        // Stage and check out LFS files through git-lfs, as command-line Git does.
        GitLfsFilter.EnsureRegistered();
        _sessions = sessions;
        _settings = settings;
        _gitActions = gitActions;
        SshSession = new SshKeySession(Path.Combine(settings.DirectoryPath, "ssh-known-hosts.json"));
        if (githubOAuthFactory is not null)
        {
            GithubOAuthFactory = githubOAuthFactory;
        }

        if (azureRepositoryLoader is not null)
        {
            AzureRepositoryLoader = azureRepositoryLoader;
        }

        _hostingProvider = hostingProvider ?? new GitLlama.Providers.GitHubProvider();
        _ownsHostingProvider = hostingProvider is null;
        InitializePullRequestTree();
        InitializePresentation();
        Initialization = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var data = await _settings.LoadAsync();
            foreach (var repository in data.Repositories)
            {
                Repositories.Add(repository);
            }

            if (data.Tabs is { Count: > 0 })
            {
                Tabs.Clear();
                foreach (var tab in data.Tabs.Distinct().Where(AvailableTabs.Contains))
                {
                    Tabs.Add(tab);
                }

                if (Tabs.Count == 0)
                {
                    Tabs.Add("History");
                }
            }

            SelectedTab = Tabs.Contains(data.ActiveTab) ? data.ActiveTab : Tabs.FirstOrDefault() ?? "History";
            SelectedLayout = LayoutNames.Contains(data.LayoutName) ? data.LayoutName : "Standard";
            AutoRefresh = data.AutoRefresh;
            LoadAvatars = data.LoadAvatars;
            InitializeProfiles(data);
            SelectedRepository = Repositories.FirstOrDefault(r => r.Path == data.ActiveRepository);
            if (data.OpenRepositoryPaths is { Count: > 0 } paths)
            {
                RepositoryWorkspaceTabs.Clear();
                foreach (var path in paths)
                {
                    var repository = Repositories.FirstOrDefault(repo => repo.Path == path);
                    if (repository is not null && !ValidateRepositoryForOpen(repository))
                        continue;
                    if (path is null || repository is not null)
                    {
                        RepositoryWorkspaceTabs.Add(new RepositoryWorkspaceTab { Repository = repository });
                    }
                }

                if (RepositoryWorkspaceTabs.Count > 0)
                {
                    SelectedRepositoryWorkspaceTab = RepositoryWorkspaceTabs[
                        Math.Clamp(data.ActiveRepositoryTab, 0, RepositoryWorkspaceTabs.Count - 1)
                    ];
                }
            }

            _initialized = true;
            await RestoreProfileIntegrationsAsync();
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Load settings");
            Status = PresentError(e);
        }
        finally
        {
            if (RepositoryWorkspaceTabs.Count == 0)
            {
                NewRepositoryTab();
            }

            try
            {
                await _repositoryActivation;
            }
            finally
            {
                _loadingSettings = false;
                _initialized = true;
                IsStarting = false;
            }
        }
    }

    partial void OnSelectedRepositoryChanged(RepositoryInfo? oldValue, RepositoryInfo? newValue)
    {
        var value = newValue;
        if (_restoringRejectedRepositorySelection)
            return;
        if (value is not null && !ValidateRepositoryForOpen(value))
        {
            _restoringRejectedRepositorySelection = true;
            try { SelectedRepository = oldValue; }
            finally { _restoringRejectedRepositorySelection = false; }
            return;
        }
        if (_updatingRepositoryFavorite)
        {
            return;
        }

        RemoteOperationFeedback = "";
        ClearGithubPushAccessFeedback();
        UpdateRepositoryBreadcrumbs();
        // Long-running git-lfs processes keep their repository folder in use; start fresh for the new one.
        GitLfsFilter.Shutdown();
        if (value is not null)
        {
            OpenRepositoryTab(value);
        }

        var version = Interlocked.Increment(ref _repositorySelectionVersion);
        ResetPresentation();
        ResetFeatures();
        ResetFileRevisionPreview();
        _ = ConfigureWatcherAsync();
        BranchLines.Clear();
        CommitLines.Clear();
        AuthorName = AuthorEmail = CommitMessage = "";
        SelectedChangeLine = null;
        _changePaths.Clear();
        ChangeLines.Clear();
        _commitDetails.Clear();
        Remotes.Clear();
        Tags.Clear();
        Stashes.Clear();
        SelectedStash = null;

        _inspectedStashSha = null;
        UpdateStashTree();
        SelectedTag = null;

        SelectedRemote = null;
        StagingSummary = "No staged changes";
        ConfiguredIdentity = value is null ? "No repository selected" : "Loading Git identity...";
        LocalIdentityName = "";
        LocalIdentityEmail = "";
        if (value is null)
        {
            _repositoryActivation = Task.CompletedTask;
            CurrentBranch = "No repository";
            CommitLines.Clear();
            ChangeLines.Clear();
            BranchLines.Clear();
            InspectorText = "Select a repository to inspect it.";
        }
        else
        {
            _repositoryActivation = ActivateAsync(value, version);
        }

        if (!_loadingSettings)
        {
            _ = PersistAsync();
        }
    }

    private async Task ActivateAsync(RepositoryInfo? repository, long version)
    {
        if (repository is null)
        {
            return;
        }

        try
        {
            var snapshot = await _sessions.GetSession(repository.Path).OpenAsync();
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            _currentHistoryHeadSha = snapshot.HeadSha;
            CurrentBranch = snapshot.Branch ?? "Detached HEAD";
            Status = $"Opened repository: {repository.Name}";
            await RefreshAsync();
            await LoadIdentityAsync(repository.Path, version);
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                await PersistAsync();
            }
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Open repository");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
    }

    private async Task LoadIdentityAsync(string path, long version)
    {
        try
        {
            var identity = await _identity.ReadAsync(path);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != path)
            {
                return;
            }

            LocalIdentityName = identity.Name ?? "";
            LocalIdentityEmail = identity.Email ?? "";
            ApplyProfileIdentity();
            ConfiguredIdentity =
                string.IsNullOrWhiteSpace(identity.Name) && string.IsNullOrWhiteSpace(identity.Email)
                    ? "No Git identity configured"
                    : $"{identity.Name ?? "(no name)"} <{identity.Email ?? "no email"}>";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Read Git identity");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                ConfiguredIdentity = "Unable to read Git identity: " + PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task SaveLocalIdentityAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var name = LocalIdentityName.Trim();
        var email = LocalIdentityEmail.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            Status = "Both author name and email are required.";
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await _identity.SetLocalAsync(repo.Path, name, email);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await LoadIdentityAsync(repo.Path, version);
            Status = "Saved repository-local Git identity; global Git settings were not changed.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Save local Git identity");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    partial void OnSelectedLayoutChanged(string value)
    {
        if (!_loadingSettings)
        {
            _ = PersistAsync();
        }
    }

    partial void OnSelectedTabChanged(string value)
    {
        if (!_loadingSettings)
        {
            _ = PersistAsync();
        }
    }

    private async Task PersistAsync()
    {
        if (!_initialized || _loadingSettings || _switchingProfile)
        {
            return;
        }

        var revision = Interlocked.Increment(ref _settingsRevision);
        await _saveLock.WaitAsync();
        try
        {
            if (revision != Interlocked.Read(ref _settingsRevision))
            {
                return;
            }

            CaptureActiveProfile();
            var snapshot = new GitLlamaSettings(
                Repositories.ToList(),
                SelectedRepository?.Root.Path,
                Tabs.ToList(),
                SelectedTab,
                LayoutName: SelectedLayout,
                AutoRefresh: AutoRefresh,
                OpenRepositoryPaths: RepositoryWorkspaceTabs.Select(tab => tab.Repository?.Root.Path).ToList(),
                ActiveRepositoryTab: RepositoryWorkspaceTabs.IndexOf(SelectedRepositoryWorkspaceTab!),
                UseCredentialManager: UseCredentialManager,
                UseSshAgent: UseSshAgent,
                SshPrivateKeyPath: SshPrivateKeyPath,
                SshPublicKeyPath: SshPublicKeyPath,
                Profiles: Profiles.ToList(),
                ActiveProfileId: ActiveProfile?.Id,
                LoadAvatars: LoadAvatars
            );
            await _settings.SaveAsync(snapshot);
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Repository workspace operation");
            Status = PresentError(e);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var repo = SelectedRepository;
        if (repo is null)
        {
            return;
        }

        using var contextSnapshot = BeginContextSnapshot();
        var previousHistorySha = SelectedHistoryRow?.Sha;
        var previousWorkingPath = SelectedWorkingFile?.Path;
        var previousHistoricalPath = SelectedHistoryFileRow?.Path;
        var previousReviewSource = ReviewSource;
        var wasFileReview = IsFileReview;
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var refreshVersion = Interlocked.Increment(ref _refreshRevision);
        Interlocked.Increment(ref _historyPageRevision);
        try
        {
            var snapshot = await _sessions.GetSession(repo.Path).OpenAsync();
            if (
                version != Interlocked.Read(ref _repositorySelectionVersion)
                || refreshVersion != Interlocked.Read(ref _refreshRevision)
            )
            {
                return;
            }

            _currentHistoryHeadSha = snapshot.HeadSha;
            CurrentBranch = snapshot.Branch ?? "Detached HEAD";
            var overview = await _sessions.GetSession(repo.Path).RefreshAsync();
            if (
                version != Interlocked.Read(ref _repositorySelectionVersion)
                || refreshVersion != Interlocked.Read(ref _refreshRevision)
            )
            {
                return;
            }

            Interlocked.Increment(ref _historySelectionRevision);
            Interlocked.Increment(ref _historyFileRevision);
            SelectedBranchLine = null;
            InspectorText = $"Repository: {repo.Name} · Branch: {CurrentBranch}";
            GitHistoryPage historyPage;
            string historyScope;
            do
            {
                historyScope = SelectedHistoryScope;
                historyPage = await GetRefreshHistoryAsync(repo.Path);
            } while (
                historyScope != SelectedHistoryScope
                && version == Interlocked.Read(ref _repositorySelectionVersion)
                && refreshVersion == Interlocked.Read(ref _refreshRevision)
            );
            if (
                version != Interlocked.Read(ref _repositorySelectionVersion)
                || refreshVersion != Interlocked.Read(ref _refreshRevision)
            )
            {
                return;
            }

            _loadedGraphNodes.Clear();
            _loadedGraphNodes.AddRange(historyPage.Commits);
            HasMoreHistory = historyPage.HasMore;
            RebuildPagedHistory();
            _changePaths.Clear();
            var workingRows = overview.Changes.Select(WorkingFileRow.From).Select(WithLfs).ToArray();
            CollectionUpdates.Sync(WorkingFiles, workingRows, file => file.Path);
            CollectionUpdates.Sync(ChangeLines, workingRows.Select(file => file.Line), line => line);
            RepositoryOperation = overview.Operation;
            if (HasMergeConflicts)
            {
                await InspectMergeConflictsAsync();
            }

            foreach (var change in overview.Changes)
            {
                var line = $"{change.State}  {change.Path}";
                _changePaths[line] = change.Path;
            }

            var stagedCount = WorkingFiles.Count(file => file.IsStaged);
            StagedFileCount = stagedCount;
            StagingSummary =
                stagedCount == 0 ? "No staged changes"
                : stagedCount == 1 ? "1 staged file"
                : $"{stagedCount} staged files";
            CollectionUpdates.Sync(BranchLines, overview.Branches, branch => branch);
            // Activation refresh can finish after a user has clicked a different commit.
            SelectedHistoryRow = _inspectedStashSha is not null
                ? null
                : HistoryRows.FirstOrDefault(r => r.Sha == SelectedHistoryRow?.Sha)
                    ?? HistoryRows.FirstOrDefault(r => r.Sha == previousHistorySha) ?? HistoryRows.FirstOrDefault();
            if (SelectedHistoryRow is { } clicked && clicked.Sha != previousHistorySha
                && _activeHistoryCommit?.Sha != clicked.Sha)
            {
                await LoadHistoryCommitAsync(clicked.Sha);
                if (version != Interlocked.Read(ref _repositorySelectionVersion)
                    || refreshVersion != Interlocked.Read(ref _refreshRevision)) return;
            }
            SelectedWorkingFile = WorkingFiles.FirstOrDefault(file => file.Path == previousWorkingPath);
            if (SelectedWorkingFile is { } selectedWorking)
            {
                await LoadSelectedDiffAsync(selectedWorking.Line);
            }

            Status = $"{overview.Commits.Count} recent commits · {overview.Changes.Count} changed files";
            await RefreshRemotesAsync(repo.Path, version, refreshVersion);
            var tags = await _tags.ListAsync(repo.Path);
            if (
                version != Interlocked.Read(ref _repositorySelectionVersion)
                || refreshVersion != Interlocked.Read(ref _refreshRevision)
            )
            {
                return;
            }

            var previousTag = SelectedTag?.Name;
            Tags.Clear();
            foreach (var tag in tags)
            {
                Tags.Add(tag);
            }

            SelectedTag = Tags.FirstOrDefault(tag => tag.Name == previousTag);
            var stashes = await _stashes.ListAsync(repo.Path);
            if (
                version != Interlocked.Read(ref _repositorySelectionVersion)
                || refreshVersion != Interlocked.Read(ref _refreshRevision)
            )
            {
                return;
            }

            var previousStash = SelectedStash?.Message;
            Stashes.Clear();
            foreach (var stash in stashes)
            {
                Stashes.Add(stash);
            }

            SelectedStash = Stashes.FirstOrDefault(stash => stash.Message == previousStash);
            UpdateStashTree();
            if (_inspectedStashSha is { } inspectedStash)
            {
                if (Stashes.Any(stash => stash.Sha == inspectedStash))
                {
                    // Indexes shift when stashes are added or removed, so refresh the header.
                    await LoadHistoryCommitAsync(inspectedStash);
                }
                else
                {
                    _inspectedStashSha = null;
                    SelectedHistoryRow = HistoryRows.FirstOrDefault();
                }
            }

            GitUpstream? tracked = null;
            try
            {
                tracked = await _upstreams.ReadAsync(repo.Path);
            }
            catch (Exception e) when (e is InvalidOperationException or LibGit2Sharp.LibGit2SharpException)
            {
                // Leave the upstream unknown; the toolbar offers to choose one.
            }

            var upstream =
                tracked is { Published: true } published ? $"{published.Ahead} ahead · {published.Behind} behind"
                : tracked is { } pending ? $"{pending.Name} not pushed yet"
                : "No upstream";

            if (
                version == Interlocked.Read(ref _repositorySelectionVersion)
                && refreshVersion == Interlocked.Read(ref _refreshRevision)
            )
            {
                UpstreamStatus = upstream;
                Upstream = tracked;
            }

            await RefreshFeatureListsAsync(repo.Path, version, refreshVersion, overview.Changes);
            if (
                version != Interlocked.Read(ref _repositorySelectionVersion)
                || refreshVersion != Interlocked.Read(ref _refreshRevision)
            )
            {
                return;
            }

            if (
                wasFileReview
                && previousReviewSource == "Commit"
                && previousHistoricalPath is not null
                && (_inspectedStashSha ?? SelectedHistoryRow?.Sha) is { } reviewedSha
            )
            {
                await LoadHistoryCommitAsync(reviewedSha);
                if (
                    version != Interlocked.Read(ref _repositorySelectionVersion)
                    || refreshVersion != Interlocked.Read(ref _refreshRevision)
                )
                {
                    return;
                }

                SelectedHistoryFileRow = HistoryFileRows.FirstOrDefault(file => file.Path == previousHistoricalPath);
            }
            else if (wasFileReview && previousReviewSource == "Working" && FileReviewMode == "File")
            {
                await LoadWorkingContentAsync();
            }
            else if (wasFileReview && previousReviewSource == "Repository")
            {
                await PreviewRepositoryFileAsync(SelectedRepositoryFile);
            }
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Refresh repository");
            if (
                version == Interlocked.Read(ref _repositorySelectionVersion)
                && refreshVersion == Interlocked.Read(ref _refreshRevision)
            )
            {
                Status = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (SelectedRepository is null)
        {
            return;
        }

        var index = Repositories.IndexOf(SelectedRepository);
        if (index < 0)
        {
            return;
        }

        var replacement = SelectedRepository with { IsFavorite = !SelectedRepository.IsFavorite };
        _updatingRepositoryFavorite = true;
        try
        {
            Repositories[index] = replacement;
            foreach (var tab in RepositoryWorkspaceTabs.Where(tab => tab.Repository?.Path == replacement.Path))
            {
                tab.Repository = replacement;
            }

            SelectedRepository = replacement;
        }
        finally
        {
            _updatingRepositoryFavorite = false;
        }

        await PersistAsync();
    }

    [RelayCommand]
    private async Task RemoveRepositoryAsync()
    {
        var selected = SelectedRepository;
        if (selected is null)
        {
            return;
        }

        Repositories.Remove(selected);
        SelectedRepository = Repositories.FirstOrDefault();
        if (SelectedRepository is null)
        {
            CurrentBranch = "No repository";
            CommitLines.Clear();
            ChangeLines.Clear();
            BranchLines.Clear();
            Status = "Repository removed from recent list (files were not deleted)";
        }

        await PersistAsync();
    }

    [RelayCommand]
    private async Task AddRepositoryAsync()
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath))
        {
            return;
        }

        try
        {
            var snapshot = await _sessions.GetSession(RepositoryPath).OpenAsync();
            var existing = Repositories.FirstOrDefault(r =>
                string.Equals(
                    r.Path,
                    snapshot.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                )
            );
            if (existing is null)
            {
                existing = new RepositoryInfo(
                    snapshot.Path,
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(snapshot.Path))
                );
                Repositories.Add(existing);
            }

            if (!OpenRepositoryTab(existing))
                return;
            RepositoryPath = "";
            await PersistAsync();
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Repository workspace operation");
            ReportRepositoryOpenError("repository", RepositoryPath, e);
        }
    }

    private async Task RefreshRemotesAsync(string path, long version, long refreshVersion)
    {
        var remotes = await _remotes.ListAsync(path);
        if (
            version != Interlocked.Read(ref _repositorySelectionVersion)
            || refreshVersion != Interlocked.Read(ref _refreshRevision)
        )
        {
            return;
        }

        var selectedName = SelectedRemote?.Name;
        _refreshingRemoteList = true;
        try
        {
            Remotes.Clear();
            foreach (var remote in remotes)
            {
                Remotes.Add(remote);
            }

            SelectedRemote =
                remotes.FirstOrDefault(remote => remote.Name == selectedName)
                ?? remotes.FirstOrDefault(remote => remote.Name == "origin")
                ?? remotes.FirstOrDefault();
        }
        finally
        {
            _refreshingRemoteList = false;
        }

        FilterBranches();
        UpdateNetworkAndHostingTarget(SelectedRemote);
        if (SelectedRemote is not null)
        {
            await LoadRemoteBranchesAsync(SelectedRemote);
        }
    }

    [RelayCommand]
    private async Task AddRemoteAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var name = NewRemoteName.Trim();
        var url = NewRemoteUrl.Trim();
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _remotes.AddAsync(repo.Path, name, url);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            NewRemoteName = "";
            NewRemoteUrl = "";
            await RefreshAsync();
            Status = $"Added remote {name}";
            SelectedRemote = Remotes.FirstOrDefault(remote => remote.Name == name);
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Add remote");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task FetchRemoteAsync(CancellationToken cancellationToken)
    {
        if (SelectedRepository is not { } repo || SelectedRemote is not { } remote)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            Status = $"Fetching {remote.Name}...";
            IsNetworkBusy = true;
            await _remotes.FetchAsync(repo.Path, remote.Name, cancellationToken, NetworkOptions(version));
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            MarkLfsLocksStale();
            await RefreshAsync();
            Status = $"Fetched {remote.Name}";
        }
        catch (Exception e)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = cancellationToken.IsCancellationRequested ? "Fetch canceled." : PresentError(e);
            }
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
            _gitMutationLock.Release();
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PushRemoteAsync(CancellationToken cancellationToken)
    {
        ClearGithubPushAccessFeedback();
        if (SelectedRepository is not { } repo)
        {
            Status = RemoteOperationFeedback = "Push failed: open a repository first.";
            return;
        }

        if (SelectedRemote is not { } remote)
        {
            Status = RemoteOperationFeedback = "Push failed: choose a remote in the Remotes section first.";
            return;
        }

        RemoteOperationFeedback = $"Preparing to push {CurrentBranch} to {remote.Name}…";
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var options = NetworkOptions(version, remoteUrl: remote.PushUrl ?? remote.Url);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                RemoteOperationFeedback = "Push canceled: the selected repository changed.";
                return;
            }

            Status = $"Pushing current branch and its tags to {remote.Name}...";
            RemoteOperationFeedback = Status;
            IsNetworkBusy = true;
            if (
                !await CheckLinkedGithubPushAccessAsync(remote.PushUrl ?? remote.Url, options.Secret, cancellationToken)
            )
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            // Push to the branch the current branch tracks on this remote, which can have a different name.
            var remoteBranch = Upstream is { } tracked && tracked.Remote == remote.Name ? tracked.Branch : null;
            var branch = await _remotes.PushCurrentBranchAsync(
                repo.Path,
                remote.Name,
                cancellationToken,
                options,
                remoteBranch
            );
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            MarkLfsLocksStale();
            await RefreshAsync();
            Status = RemoteOperationFeedback = $"Pushed {branch} and its tags to {remote.Name}/{remoteBranch ?? branch}.";
        }
        catch (Exception e)
        {
            var message = SafeMessage(e);
            if (!string.IsNullOrEmpty(options.Secret))
            {
                message = message.Replace(options.Secret, "[redacted]", StringComparison.Ordinal);
            }

            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = RemoteOperationFeedback = cancellationToken.IsCancellationRequested
                    ? "Push canceled; refresh the remote to check its state."
                    : PresentError($"Push to {remote.Name} failed: {message}", "Push failed");
            }

            if (
                !cancellationToken.IsCancellationRequested
                && version == Interlocked.Read(ref _repositorySelectionVersion)
            )
            {
                OfferGithubAccessAfterPushFailure(remote.PushUrl ?? remote.Url, options.Secret, message);
            }
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
            _gitMutationLock.Release();
        }
    }

    private async Task LoadRemoteBranchesAsync(GitRemoteInfo? remote)
    {
        var repo = SelectedRepository;
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        RemoteBranches.Clear();
        SelectedRemoteBranch = null;
        if (repo is null || remote is null)
        {
            return;
        }

        try
        {
            var branches = await _remotes.ListRemoteBranchesAsync(repo.Path, remote.Name);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRemote?.Name != remote.Name)
            {
                return;
            }

            foreach (var branch in branches)
            {
                RemoteBranches.Add(branch);
            }
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Load remote branches");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
    }

    partial void OnSelectedRemoteBranchChanged(GitRemoteBranchInfo? value)
    {
        if (value is not null)
        {
            UpstreamBranchName = value.BranchName;
        }
    }

    [RelayCommand]
    private async Task CheckoutRemoteBranchAsync()
    {
        if (SelectedRepository is not { } repo || SelectedRemoteBranch is not { } remoteBranch)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            var localName = string.IsNullOrWhiteSpace(RemoteLocalBranchName)
                ? remoteBranch.BranchName
                : RemoteLocalBranchName.Trim();
            BranchSwitchTarget = localName;
            IsBranchSwitching = true;
            await _branches.CheckoutRemoteBranchAsync(
                repo.Path,
                remoteBranch.RemoteName,
                remoteBranch.BranchName,
                localName
            );
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            CurrentBranch = localName;
            await RefreshAsync();
            Status = $"Checked out {localName} tracking {remoteBranch.RemoteName}/{remoteBranch.BranchName}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Checkout remote branch");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            IsBranchSwitching = false;
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task SetUpstreamAsync()
    {
        if (SelectedRepository is not { } repo || SelectedRemote is not { } remote)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _remotes.SetCurrentBranchUpstreamAsync(repo.Path, remote.Name, UpstreamBranchName.Trim());
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await RefreshAsync();
            Status = $"Tracking {remote.Name}/{UpstreamBranchName.Trim()}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Set upstream");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task ClearUpstreamAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _remotes.ClearCurrentBranchUpstreamAsync(repo.Path);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await RefreshAsync();
            Status = "Current branch upstream cleared";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Clear upstream");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PullFastForwardAsync(CancellationToken cancellationToken)
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        if (Upstream is not { Published: true })
        {
            Status = Upstream is { } pending
                ? $"{pending.Name} isn't on the server yet, so there's nothing to pull. Push first."
                : "Choose an upstream to pull from with the upstream picker next to the branch.";
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            Status = "Pulling tracked branch (fast-forward only)...";
            IsNetworkBusy = true;
            await _remotes.PullFastForwardAsync(repo.Path, cancellationToken, NetworkOptions(version));
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            MarkLfsLocksStale();
            await RefreshAsync();
            Status = "Pull complete";
        }
        catch (Exception e)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = cancellationToken.IsCancellationRequested
                    ? "Pull canceled; refresh to inspect the repository."
                    : PresentError(e);
            }
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task RemoveRemoteAsync()
    {
        if (SelectedRepository is not { } repo || SelectedRemote is not { } remote)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _remotes.RemoveAsync(repo.Path, remote.Name);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await RefreshAsync();
            Status = $"Removed remote {remote.Name}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Remove remote");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PushSelectedTagAsync(CancellationToken cancellationToken)
    {
        if (SelectedRepository is not { } repo || SelectedRemote is not { } remote || SelectedTag is not { } tag)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            IsNetworkBusy = true;
            await _tags.PushAsync(repo.Path, remote.Name, tag.Name, cancellationToken, NetworkOptions(version));
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = $"Pushed tag {tag.Name} to {remote.Name} (without force).";
            }
        }
        catch (Exception e)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = cancellationToken.IsCancellationRequested
                    ? "Tag push canceled; refresh the remote to inspect its state."
                    : PresentError(e);
            }
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
            _gitMutationLock.Release();
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task DeleteSelectedRemoteTagAsync(CancellationToken cancellationToken)
    {
        if (SelectedRepository is not { } repo || SelectedRemote is not { } remote || SelectedTag is not { } tag)
        {
            return;
        }

        if (!await ConfirmActionAsync(
            "Delete remote tag?",
            $"Delete tag {tag.Name} from {remote.Name} on the server? The local tag will be kept.",
            "Delete remote tag",
            () => SelectedRemote == remote && SelectedTag == tag))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            IsNetworkBusy = true;
            await _tags.DeleteRemoteAsync(repo.Path, remote.Name, tag.Name, cancellationToken, NetworkOptions(version));
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            Status = $"Deleted remote tag {tag.Name} from {remote.Name}; local tag preserved.";
        }
        catch (Exception e)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = cancellationToken.IsCancellationRequested
                    ? "Remote tag deletion canceled; refresh the remote to inspect its state."
                    : PresentError(e);
            }
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedTagAsync()
    {
        if (SelectedRepository is not { } repo || SelectedTag is not { } tag)
        {
            return;
        }

        var tagName = tag.Name;
        if (!await ConfirmActionAsync(
            "Delete local tag?",
            $"Delete local tag {tagName}? Remote tags will remain unchanged.",
            "Delete tag",
            () => SelectedTag == tag))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await _tags.DeleteAsync(repo.Path, tagName);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            SelectedTag = null;

            await RefreshAsync();
            Status = $"Deleted local tag {tagName}; remote tags were not changed.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Delete local tag");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task PopSelectedStashAsync(bool? allowLocalChanges)
    {
        if (SelectedRepository is not { } repo || SelectedStash is not { } stash)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var current = await _stashes.ListAsync(repo.Path);
            if (stash.Index >= current.Count || current[stash.Index].Message != stash.Message)
            {
                throw new InvalidOperationException("The selected stash changed. Refresh and select it again.");
            }

            await _stashes.PopAsync(repo.Path, stash.Index, allowLocalChanges == true);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            SelectedStash = null;
            await RefreshAsync();
            Status = $"Popped stash@{{{stash.Index}}}; entry removed after successful application.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Pop stash");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task ApplySelectedStashAsync(bool? allowLocalChanges)
    {
        if (SelectedRepository is not { } repo || SelectedStash is not { } stash)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var current = await _stashes.ListAsync(repo.Path);
            if (stash.Index >= current.Count || current[stash.Index].Message != stash.Message)
            {
                throw new InvalidOperationException("The selected stash changed. Refresh and select it again.");
            }

            await _stashes.ApplyAsync(repo.Path, stash.Index, allowLocalChanges == true);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await RefreshAsync();
            Status = $"Applied stash@{{{stash.Index}}}; stash entry was preserved.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Apply stash");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task DropSelectedStashAsync()
    {
        if (SelectedRepository is not { } repo || SelectedStash is not { } stash)
        {
            return;
        }

        var expected = $"stash@{{{stash.Index}}}";
        if (!await ConfirmActionAsync(
            "Drop stash?",
            $"Permanently delete {expected}: {stash.Message} without applying it? This cannot be undone.",
            "Drop stash",
            () => SelectedStash == stash))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var current = await _stashes.ListAsync(repo.Path);
            if (stash.Index >= current.Count || current[stash.Index].Message != stash.Message)
            {
                throw new InvalidOperationException("The selected stash changed. Refresh and select it again.");
            }

            await _stashes.DropAsync(repo.Path, stash.Index);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            SelectedStash = null;

            await RefreshAsync();
            Status = $"Dropped {expected} permanently without applying it.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Drop stash");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStash))]
    private async Task SaveStagedStashAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        if (StashBlockedReason is { } blocked)
        {
            Status = PresentError(blocked, "Action needs attention");
            return;
        }

        var message = StashMessage.Trim();
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var result = await _stashes.SaveAsync(repo.Path, message, AuthorName, AuthorEmail);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            StashMessage = "";
            await RefreshAsync();
            Status =
                $"Stashed {(result.StashedFiles == 1 ? "1 file" : $"{result.StashedFiles} files")}. "
                + GitStashService.Describe(result);
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Save staged stash");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task CreateBranchFromSelectedCommitAsync()
    {
        if (
            SelectedRepository is not { } repo
            || SelectedCommitLine is not { } line
            || !_commitDetails.TryGetValue(line, out var commit)
        )
        {
            Status = PresentError("Select a commit in History to create a branch from.", "Action needs attention");
            return;
        }

        var name = BranchFromCommitName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = PresentError("Enter a name for the new branch.", "Action needs attention");
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var created = await _branchFromCommit.CreateAsync(repo.Path, commit.Sha, name);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            BranchFromCommitName = "";
            await RefreshAsync();
            Status = $"Created branch {created} at {commit.Sha[..7]} without switching branches.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Create branch from commit");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task HardResetToSelectedCommitAsync()
    {
        if (
            SelectedRepository is not { } repo
            || SelectedCommitLine is not { } line
            || !_commitDetails.TryGetValue(line, out var commit)
        )
        {
            Status = PresentError("Select an ancestor commit in History.", "Action needs attention");
            return;
        }

        if (!await ConfirmActionAsync(
            "Hard reset branch?",
            $"Move {CurrentBranch} back to {commit.Sha[..7]} and replace tracked files and the index with that commit? Later local commits will leave this branch. A clean working tree is required; published commits are protected.",
            "Hard reset",
            () => SelectedCommitLine == line))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var sha = await _hardReset.ResetToAncestorAsync(repo.Path, commit.Sha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await RefreshAsync();
            Status = $"Hard reset to {sha[..7]}; tracked files and index reset to target.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Hard reset");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task MixedResetToSelectedCommitAsync()
    {
        if (
            SelectedRepository is not { } repo
            || SelectedCommitLine is not { } line
            || !_commitDetails.TryGetValue(line, out var commit)
        )
        {
            Status = PresentError("Select an ancestor commit in History.", "Action needs attention");
            return;
        }

        if (!await ConfirmActionAsync(
            "Mixed reset branch?",
            $"Move {CurrentBranch} back to {commit.Sha[..7]} and unstage changes? Working files will be kept. Later commits will be removed from this branch; published commits are protected.",
            "Mixed reset",
            () => SelectedCommitLine == line))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var sha = await _mixedReset.ResetToAncestorAsync(repo.Path, commit.Sha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await RefreshAsync();
            Status = $"Mixed reset HEAD to {sha[..7]}; index reset, working files preserved.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Mixed reset");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task SoftResetToSelectedCommitAsync()
    {
        if (
            SelectedRepository is not { } repo
            || SelectedCommitLine is not { } line
            || !_commitDetails.TryGetValue(line, out var commit)
        )
        {
            Status = PresentError("Select an ancestor commit in History.", "Action needs attention");
            return;
        }

        if (!await ConfirmActionAsync(
            "Soft reset branch?",
            $"Move {CurrentBranch} back to {commit.Sha[..7]}? Staged changes and working files will be kept. Later commits will be removed from this branch; published commits are protected.",
            "Soft reset",
            () => SelectedCommitLine == line))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var sha = await _softReset.ResetToAncestorAsync(repo.Path, commit.Sha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await RefreshAsync();
            Status = $"Soft reset HEAD to {sha[..7]}; index and working files preserved.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Soft reset");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task RevertSelectedCommitAsync()
    {
        if (
            SelectedRepository is not { } repo
            || SelectedCommitLine is not { } line
            || !_commitDetails.TryGetValue(line, out var commit)
        )
        {
            Status = PresentError("Select a commit in History to revert.", "Action needs attention");
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var sha = await _revert.RevertAsync(repo.Path, commit.Sha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await RefreshAsync();
            Status = $"Reverted {commit.Sha[..7]} with new commit {sha[..7]}.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Revert commit");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                await RefreshAsync();
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task CherryPickSelectedCommitAsync()
    {
        if (
            SelectedRepository is not { } repo
            || SelectedCommitLine is not { } line
            || !_commitDetails.TryGetValue(line, out var commit)
        )
        {
            Status = PresentError("Select a commit in History to cherry-pick.", "Action needs attention");
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var sha = await _cherryPick.CherryPickAsync(repo.Path, commit.Sha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await RefreshAsync();
            Status = $"Cherry-picked {commit.Sha[..7]} as {sha[..7]}.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Cherry-pick commit");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                await RefreshAsync();
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task AmendLatestCommitMessageAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var message = AmendedCommitMessage.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            Status = PresentError("Enter a replacement commit message.", "Action needs attention");
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            var sha = await _commitAmend.AmendLatestMessageAsync(repo.Path, message);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            AmendedCommitMessage = "";
            await RefreshAsync();
            Status = $"Amended latest unpublished commit message: {sha[..7]}.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Amend commit message");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task CreateAnnotatedTagAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var tagName = AnnotatedTagName.Trim();
        var message = AnnotatedTagMessage.Trim();
        var authorName = AnnotatedTagAuthorName.Trim();
        var authorEmail = AnnotatedTagAuthorEmail.Trim();
        if (
            string.IsNullOrWhiteSpace(tagName)
            || string.IsNullOrWhiteSpace(message)
            || string.IsNullOrWhiteSpace(authorName)
            || string.IsNullOrWhiteSpace(authorEmail)
        )
        {
            Status = PresentError("Annotated tags require a name, message, author name and author email.", "Action needs attention");
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await _tags.CreateAnnotatedAsync(repo.Path, tagName, message, authorName, authorEmail);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            AnnotatedTagName = "";
            AnnotatedTagMessage = "";
            await RefreshAsync();
            Status = $"Created annotated tag {tagName}.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Create annotated tag");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task CreateLightweightTagAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var tagName = NewTagName.Trim();
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await _tags.CreateAsync(repo.Path, tagName);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            NewTagName = "";
            await RefreshAsync();
            Status = $"Created lightweight tag {tagName}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Create lightweight tag");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task PreviewRebasePlanAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var steps = await _rebasePlanner.PlanAsync(repo.Path, RebaseUpstreamSha.Trim());
            GitRebasePlanService.ValidatePlan(steps);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            EditableRebasePlan = string.Join(
                Environment.NewLine,
                steps.Select(step => $"pick {step.CommitSha} {step.Subject}")
            );
            RebasePlanPreview =
                steps.Count == 0
                    ? "No commits to rebase."
                    : string.Join(
                        Environment.NewLine,
                        steps.Select(step => $"pick {step.CommitSha[..7]} {step.Subject}")
                    );
            Status = $"Previewed {steps.Count} rebase steps. No commits were modified.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Preview rebase plan");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                RebasePlanPreview = PresentError(e);
                Status = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectRebaseConflictsAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var state = await _rebaseConflicts.InspectAsync(repo.Path);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            RebaseConflictSummary =
                (state.IsRebasing ? "Rebase in progress" : "No rebase in progress")
                + Environment.NewLine
                + (
                    state.Paths.Count == 0
                        ? "No unresolved index conflicts."
                        : "Unresolved paths:" + Environment.NewLine + string.Join(Environment.NewLine, state.Paths)
                );
            Status =
                state.Paths.Count == 0
                    ? "No unresolved index conflicts."
                    : $"{state.Paths.Count} unresolved conflicted paths.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect rebase conflicts");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = RebaseConflictSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task LoadCommitGraphAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var nodes = await _commitGraph.GetAsync(repo.Path);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            CommitGraphSummary =
                nodes.Count == 0
                    ? "No commits yet."
                    : string.Join(
                        Environment.NewLine,
                        nodes.Select(node =>
                            $"{node.Sha[..7]} {node.Subject} | parents: {(node.Parents.Count == 0 ? "root" : string.Join(", ", node.Parents.Select(parent => parent[..7])))} | {string.Join(", ", node.Labels)}"
                        )
                    );
            Status = $"Loaded {nodes.Count} commit graph nodes.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Load commit graph");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = CommitGraphSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task LoadGraphLanesAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            string output;
            var lanes = await new GitGraphLaneService().GetAsync(repo.Path);
            output =
                lanes.Count == 0
                    ? "No commits."
                    : string.Join(
                        Environment.NewLine,
                        lanes.Select(lane =>
                            $"lane {lane.Lane}: {lane.Sha[..7]} -> {string.Join(", ", lane.Parents.Select(parent => parent[..7]))}"
                        )
                    );
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            GraphLaneSummary = output;
            Status = "Load graph lanes completed.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Load graph lanes");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = GraphLaneSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task SearchCommitsAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            string output;
            var hits = await new GitCommitSearchService().SearchAsync(repo.Path, CommitSearchQuery.Trim());
            output =
                hits.Count == 0
                    ? "No matching commits."
                    : string.Join(
                        Environment.NewLine,
                        hits.Select(hit => $"{hit.Sha[..7]} {hit.Subject} — {hit.Author}")
                    );
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            CommitSearchSummary = output;
            Status = "Search commits completed.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Search commits");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = CommitSearchSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectBlameAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            string output;
            var lines = await new GitFileBlameService().GetAsync(repo.Path, BlamePath.Trim());
            output =
                lines.Count == 0
                    ? "No blame entries."
                    : string.Join(
                        Environment.NewLine,
                        lines.Select(line =>
                            $"{line.StartLine} (+{line.LineCount}) {line.CommitSha[..7]} {line.Author}"
                        )
                    );
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            BlameSummary = output;
            Status = "Inspect blame completed.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect blame");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = BlameSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task LoadFileHistoryAsync()
    {
        if (SelectedRepository is not { } repo) return;
        var path = FileHistoryPath.Trim();
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var generation = Interlocked.Increment(ref _fileHistoryLoadGeneration);
        string? start = ReviewSource == "Commit" && SelectedCommitLine is { } line
            && _commitDetails.TryGetValue(line, out var commit) ? commit.Sha : null;
        FileHistoryFeedback = "Loading file history…";
        bool Current() => generation == Interlocked.Read(ref _fileHistoryLoadGeneration)
            && version == Interlocked.Read(ref _repositorySelectionVersion)
            && SelectedRepository?.Path == repo.Path && FileHistoryPath.Trim() == path;
        try
        {
            var entries = await new GitFileHistoryService().GetAsync(repo.Path, path, maxCount: 1000, startCommit: start);
            if (!Current()) return;
            var selected = SelectedFileRevision?.Sha;
            var comparison = SelectedComparisonRevision?.Sha;
            _settingFileRevisions = true;
            try
            {
                FileRevisions.Clear();
                foreach (var entry in entries) FileRevisions.Add(entry);
                SelectedFileRevision = entries.FirstOrDefault(entry => entry.Sha == selected) ?? entries.FirstOrDefault();
                SelectedComparisonRevision = entries.FirstOrDefault(entry => entry.Sha == comparison)
                    ?? entries.Skip(1).FirstOrDefault() ?? entries.FirstOrDefault();
            }
            finally { _settingFileRevisions = false; }
            FileHistorySummary = entries.Count == 0 ? "No file history."
                : string.Join(Environment.NewLine, entries.Select(entry => $"{entry.Sha[..7]} {entry.Subject}"));
            if (entries.Count == 0)
            {
                FileRevisionContent = FileComparisonContent = "";
                FileRevisionLabel = "Choose a revision";
                FileRevisionLineKinds = FileComparisonLineKinds = new Dictionary<int, string>();
                FileHistoryFeedback = "No committed history was found for this path.";
            }
            else await LoadFileRevisionPreviewAsync();
        }
        catch (Exception e)
        {
            if (Current())
            {
                ResetFileRevisionPreview();
                Status = FileHistorySummary = FileHistoryFeedback = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectTagsAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            string output;
            var tags = await new GitTagInspectionService().GetAsync(repo.Path);
            output =
                tags.Count == 0
                    ? "No tags."
                    : string.Join(
                        Environment.NewLine,
                        tags.Select(tag =>
                            $"{tag.Name} -> {tag.TargetSha[..7]} {(tag.IsAnnotated ? "(annotated)" : "(lightweight)")}"
                        )
                    );
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            TagInspectionSummary = output;
            Status = "Inspect tags completed.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect tags");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = TagInspectionSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectAncestryAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var result = await new GitCommitAncestryService().InspectAsync(repo.Path, AncestryCommitSha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            AncestrySummary =
                $"Commit: {result.Sha}{Environment.NewLine}Subject: {result.Subject}{Environment.NewLine}Merge: {result.IsMergeCommit}{Environment.NewLine}Parents: {(result.Parents.Count == 0 ? "(root)" : string.Join(", ", result.Parents))}";
            Status = "Commit ancestry inspected.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect commit ancestry");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = AncestrySummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectDescendantsAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var descendants = await new GitCommitDescendantService().GetAsync(repo.Path, DescendantCommitSha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            DescendantSummary =
                descendants.Count == 0
                    ? "No reachable descendants."
                    : string.Join(
                        Environment.NewLine,
                        descendants.Select(c => $"Distance {c.Distance}: {c.Sha[..7]} {c.Subject}")
                    );
            Status = "Commit descendants inspected.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect commit descendants");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = DescendantSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectMergeBaseAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var result = await new GitMergeBaseInspectionService().InspectAsync(
                repo.Path,
                MergeBaseFirstSha,
                MergeBaseSecondSha
            );
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            MergeBaseSummary = result.BaseSha is null
                ? "No common ancestor found."
                : $"Common ancestor: {result.BaseSha}{Environment.NewLine}Subject: {result.BaseSubject}";
            Status = "Merge base inspected.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect merge base");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = MergeBaseSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectRelationshipAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var result = await new GitCommitRelationshipService().InspectAsync(
                repo.Path,
                RelationshipFirstSha,
                RelationshipSecondSha
            );
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            RelationshipSummary =
                $"Relationship: {result.Relationship}{Environment.NewLine}Common ancestor: {result.MergeBaseSha ?? "(none)"}";
            Status = "Commit relationship inspected.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect commit relationship");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = RelationshipSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectDiffStatisticsAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var result = await new GitCommitDiffStatisticsService().InspectAsync(repo.Path, DiffStatisticsCommitSha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            DiffStatisticsSummary =
                $"Commit: {result.Sha}{Environment.NewLine}Subject: {result.Subject}{Environment.NewLine}Files changed: {result.FilesChanged}{Environment.NewLine}Lines added: {result.LinesAdded}{Environment.NewLine}Lines deleted: {result.LinesDeleted}{Environment.NewLine}Root commit: {result.IsRoot}";
            Status = "Commit diff statistics inspected.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect commit diff statistics");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = DiffStatisticsSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectChangedFilesAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var files = await new GitCommitChangedFilesService().GetAsync(repo.Path, ChangedFilesCommitSha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            ChangedFilesSummary =
                files.Count == 0
                    ? "No changed files."
                    : string.Join(Environment.NewLine, files.Select(file => $"{file.Status}: {file.Path}"));
            Status = "Commit changed files inspected.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect commit changed files");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = ChangedFilesSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectFileStatisticsAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var files = await new GitCommitFileStatisticsService().GetAsync(repo.Path, FileStatisticsCommitSha);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            FileStatisticsSummary =
                files.Count == 0
                    ? "No changed files."
                    : string.Join(
                        Environment.NewLine,
                        files.Select(file => $"{file.Status}: {file.Path} (+{file.LinesAdded}/-{file.LinesDeleted})")
                    );
            Status = "Commit file statistics inspected.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect commit file statistics");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = FileStatisticsSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectFilePatchAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var result = await new GitCommitFilePatchService().GetAsync(repo.Path, FilePatchCommitSha, FilePatchPath);
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            FilePatchSummary =
                $"File: {result.Path} (+{result.LinesAdded}/-{result.LinesDeleted}){Environment.NewLine}{result.PatchText}";
            Status = "Commit file patch inspected.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect commit file patch");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = FilePatchSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task InspectFileContentAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var result = await new GitCommitFileContentService().GetAsync(
                repo.Path,
                FileContentCommitSha,
                FileContentPath
            );
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            FileContentSummary = $"File: {result.Path} ({result.Size} bytes){Environment.NewLine}{result.Content}";
            Status = "Historical file content inspected.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Inspect historical file content");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = FileContentSummary = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private async Task CreateBranchAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var name = NewBranchName.Trim();
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _branches.CreateAsync(repo.Path, name);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            NewBranchName = "";
            await RefreshAsync();
            Status = $"Created branch {name}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Create branch");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task CheckoutBranchAsync()
    {
        if (SelectedRepository is not { } repo || SelectedBranchLine is not { } selected)
        {
            return;
        }

        var name = selected.Trim();
        if (name.StartsWith("* ", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            BranchSwitchTarget = name;
            IsBranchSwitching = true;
            var current = await _branches.CheckoutAsync(repo.Path, name);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            CurrentBranch = current;
            await RefreshAsync();
            Status = $"Switched to {current}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Checkout branch");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            IsBranchSwitching = false;
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task RenameBranchAsync()
    {
        if (SelectedRepository is not { } repo || SelectedBranchLine is not { } selected)
        {
            return;
        }

        var oldName = selected.Trim();
        if (oldName.StartsWith("* ", StringComparison.Ordinal))
        {
            oldName = oldName[2..];
        }

        var newName = RenameBranchName.Trim();
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _branches.RenameAsync(repo.Path, oldName, newName);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            if (CurrentBranch == oldName)
            {
                CurrentBranch = newName;
            }

            RenameBranchName = "";
            await RefreshAsync();
            Status = $"Renamed branch {oldName} to {newName}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Rename branch");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task DeleteBranchAsync()
    {
        if (SelectedRepository is not { } repo || SelectedBranchLine is not { } selected)
        {
            return;
        }

        var name = selected.Trim();
        if (name.StartsWith("* ", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await _branches.DeleteAsync(repo.Path, name);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await RefreshAsync();
            Status = $"Deleted branch {name}";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Delete branch");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task DiscardSelectedUnstagedAsync()
    {
        if (SelectedChangeLine is { } selected && _changePaths.TryGetValue(selected, out var path))
        {
            if (await ConfirmActionAsync("Discard file changes?",
                $"Discard unstaged edits in {path}? Staged changes will be kept. This cannot be undone.",
                "Discard changes", () => SelectedChangeLine == selected))
                await DiscardUnstagedPathAsync(path, "DISCARD");
        }
    }

    private async Task DiscardUnstagedPathAsync(string path, string confirmation)
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            // The file can be open for a moment while its diff or preview loads, so retry briefly.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _workingReview.DiscardUnstagedAsync(repo.Path, path, confirmation);
                    break;
                }
                catch (Exception e)
                    when (attempt < 5
                        && (
                            e is IOException
                            || e.InnerException is IOException
                            || e.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
                        )
                    )
                {
                    await Task.Delay(150 * attempt);
                }
            }

            if (version != Interlocked.Read(ref _repositorySelectionVersion) || SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await RefreshAsync();
            CloseReviewIfUnchanged(path);
            Status = $"Discarded unstaged changes in {path}; staged content preserved.";
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Discard unstaged changes");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task StageSelectedAsync()
    {
        await ChangeStageAsync(stage: true);
    }

    [RelayCommand]
    private async Task UnstageSelectedAsync()
    {
        await ChangeStageAsync(stage: false);
    }

    private async Task ChangeStageAsync(bool stage)
    {
        if (
            SelectedRepository is not { } repo
            || SelectedChangeLine is not { } selected
            || !_changePaths.TryGetValue(selected, out var path)
        )
        {
            return;
        }

        var selectionVersion = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (
                selectionVersion != Interlocked.Read(ref _repositorySelectionVersion)
                || SelectedRepository?.Path != repo.Path
            )
            {
                return;
            }

            if (stage)
            {
                await _gitActions.StageAsync(repo.Path, path);
            }
            else
            {
                await _gitActions.UnstageAsync(repo.Path, path);
            }

            if (SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            await RefreshAsync();
            if (SelectedRepository?.Path == repo.Path)
            {
                SelectedChangeLine = _changePaths.FirstOrDefault(entry => entry.Value == path).Key;
                IsWorkingTreeInspector = true;
                Status = stage ? $"Staged {path}" : $"Unstaged {path}";
            }
        }
        catch (Exception e)
        {
            FileLog.Error(e, stage ? "Stage file" : "Unstage file");
            if (SelectedRepository?.Path == repo.Path)
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var message = CommitMessage;
        var author = AuthorName;
        var email = AuthorEmail;
        var amend = AmendStagedCommit;
        var selectionVersion = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (
                selectionVersion != Interlocked.Read(ref _repositorySelectionVersion)
                || SelectedRepository?.Path != repo.Path
            )
            {
                return;
            }

            var sha = await _gitActions.CommitAsync(repo.Path, message, author, email, amend: amend);
            if (SelectedRepository?.Path != repo.Path)
            {
                return;
            }

            CommitMessage = "";
            AmendStagedCommit = false;
            await RefreshAsync();
            if (SelectedRepository?.Path == repo.Path)
            {
                Status = $"{(amend ? "Amended" : "Created")} commit {sha[..7]}";
            }
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Create commit");
            if (SelectedRepository?.Path == repo.Path)
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }
}
