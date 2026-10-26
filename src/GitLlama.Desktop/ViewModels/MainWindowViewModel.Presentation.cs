using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;
using GitLlama.Git;
using GitLlama.Infrastructure;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private IReadOnlyList<HistoryRow> _allHistoryRows = [];
    private string? _currentHistoryHeadSha;
    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];
    public ObservableCollection<RepositoryInfo> FilteredRepositories { get; } = [];
    public ObservableCollection<string> FilteredBranches { get; } = [];
    public ObservableCollection<string> LocalBranches { get; } = [];
    public ObservableCollection<string> SidebarRemoteBranches { get; } = [];
    public bool HasSelectedWorkingFile => SelectedWorkingFile is not null;
    public bool HasSelectedHistoryFile => SelectedHistoryFileRow is not null;
    public string WorkingChangesCaption =>
        HasMergeConflicts ? $"Merge conflicts ({ConflictedFileCount}) · Resolve"
        : IsMergeInProgress ? "Merge ready to finish · View changes"
        : ChangeCount == 0 ? "Working directory is clean"
        : $"{ChangeCount} working changes · View changes";
    public ObservableCollection<string> OperationLog { get; } = [];
    public ObservableCollection<WorkingFileRow> WorkingFiles { get; } = new BatchObservableCollection<WorkingFileRow>();
    public ObservableCollection<WorkingFileRow> StagedFiles { get; } = new BatchObservableCollection<WorkingFileRow>();
    public ObservableCollection<WorkingFileRow> UnstagedFiles { get; } = new BatchObservableCollection<WorkingFileRow>();
    public string StagedFilesCaption => $"Staged changes ({StagedFiles.Count})";
    public string UnstagedFilesCaption => $"Unstaged changes ({UnstagedFiles.Count})";
    public bool HasStagedFiles => StagedFiles.Count > 0;
    public bool HasUnstagedFiles => UnstagedFiles.Count > 0;
    public WorkingFileRow? SelectedStagedFile => ReviewStagedPatch ? SelectedWorkingFile : null;
    public WorkingFileRow? SelectedUnstagedFile => ReviewStagedPatch ? null : SelectedWorkingFile;
    public ObservableCollection<HistoryFileRow> HistoryFileRows { get; } = [];

    [ObservableProperty]
    private WorkingFileRow? selectedWorkingFile;

    [ObservableProperty]
    private HistoryFileRow? selectedHistoryFileRow;

    [ObservableProperty]
    private HistoryRow? selectedHistoryRow;

    [ObservableProperty]
    private string repositorySearch = "";

    [ObservableProperty]
    private string branchSearch = "";

    [ObservableProperty]
    private string historySubject = "Select a commit";

    [ObservableProperty]
    private string historyAuthor = "";

    [ObservableProperty]
    private AvatarImage? historyAvatar;

    [ObservableProperty]
    private string historySha = "";

    [ObservableProperty]
    private string historyDate = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistoryMessage))]
    private string historyMessage = "Choose a commit in History to inspect its message and changed files.";
    public bool HasHistoryMessage => !string.IsNullOrWhiteSpace(HistoryMessage);

    // The subject is shown as the heading, so the description is everything after its paragraph.
    internal static string CommitDescription(string message)
    {
        var normalized = message.Replace("\r\n", "\n").Trim();
        var end = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        return end < 0 ? "" : normalized[(end + 2)..].Trim();
    }

    [ObservableProperty]
    private bool isWorkingTreeInspector;

    [ObservableProperty]
    private int stagedFileCount;

    [ObservableProperty]
    private double historyGraphWidth = 84;

    [ObservableProperty]
    private string upstreamStatus = "No upstream";
    public bool HasRepository => SelectedRepository is not null;
    public bool HasNoRepository => !HasRepository;
    public bool HasNoHistory => HistoryRows.Count == 0;
    public bool HasNoChanges => ChangeLines.Count == 0;
    public int ChangeCount => ChangeLines.Count;
    public string HistoryCaption =>
        $"{HistoryRows.Count} shown · {_loadedGraphNodes.Count} loaded{(HasMoreHistory ? " · more available" : "")}";
    public string HistoryEmptyMessage =>
        !HasRepository ? "Open a repository to explore its history."
        : string.IsNullOrWhiteSpace(HistorySearch) ? "This repository has no commits yet."
        : "No commits match your search.";
    public string CommitButtonText =>
        AmendStagedCommit ? $"Amend with {StagedFileCount} files"
        : StagedFileCount == 1 ? "Commit 1 file"
        : $"Commit {StagedFileCount} files";
    public bool CanCommit =>
        HasRepository
        && StagedFileCount > 0
        && (AmendStagedCommit || !string.IsNullOrWhiteSpace(CommitSummaryText))
        && !string.IsNullOrWhiteSpace(AuthorName)
        && !string.IsNullOrWhiteSpace(AuthorEmail);
    public string CommitSummaryText
    {
        get => CommitMessage.Split('\n')[0].TrimEnd('\r');
        set =>
            CommitMessage =
                value.TrimEnd('\r', '\n')
                + (string.IsNullOrWhiteSpace(CommitDescriptionText) ? "" : "\n\n" + CommitDescriptionText);
    }
    public string CommitDescriptionText
    {
        get => string.Join('\n', CommitMessage.Split('\n').Skip(1)).TrimStart('\r', '\n');
        set => CommitMessage = CommitSummaryText + (string.IsNullOrWhiteSpace(value) ? "" : "\n\n" + value);
    }
    public bool HasHistoryFilter =>
        !string.IsNullOrWhiteSpace(HistorySearch)
        || !string.IsNullOrWhiteSpace(HistoryAuthorFilter)
        || HistorySince is not null
        || HistoryUntil is not null;

    private void InitializePresentation()
    {
        InitializeMultiFileSelection();
        WorkingFiles.CollectionChanged += (_, _) =>
        {
            CollectionUpdates.Sync(StagedFiles, WorkingFiles.Where(file => file.IsStaged), file => file.Path);
            CollectionUpdates.Sync(UnstagedFiles, WorkingFiles.Where(file => file.IsUnstaged), file => file.Path);
            OnPropertyChanged(nameof(StagedFilesCaption));
            OnPropertyChanged(nameof(UnstagedFilesCaption));
            OnPropertyChanged(nameof(HasStagedFiles));
            OnPropertyChanged(nameof(HasUnstagedFiles));
            NotifyStashAvailability();
            NotifyMergeState();
        };
        Repositories.CollectionChanged += (_, _) => FilterRepositories();
        BranchLines.CollectionChanged += (_, _) => FilterBranches();
        Remotes.CollectionChanged += (_, _) =>
        {
            FilterBranches();
            NotifyUpstream();
        };
        ChangeLines.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ChangeCount));
            OnPropertyChanged(nameof(HasNoChanges));
            OnPropertyChanged(nameof(WorkingChangesCaption));
        };
        HistoryRows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoHistory));
            OnPropertyChanged(nameof(HistoryCaption));
        };
    }

    partial void OnSelectedWorkingFileChanged(WorkingFileRow? value)
    {
        SynchronizeInspectorSelection(ReviewStagedPatch ? SelectedStagedFiles : SelectedUnstagedFiles, value);
        SelectedChangeLine = value?.Line;
        OnPropertyChanged(nameof(HasSelectedWorkingFile));
        NotifyWorkingSelection();
        if (value is not null)
        {
            OpenFileReview("Working", value.Path);
        }
    }

    private void NotifyWorkingSelection()
    {
        OnPropertyChanged(nameof(SelectedStagedFile));
        OnPropertyChanged(nameof(SelectedUnstagedFile));
    }

    partial void OnSelectedHistoryFileRowChanged(HistoryFileRow? value)
    {
        SynchronizeInspectorSelection(SelectedCommitFiles, value);
        SelectedHistoryFile = value?.Line;
        OnPropertyChanged(nameof(HasSelectedHistoryFile));
        if (value is not null)
        {
            FileHistoryPath = value.Path;
            OpenFileReview("Commit", value.Path);
        }
    }

    partial void OnAuthorNameChanged(string value)
    {
        OnPropertyChanged(nameof(EffectiveCommitIdentity));
        OnPropertyChanged(nameof(CanCommit));
        CommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnAuthorEmailChanged(string value)
    {
        OnPropertyChanged(nameof(EffectiveCommitIdentity));
        OnPropertyChanged(nameof(CanCommit));
        CommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnRepositorySearchChanged(string value) => FilterRepositories();

    partial void OnBranchSearchChanged(string value) => FilterBranches();

    private void FilterRepositories()
    {
        FilteredRepositories.Clear();
        foreach (
            var repo in Repositories.Where(r =>
                r.Name.Contains(RepositorySearch, StringComparison.OrdinalIgnoreCase)
                || r.Path.Contains(RepositorySearch, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            FilteredRepositories.Add(repo);
        }
    }

    private void FilterBranches()
    {
        if (_refreshingRemoteList)
        {
            return;
        }

        FilteredBranches.Clear();
        LocalBranches.Clear();
        SidebarRemoteBranches.Clear();
        foreach (var branch in BranchLines.Where(b => b.Contains(BranchSearch, StringComparison.OrdinalIgnoreCase)))
        {
            FilteredBranches.Add(branch);
            (
                Remotes.Any(r => branch.StartsWith(r.Name + "/", StringComparison.Ordinal))
                    ? SidebarRemoteBranches
                    : LocalBranches
            ).Add(branch);
        }

        UpdateRemoteTree();
    }

    private void SetHistoryRows(IReadOnlyList<GitCommitNode> nodes)
    {
        _allHistoryRows = HistoryRow.Layout(nodes, CurrentBranch, _currentHistoryHeadSha);
        HistoryGraphWidth = _allHistoryRows.FirstOrDefault()?.GraphWidth ?? 84;
        FilterPresentationHistory();
    }

    private void FilterPresentationHistory()
    {
        var previous = SelectedHistoryRow?.Sha;
        var existing = HistoryRows.ToDictionary(row => row.Sha, StringComparer.Ordinal);
        var desired = _allHistoryRows.Where(r =>
                (
                    string.IsNullOrWhiteSpace(HistorySearch)
                    || $"{r.Sha} {r.Subject} {r.Author} {string.Join(' ', r.Labels)}".Contains(
                        HistorySearch,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                && (
                    string.IsNullOrWhiteSpace(HistoryAuthorFilter)
                    || r.Author.Contains(HistoryAuthorFilter, StringComparison.OrdinalIgnoreCase)
                )
                && (HistorySince is null || r.Commit.Timestamp.Date >= HistorySince.Value.Date)
                && (HistoryUntil is null || r.Commit.Timestamp.Date <= HistoryUntil.Value.Date)
            ).Select(row =>
        {
            // Filtering removes intermediary commits; suppress edges to avoid inventing ancestry.
            var visible = HasHistoryFilter ? row with { Segments = [] } : row;
            return existing.TryGetValue(row.Sha, out var retained) && retained.HasSamePresentation(visible)
                ? retained : visible;
        }).ToArray();
        CollectionUpdates.Sync(HistoryRows, desired, row => row.Sha);

        SelectedHistoryRow = HistoryRows.FirstOrDefault(r => r.Sha == previous);
        if (HistoryRows.Count == 0 && HasHistoryFilter)
        {
            Interlocked.Increment(ref _historySelectionRevision);
            Interlocked.Increment(ref _historyFileRevision);
            _activeHistoryCommit = null;
            HistoryFiles.Clear();
            HistoryFileRows.Clear();
            _historyFilePaths.Clear();
            SelectedHistoryFileRow = null;
            SelectedHistoryFile = null;
        }

        OnPropertyChanged(nameof(HistoryEmptyMessage));
        OnPropertyChanged(nameof(HasHistoryFilter));
    }

    partial void OnSelectedHistoryRowChanged(HistoryRow? value)
    {
        if (value is null)
        {
            if (IsUpdatingContextSnapshot)
            {
                return;
            }

            Interlocked.Increment(ref _historySelectionRevision);
            Interlocked.Increment(ref _historyFileRevision);
            _activeHistoryCommit = null;
            HistoryFiles.Clear();
            _historyFilePaths.Clear();
            SelectedHistoryFile = null;
            HistoryFileRows.Clear();
            SelectedHistoryFileRow = null;
            HistorySubject = "Select a commit";
            HistoryAuthor = HistorySha = HistoryDate = "";
            HistoryAvatar = null;
            HistoryMessage = "Choose a commit in History to inspect its message and changed files.";
            HistoryCommitDetails = "Select a commit in History.";
            HistoryFilePreview = "Select a changed file to inspect its patch.";
            return;
        }

        if (!IsUpdatingContextSnapshot)
        {
            _inspectedStashSha = null;
            IsWorkingTreeInspector = false;
            IsFileReview = false;
            IsConflictResolver = false;
            CenterSection = "History";
        }

        HistorySubject = value.Subject;
        HistoryAuthor = value.Author;
        HistoryAvatar = value.AuthorAvatar;
        HistoryMessage = "";
        HistorySha = value.ShortSha;
        HistoryDate = value.FullDate;
        var line = _historyGraphShas.FirstOrDefault(e => e.Value == value.Sha).Key;
        if (line is not null)
        {
            if (SelectedHistoryGraphLine == line)
            {
                _ = LoadHistoryCommitAsync(value.Sha);
            }
            else
            {
                SelectedHistoryGraphLine = line;
            }
        }
    }

    private void ResetPresentation()
    {
        ClearInspectorFileSelection();
        _conflictDrafts.Clear();
        Interlocked.Increment(ref _conflictOpenRevision);
        IsFileReview = false;
        IsConflictResolver = false;
        CenterSection = "History";
        Interlocked.Increment(ref _centerContentRevision);
        _allHistoryRows = [];
        HistoryRows.Clear();
        SelectedHistoryRow = null;
        HistoryFiles.Clear();
        _historyFilePaths.Clear();
        _historyGraphShas.Clear();
        HistoryGraphLines.Clear();
        WorkingFiles.Clear();
        SelectedWorkingFile = null;
        HistoryFileRows.Clear();
        SelectedHistoryFileRow = null;
        Interlocked.Increment(ref _historySelectionRevision);
        Interlocked.Increment(ref _historyFileRevision);
        _activeHistoryCommit = null;
        HistorySubject = "Select a commit";
        HistoryAuthor = HistorySha = HistoryDate = "";
        HistoryAvatar = null;
        HistoryMessage = "Choose a commit in History to inspect its message and changed files.";
        HistoryCommitDetails = "Select a commit in History.";
        HistoryFilePreview = "Select a changed file to inspect its patch.";
        StagedFileCount = 0;
        RepositoryOperation = "None";
        UpstreamStatus = "No upstream";
        Upstream = null;
        FileHistoryPath = "";
        FileHistorySummary = "Select a file to load its history.";
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(HasNoRepository));
        OnPropertyChanged(nameof(HistoryEmptyMessage));
        CommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnStagedFileCountChanged(int value)
    {
        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(CanCommit));
        CommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnCommitMessageChanged(string value)
    {
        OnPropertyChanged(nameof(CommitSummaryText));
        OnPropertyChanged(nameof(CommitDescriptionText));
        OnPropertyChanged(nameof(CanCommit));
        CommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnStatusChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var redacted = SecretRedactor.Redact(value);
        foreach (
            var secret in new[] { NetworkSecret, GithubToken, AzurePatInput }.Concat(_azurePersonalAccessTokens.Values)
        )
        {
            if (!string.IsNullOrEmpty(secret))
            {
                redacted = redacted.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        if (redacted != value)
        {
            Status = redacted;
            return;
        }

        OperationLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {value}");
        while (OperationLog.Count > 200)
        {
            OperationLog.RemoveAt(OperationLog.Count - 1);
        }
    }

    [RelayCommand]
    private void ShowWorkingTree()
    {
        IsWorkingTreeInspector = true;
        if (SelectedWorkingFile is { } file)
        {
            OpenFileReview("Working", file.Path);
        }
        else
        {
            IsFileReview = false;
        }
        ActivateTool?.Invoke("inspector");
    }

    partial void OnSelectedTagChanged(GitTagInfo? value)
    {
        if (IsUpdatingContextSnapshot)
        {
            return;
        }

        if (value is not null && HistoryRows.FirstOrDefault(row => row.Sha == value.TargetSha) is { } row)
        {
            SelectedHistoryRow = row;
        }
    }
}
