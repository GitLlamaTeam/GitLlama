using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private long _fileRevisionPreviewGeneration;
    private long _fileHistoryLoadGeneration;
    private bool _settingFileRevisions;
    public ObservableCollection<GitFileHistoryEntry> FileRevisions { get; } = [];
    public IReadOnlyList<string> FileComparisonModes { get; } = ["Preview", "Current file", "Another revision"];

    [ObservableProperty] private GitFileHistoryEntry? selectedFileRevision;
    [ObservableProperty] private GitFileHistoryEntry? selectedComparisonRevision;
    [ObservableProperty] private string fileComparisonMode = "Preview";
    [ObservableProperty] private string fileRevisionContent = "";
    [ObservableProperty] private string fileComparisonContent = "";
    [ObservableProperty] private string fileRevisionLabel = "Choose a revision";
    [ObservableProperty] private string fileComparisonLabel = "";
    [ObservableProperty] private string fileHistoryFeedback = "Select a revision to preview the file.";
    [ObservableProperty] private IReadOnlyDictionary<int, string> fileRevisionLineKinds = new Dictionary<int, string>();
    [ObservableProperty] private IReadOnlyDictionary<int, string> fileComparisonLineKinds = new Dictionary<int, string>();
    public bool IsHistoryComparison => FileComparisonMode != "Preview";
    public bool IsRevisionComparison => FileComparisonMode == "Another revision";
    public int HistoryPreviewColumnSpan => IsHistoryComparison ? 1 : 3;

    partial void OnSelectedFileRevisionChanged(GitFileHistoryEntry? value) => RequestFileRevisionPreview();
    partial void OnSelectedComparisonRevisionChanged(GitFileHistoryEntry? value) => RequestFileRevisionPreview();
    partial void OnFileComparisonModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsHistoryComparison));
        OnPropertyChanged(nameof(IsRevisionComparison));
        OnPropertyChanged(nameof(HistoryPreviewColumnSpan));
        RequestFileRevisionPreview();
    }

    partial void OnReviewPathChanged(string value) => ResetFileRevisionPreview();
    partial void OnFileHistoryPathChanged(string value) => ResetFileRevisionPreview();

    private void ResetFileRevisionPreview()
    {
        Interlocked.Increment(ref _fileHistoryLoadGeneration);
        Interlocked.Increment(ref _fileRevisionPreviewGeneration);
        _settingFileRevisions = true;
        FileRevisions.Clear();
        SelectedFileRevision = SelectedComparisonRevision = null;
        _settingFileRevisions = false;
        FileRevisionContent = FileComparisonContent = "";
        FileRevisionLabel = "Choose a revision";
        FileComparisonLabel = "";
        FileHistoryFeedback = "Select a revision to preview the file.";
        FileRevisionLineKinds = FileComparisonLineKinds = new Dictionary<int, string>();
    }

    private void RequestFileRevisionPreview()
    {
        if (!_settingFileRevisions) _ = LoadFileRevisionPreviewAsync();
    }

    internal async Task LoadFileRevisionPreviewAsync()
    {
        var generation = Interlocked.Increment(ref _fileRevisionPreviewGeneration);
        var repository = SelectedRepository?.Path;
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var path = FileHistoryPath.Trim();
        var left = SelectedFileRevision;
        var right = SelectedComparisonRevision;
        var mode = FileComparisonMode;
        FileRevisionContent = FileComparisonContent = "";
        FileRevisionLineKinds = FileComparisonLineKinds = new Dictionary<int, string>();
        FileRevisionLabel = left?.ToString() ?? "Choose a revision";
        FileComparisonLabel = mode == "Current file" ? "Current working file" : right?.ToString() ?? "Choose another revision";
        if (repository is null || left is null || (mode == "Another revision" && right is null)) return;
        FileHistoryFeedback = "Loading revision…";
        bool Current() => generation == Interlocked.Read(ref _fileRevisionPreviewGeneration)
            && version == Interlocked.Read(ref _repositorySelectionVersion)
            && SelectedRepository?.Path == repository && FileHistoryPath.Trim() == path;
        try
        {
            var comparison = await new GitFileComparisonService().GetAsync(repository, path, left.Sha,
                mode == "Preview" ? left.Sha : mode == "Another revision" ? right!.Sha : null);
            if (!Current()) return;
            FileRevisionContent = comparison.Left.Text;
            FileComparisonContent = comparison.Right.Text;
            if (!comparison.Left.Exists) FileRevisionLabel += " · File absent";
            if (!comparison.Right.Exists) FileComparisonLabel += " · File absent";
            var lines = GitPartialStageService.Lines(comparison.Patch);
            FileRevisionLineKinds = lines.Where(line => line.Kind == "Deleted" && line.OldNumber.HasValue)
                .ToDictionary(line => line.OldNumber!.Value, _ => "Deleted");
            FileComparisonLineKinds = lines.Where(line => line.Kind == "Added" && line.NewNumber.HasValue)
                .ToDictionary(line => line.NewNumber!.Value, _ => "Added");
            FileHistoryFeedback = mode == "Preview"
                ? (comparison.Left.Exists ? "Read-only revision preview" : "This file does not exist in this revision.")
                : comparison.Patch.Length == 0 ? "These versions are identical."
                : $"{FileRevisionLineKinds.Count} removed lines · {FileComparisonLineKinds.Count} added lines";
        }
        catch (Exception e)
        {
            if (Current()) FileHistoryFeedback = PresentError(e);
        }
    }
}
