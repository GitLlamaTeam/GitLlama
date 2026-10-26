using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private string centerSection = "History";

    [ObservableProperty]
    private bool isFileReview;

    [ObservableProperty]
    private string reviewSource = "";

    [ObservableProperty]
    private string reviewPath = "";

    [ObservableProperty]
    private string fileReviewMode = "Diff";

    [ObservableProperty]
    private string workingFileContent = "";
    public ObservableCollection<RepositoryWorkspaceTab> RepositoryWorkspaceTabs { get; } = [];

    [ObservableProperty]
    private RepositoryWorkspaceTab? selectedRepositoryWorkspaceTab;
    public bool IsRepositoryManagerOpen => RepositoryWorkspaceTabs.Any(tab => tab.IsEmpty);
    public bool IsRepositoryManagerSelected => SelectedRepositoryWorkspaceTab?.IsEmpty != false;

    private void NotifyRepositoryTabs()
    {
        OnPropertyChanged(nameof(IsRepositoryManagerOpen));
        OnPropertyChanged(nameof(IsRepositoryManagerSelected));
    }

    partial void OnSelectedRepositoryWorkspaceTabChanged(RepositoryWorkspaceTab? oldValue, RepositoryWorkspaceTab? newValue)
    {
        var value = newValue;
        if (_restoringRejectedRepositorySelection)
            return;
        if (value?.Repository is { } candidate && !ValidateRepositoryForOpen(candidate))
        {
            _restoringRejectedRepositorySelection = true;
            try { SelectedRepositoryWorkspaceTab = oldValue; }
            finally { _restoringRejectedRepositorySelection = false; }
            return;
        }
        NotifyRepositoryTabs();
        if (value?.Repository is { } repository)
        {
            SelectedRepository = repository;
        }

        if (
            value?.IsEmpty == true
            && !_openingCloneCatalog
            && (_azurePersonalAccessTokens.Count > 0 || IsGithubConnected)
        )
        {
            _ = RefreshCloneableRepositoriesAsync();
        }

        _ = PersistAsync();
    }

    public void NewRepositoryTab()
    {
        var tab = new RepositoryWorkspaceTab();
        RepositoryWorkspaceTabs.Add(tab);
        SelectedRepositoryWorkspaceTab = tab;
        NotifyRepositoryTabs();
    }

    public bool OpenRepositoryTab(GitLlama.Core.RepositoryInfo repository)
    {
        if (!ValidateRepositoryForOpen(repository))
            return false;
        if (!_loadingSettings)
            RepositoryOpenError = "";
        // A submodule opens in the tab of the repository it belongs to.
        var existing = RepositoryWorkspaceTabs.FirstOrDefault(tab =>
            string.Equals(
                tab.Repository?.Root.Path,
                repository.Root.Path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            )
        );
        if (existing is not null)
        {
            if (SelectedRepositoryWorkspaceTab is { IsEmpty: true } empty)
            {
                RepositoryWorkspaceTabs.Remove(empty);
            }

            existing.Repository = repository;
            SelectedRepositoryWorkspaceTab = existing;
        }
        else
        {
            var tab = SelectedRepositoryWorkspaceTab is { IsEmpty: true } empty ? empty : new RepositoryWorkspaceTab();
            tab.Repository = repository;
            if (!RepositoryWorkspaceTabs.Contains(tab))
            {
                RepositoryWorkspaceTabs.Add(tab);
            }

            SelectedRepositoryWorkspaceTab = tab;
        }

        NotifyRepositoryTabs();
        SelectedRepository = repository;
        _ = PersistAsync();
        return true;
    }

    public void CloseWorkspaceTab(RepositoryWorkspaceTab tab)
    {
        var index = RepositoryWorkspaceTabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var active = SelectedRepositoryWorkspaceTab == tab;
        RepositoryWorkspaceTabs.Remove(tab);
        if (RepositoryWorkspaceTabs.Count == 0)
        {
            SelectedRepository = null;
            NewRepositoryTab();
        }
        else if (active)
        {
            SelectedRepositoryWorkspaceTab = RepositoryWorkspaceTabs[
                Math.Min(index, RepositoryWorkspaceTabs.Count - 1)
            ];
        }

        NotifyRepositoryTabs();
        _ = PersistAsync();
    }

    private long _centerContentRevision;
    private int _contextSnapshotDepth;
    internal bool IsUpdatingContextSnapshot => _contextSnapshotDepth > 0;

    private IDisposable BeginContextSnapshot()
    {
        _contextSnapshotDepth++;
        OnPropertyChanged(nameof(IsUpdatingContextSnapshot));
        return new ContextSnapshotScope(() =>
        {
            _contextSnapshotDepth--;
            OnPropertyChanged(nameof(IsUpdatingContextSnapshot));
        });
    }

    private sealed class ContextSnapshotScope(Action finish) : IDisposable
    {
        private Action? _finish = finish;

        public void Dispose() => Interlocked.Exchange(ref _finish, null)?.Invoke();
    }

    public bool IsFileMode => FileReviewMode == "File";
    public bool IsHistoryMode => FileReviewMode == "History";
    public bool IsBlameMode => FileReviewMode == "Blame";
    public bool IsDiffReview => FileReviewMode == "Diff";
    public bool IsContentReview => !IsDiffReview && !IsHistoryMode;
    public bool IsWorkingFileReview => ReviewSource == "Working";
    public bool IsWorkingDiffReview => IsFileReview && IsWorkingFileReview && IsDiffReview;
    public string ApplyLineLabel => ReviewStagedPatch ? "Unstage line" : "Stage line";
    public string ApplyHunkLabel => ReviewStagedPatch ? "Unstage hunk" : "Stage hunk";
    public bool CanApplySelectedLine =>
        IsWorkingDiffReview
        && SelectedPatchLine is { IsChange: true } selected
        && PatchLines.Any(line => line.Index == selected.Index && line.Text == selected.Text);
    public bool CanApplySelectedHunk =>
        IsWorkingDiffReview
        && SelectedPatchLine is { } selected
        && !string.IsNullOrEmpty(selected.Hunk)
        && PatchLines.Any(line => line.Hunk == selected.Hunk && line.IsChange);
    public bool CanReviewDiff => ReviewSource != "Repository";
    public string CenterPatch =>
        ReviewSource == "Working" ? (ReviewStagedPatch ? StagedDiffText : UnstagedDiffText) : HistoryFilePreview;
    public string CenterContent =>
        FileReviewMode == "History" ? FileHistorySummary
        : FileReviewMode == "Blame" ? BlameSummary
        : ReviewSource == "Working" ? WorkingFileContent
        : ReviewSource == "Repository" ? SelectedFilePreview
        : HistoryFilePreview;

    internal void OpenFileReview(string source, string path)
    {
        if (IsUpdatingContextSnapshot)
        {
            return;
        }

        var same = IsFileReview && ReviewSource == source && ReviewPath == path;
        ReviewSource = source;
        ReviewPath = path;
        IsFileReview = true;
        if (!same)
        {
            FileReviewMode = source == "Repository" ? "File" : "Diff";
        }

        NotifyCenterReview();
    }

    internal void NotifyCenterReview()
    {
        foreach (
            var name in new[]
            {
                nameof(IsFileMode),
                nameof(IsHistoryMode),
                nameof(IsBlameMode),
                nameof(IsDiffReview),
                nameof(IsContentReview),
                nameof(IsWorkingFileReview),
                nameof(CanReviewDiff),
                nameof(CenterPatch),
                nameof(CenterContent),
            }
        )
        {
            OnPropertyChanged(name);
        }

        foreach (
            var name in new[]
            {
                nameof(IsWorkingDiffReview),
                nameof(ApplyLineLabel),
                nameof(ApplyHunkLabel),
                nameof(CanApplySelectedLine),
                nameof(CanApplySelectedHunk),
            }
        )
        {
            OnPropertyChanged(name);
        }

        ApplySelectedLineCommand.NotifyCanExecuteChanged();
        ApplySelectedHunkCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFileReviewChanged(bool value)
    {
        if (value)
        {
            IsConflictResolver = false;
        }

        NotifyCenterReview();
    }

    partial void OnSelectedPatchLineChanged(GitLlama.Git.GitPatchLine? value) => NotifyCenterReview();

    partial void OnReviewSourceChanged(string value) => NotifyCenterReview();

    partial void OnFileReviewModeChanged(string value)
    {
        if (ReviewSource == "Commit")
        {
            HistoryShowContent = value == "File";
        }

        if (value == "File" && ReviewSource == "Working")
        {
            _ = LoadWorkingContentAsync();
        }

        NotifyCenterReview();
    }

    partial void OnUnstagedDiffTextChanged(string value) => NotifyCenterReview();

    partial void OnStagedDiffTextChanged(string value) => NotifyCenterReview();

    partial void OnHistoryFilePreviewChanged(string value) => NotifyCenterReview();

    partial void OnSelectedFilePreviewChanged(string value) => NotifyCenterReview();

    partial void OnWorkingFileContentChanged(string value) => NotifyCenterReview();

    partial void OnFileHistorySummaryChanged(string value) => NotifyCenterReview();

    partial void OnBlameSummaryChanged(string value) => NotifyCenterReview();

    private async Task LoadWorkingContentAsync()
    {
        var revision = Interlocked.Increment(ref _centerContentRevision);
        var repository = SelectedRepository?.Path;
        var path = ReviewPath;
        var staged = ReviewStagedPatch;
        WorkingFileContent = "Loading file…";
        if (repository is null)
        {
            return;
        }

        try
        {
            var content = await new GitLlama.Git.GitFileBrowserService().ReadContentAsync(repository, path, staged);
            if (
                revision == Interlocked.Read(ref _centerContentRevision)
                && SelectedRepository?.Path == repository
                && ReviewPath == path
                && ReviewStagedPatch == staged
            )
            {
                WorkingFileContent = content;
            }
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _centerContentRevision))
            {
                WorkingFileContent = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private void CloseFileReview()
    {
        IsFileReview = false;
    }

    [RelayCommand]
    private void ReviewDiff() => FileReviewMode = "Diff";

    [RelayCommand]
    private void ReviewFile() => FileReviewMode = "File";

    [RelayCommand]
    private async Task ReviewFileHistoryAsync()
    {
        FileHistoryPath = ReviewPath;
        FileReviewMode = "History";
        await LoadFileHistoryCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task ReviewFileBlameAsync()
    {
        BlamePath = ReviewPath;
        FileReviewMode = "Blame";
        await InspectBlameCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task CloseRepositoryTabAsync(GitLlama.Core.RepositoryInfo? repository)
    {
        if (repository is null)
        {
            return;
        }

        var tab = RepositoryWorkspaceTabs.FirstOrDefault(item => item.Repository?.Path == repository.Path);
        if (tab is not null)
        {
            CloseWorkspaceTab(tab);
        }

        await PersistAsync();
    }
}
