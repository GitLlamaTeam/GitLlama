using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

public sealed record MultiFilePreview(string Path, string Content, bool IsDiff)
{
    public bool IsFile => !IsDiff;
    public double PreviewHeight => Math.Clamp((IsDiff
        ? GitPartialStageService.Lines(Content).Count(line => line.Kind != "Metadata")
        : Content.Count(character => character == '\n') + 1) * 20 + 20, 72, 420);
}

public partial class MainWindowViewModel
{
    public ObservableCollection<WorkingFileRow> SelectedStagedFiles { get; } = [];
    public ObservableCollection<WorkingFileRow> SelectedUnstagedFiles { get; } = [];
    public ObservableCollection<HistoryFileRow> SelectedCommitFiles { get; } = [];
    public ObservableCollection<MultiFilePreview> MultiFilePreviews { get; } = [];
    private long _multiFilePreviewRevision;
    private string _multiFilePreviewContext = "";
    private bool _clearingInspectorSelection;
    [ObservableProperty] private bool isMultiFileReview;
    [ObservableProperty] private string multiFileFeedback = "";
    public bool HasWorkingFileSelection => SelectedStagedFiles.Count + SelectedUnstagedFiles.Count > 0;
    private bool HasMultipleWorkingFileSelection => SelectedStagedFiles.Count + SelectedUnstagedFiles.Count > 1;
    public string InspectorStageLabel => SelectedUnstagedFiles.Count > 1 ? $"Stage selected ({SelectedUnstagedFiles.Count})" : "✓ Stage all";
    public string InspectorUnstageLabel => SelectedStagedFiles.Count > 1 ? $"Unstage selected ({SelectedStagedFiles.Count})" : "Unstage all";
    public string InspectorDiscardLabel => HasMultipleWorkingFileSelection ? $"Discard selected changes ({InspectorDiscardPaths().Length})…" : "Discard all changes…";
    internal string[] InspectorDiscardPaths() => HasMultipleWorkingFileSelection
        ? SelectedStagedFiles.Concat(SelectedUnstagedFiles).Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray() : [];
    public bool CanInspectorStage => SelectedUnstagedFiles.Count > 1 || (!HasMultipleWorkingFileSelection && HasUnstagedFiles);
    public bool CanInspectorUnstage => SelectedStagedFiles.Count > 1 || (!HasMultipleWorkingFileSelection && HasStagedFiles);
    public string ReviewTitle => IsMultiFileReview ? $"{MultiSelectionCount} selected files" : ReviewPath;
    public bool IsSingleFileReview => !IsMultiFileReview;
    private int MultiSelectionCount => ReviewSource == "Working"
        ? (ReviewStagedPatch ? SelectedStagedFiles.Count : SelectedUnstagedFiles.Count) : SelectedCommitFiles.Count;

    private void InitializeMultiFileSelection()
    {
        SelectedStagedFiles.CollectionChanged += (_, _) => NotifyInspectorSelectionActions();
        SelectedUnstagedFiles.CollectionChanged += (_, _) => NotifyInspectorSelectionActions();
        StagedFiles.CollectionChanged += (_, _) => NotifyInspectorSelectionActions();
        UnstagedFiles.CollectionChanged += (_, _) => NotifyInspectorSelectionActions();
    }

    private void NotifyInspectorSelectionActions()
    {
        foreach (var name in new[] { nameof(HasWorkingFileSelection), nameof(InspectorStageLabel),
            nameof(InspectorUnstageLabel), nameof(InspectorDiscardLabel), nameof(CanInspectorStage), nameof(CanInspectorUnstage), nameof(ReviewTitle) })
            OnPropertyChanged(name);
    }

    private void SynchronizeInspectorSelection<T>(ObservableCollection<T> selection, T? file) where T : class
    {
        if (file is null || IsUpdatingContextSnapshot || _clearingInspectorSelection || selection.Contains(file)) return;
        // External navigation still highlights one file; clicks inside an existing range keep the range intact.
        ClearInspectorFileSelection();
        _clearingInspectorSelection = true;
        try { selection.Add(file); }
        finally { _clearingInspectorSelection = false; }
    }

    partial void OnIsMultiFileReviewChanged(bool value)
    {
        OnPropertyChanged(nameof(ReviewTitle));
        OnPropertyChanged(nameof(IsSingleFileReview));
        NotifyCenterReview();
    }

    internal void ActivateWorkingFileSelection(bool staged, WorkingFileRow? active)
    {
        if (IsUpdatingContextSnapshot || _clearingInspectorSelection) return;
        if (staged) SelectedUnstagedFiles.Clear(); else SelectedStagedFiles.Clear();
        ReviewStagedPatch = staged;
        var selection = staged ? SelectedStagedFiles : SelectedUnstagedFiles;
        if (active is not null && selection.Contains(active)) SelectedWorkingFile = active;
        else if (selection.Count > 0) SelectedWorkingFile = selection.Last();
        if (SelectedWorkingFile is { } file && selection.Count > 0) OpenFileReview("Working", file.Path);
        UpdateMultiFileContext();
    }

    internal void ActivateCommitFileSelection(HistoryFileRow? active)
    {
        if (IsUpdatingContextSnapshot || _clearingInspectorSelection) return;
        if (active is not null && SelectedCommitFiles.Contains(active)) SelectedHistoryFileRow = active;
        else if (SelectedCommitFiles.Count > 0) SelectedHistoryFileRow = SelectedCommitFiles.Last();
        if (SelectedHistoryFileRow is { } file && SelectedCommitFiles.Count > 0) OpenFileReview("Commit", file.Path);
        UpdateMultiFileContext();
    }

    private void UpdateMultiFileContext()
    {
        var multiple = MultiSelectionCount > 1;
        IsMultiFileReview = multiple;
        OnPropertyChanged(nameof(ReviewTitle));
        if (multiple)
        {
            if (FileReviewMode is not ("File" or "Diff")) FileReviewMode = "Diff";
            IsFileReview = true;
            _ = LoadMultiFilePreviewsAsync();
        }
        else
        {
            Interlocked.Increment(ref _multiFilePreviewRevision);
            _multiFilePreviewContext = "";
            MultiFilePreviews.Clear();
        }
    }

    internal void ReconcileInspectorSelection()
    {
        foreach (var item in SelectedStagedFiles.Where(item => !StagedFiles.Contains(item)).ToArray()) SelectedStagedFiles.Remove(item);
        foreach (var item in SelectedUnstagedFiles.Where(item => !UnstagedFiles.Contains(item)).ToArray()) SelectedUnstagedFiles.Remove(item);
        foreach (var item in SelectedCommitFiles.Where(item => !HistoryFileRows.Contains(item)).ToArray()) SelectedCommitFiles.Remove(item);
        if (IsMultiFileReview) UpdateMultiFileContext();
    }

    [RelayCommand]
    private void ClearInspectorFileSelection()
    {
        _clearingInspectorSelection = true;
        try
        {
            SelectedStagedFiles.Clear();
            SelectedUnstagedFiles.Clear();
            SelectedCommitFiles.Clear();
        }
        finally { _clearingInspectorSelection = false; }
        IsMultiFileReview = false;
        _multiFilePreviewContext = "";
        MultiFilePreviews.Clear();
        Interlocked.Increment(ref _multiFilePreviewRevision);
    }

    [RelayCommand]
    private Task StageInspectorFilesAsync() => ChangeInspectorFilesAsync(false);
    [RelayCommand]
    private Task UnstageInspectorFilesAsync() => ChangeInspectorFilesAsync(true);

    private Task ChangeInspectorFilesAsync(bool unstage)
    {
        if (IsFeatureBusy || !(unstage ? CanInspectorUnstage : CanInspectorStage)) return Task.CompletedTask;
        var selected = unstage ? SelectedStagedFiles : SelectedUnstagedFiles;
        var paths = (selected.Count > 1 ? selected : unstage ? StagedFiles : UnstagedFiles)
            .Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        return RunFeatureMutationAsync(async repository =>
        {
            foreach (var path in paths)
                if (unstage) await _gitActions.UnstageAsync(repository, path);
                else await _gitActions.StageAsync(repository, path);
        }, $"{paths.Length} files {(unstage ? "unstaged" : "staged")}.");
    }

    internal async Task DiscardInspectorFilesAsync(string[] paths)
    {
        if (!CanDiscardAllChanges || IsFeatureBusy) return;
        if (paths.Length == 0) { await DiscardAllChangesAsync(); return; }
        await RunFeatureMutationAsync(repository => new GitDiscardSelectedService().DiscardAsync(repository, paths, "DISCARD"),
            $"Discarded changes in {paths.Length} selected files.");
        CloseReviewIfUnchanged();
    }

    internal async Task LoadMultiFilePreviewsAsync()
    {
        var revision = Interlocked.Increment(ref _multiFilePreviewRevision);
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var repository = SelectedRepository?.Path;
        var working = ReviewSource == "Working";
        var staged = ReviewStagedPatch;
        var sha = _activeHistoryCommit?.Sha;
        var diff = FileReviewMode != "File";
        var paths = working
            ? (staged ? SelectedStagedFiles : SelectedUnstagedFiles).Select(file => file.Path).ToArray()
            : SelectedCommitFiles.Select(file => file.Path).ToArray();
        if (repository is null || paths.Length < 2) return;
        bool Current() => revision == Interlocked.Read(ref _multiFilePreviewRevision)
            && version == Interlocked.Read(ref _repositorySelectionVersion) && IsMultiFileReview;
        var context = string.Join('\n', new[] { repository, working ? "Working" : sha ?? "", staged.ToString(), diff.ToString() }.Concat(paths));
        if (_multiFilePreviewContext != context)
        {
            _multiFilePreviewContext = context;
            MultiFileFeedback = $"Loading {paths.Length} selected files…";
            MultiFilePreviews.Clear();
        }
        var previews = new List<MultiFilePreview>();
        foreach (var path in paths.Take(50))
        {
            if (!Current()) return;
            string content;
            try
            {
                if (working && diff)
                {
                    var patch = await _diffs.GetFileDiffAsync(repository, path);
                    content = staged ? patch.StagedPatch : patch.UnstagedPatch;
                }
                else if (working) content = await new GitFileBrowserService().ReadContentAsync(repository, path, staged);
                else if (sha is null) return;
                else if (diff) content = (await _historyPatch.GetAsync(repository, sha, path)).PatchText;
                else content = (await _historyContent.GetAsync(repository, sha, path)).Content;
            }
            catch (Exception e) { content = SafeMessage(e); }
            previews.Add(new(path, content, diff));
        }
        if (!Current()) return;
        CollectionUpdates.Sync(MultiFilePreviews, previews, preview => preview.Path);
        MultiFileFeedback = paths.Length > 50 ? $"{paths.Length} files selected · Showing the first 50 previews"
            : $"{paths.Length} files selected · Read-only {(working ? staged ? "staged " : "unstaged " : "")}{(diff ? "diffs" : "files")}";
    }
}
