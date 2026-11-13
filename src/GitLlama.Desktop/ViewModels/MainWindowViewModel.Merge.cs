using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;
using GitLlama.Infrastructure;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly Dictionary<string, ConflictResolverViewModel> _conflictDrafts = new(StringComparer.Ordinal);
    private long _conflictOpenRevision;
    /// <summary>The Git operation that stopped part-way, such as "Merge" or "Rebase", or "None".</summary>
    [ObservableProperty]
    private string repositoryOperation = "None";

    /// <summary>The tab shown in the merge and conflict resolution document: Merge, Conflicts or Rebase.</summary>
    [ObservableProperty]
    private int mergeTabIndex;

    public int ConflictedFileCount => WorkingFiles.Count(file => file.Status == "Conflict");
    public bool HasMergeConflicts => ConflictedFileCount > 0;
    public bool IsMergeInProgress => RepositoryOperation == "Merge";
    public bool IsPullRebaseInProgress => RepositoryOperation is "RebaseMerge" or "RebaseInteractive";
    public bool CanContinuePullRebase => IsPullRebaseInProgress && !HasMergeConflicts;
    public bool ShowMergeBanner => IsMergeInProgress || IsPullRebaseInProgress || HasMergeConflicts;
    public bool CanFinishMerge => IsMergeInProgress && !HasMergeConflicts;
    public string MergeBannerText =>
        IsPullRebaseInProgress
            ? HasMergeConflicts ? "The rebase stopped on conflicts. Resolve and stage the files, then continue the rebase."
                : "The conflicts are resolved. Continue the rebase to replay the remaining commits."
        : HasMergeConflicts
            ? (
                ConflictedFileCount == 1
                    ? "The merge stopped on 1 conflicted file. Resolve it, then finish the merge."
                    : $"The merge stopped on {ConflictedFileCount} conflicted files. Resolve them, then finish the merge."
            )
            : "All conflicts are resolved. Finish the merge to commit it.";

    private readonly GitConflictDocumentService _conflictDocuments = new();

    /// <summary>The conflicted file open in the resolver, if any.</summary>
    [ObservableProperty]
    private ConflictResolverViewModel? conflictResolver;

    /// <summary>Whether the centre shows the conflict resolver.</summary>
    [ObservableProperty]
    private bool isConflictResolver;

    public ObservableCollection<WorkingFileRow> ConflictedFiles { get; } = new BatchObservableCollection<WorkingFileRow>();
    public ObservableCollection<WorkingFileRow> ResolvedFiles { get; } = new BatchObservableCollection<WorkingFileRow>();
    public string ConflictedFilesCaption => $"Conflicted files ({ConflictedFiles.Count})";
    public string ResolvedFilesCaption => $"Resolved files ({ResolvedFiles.Count})";
    public bool HasResolvedFiles => ResolvedFiles.Count > 0;

    /// <summary>The working changes inspector lists conflicted and resolved files while a merge is in progress.</summary>
    public bool IsConflictInspector => ShowMergeBanner;
    public bool IsCommitInspector => !ShowMergeBanner;
    public string WorkingInspectorTitle => IsPullRebaseInProgress ? "Rebase in progress"
        : ShowMergeBanner ? "Merge in progress" : "Commit changes";

    partial void OnRepositoryOperationChanged(string value) => NotifyMergeState();

    /// <summary>The conflicted file open in the resolver, highlighted in the inspector.</summary>
    public WorkingFileRow? SelectedConflictFile =>
        ConflictedFiles.FirstOrDefault(file => file.Path == ConflictResolver?.Path);

    partial void OnConflictResolverChanged(ConflictResolverViewModel? value) =>
        OnPropertyChanged(nameof(SelectedConflictFile));

    partial void OnIsConflictResolverChanged(bool value)
    {
        if (!value)
        {
            Interlocked.Increment(ref _conflictOpenRevision);
            ConflictResolver = null;
        }
    }

    private void NotifyMergeState()
    {
        CollectionUpdates.Sync(
            ConflictedFiles,
            WorkingFiles.Where(file => file.Status == "Conflict"),
            file => file.Path
        );
        CollectionUpdates.Sync(ResolvedFiles, WorkingFiles.Where(file => file.Status != "Conflict"), file => file.Path);
        if (_conflictDrafts.Count > 0)
        {
            var conflictedPaths = ConflictedFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var path in _conflictDrafts.Keys.Where(path => !conflictedPaths.Contains(path)).ToArray())
                _conflictDrafts.Remove(path);
        }
        OnPropertyChanged(nameof(ConflictedFilesCaption));
        OnPropertyChanged(nameof(SelectedConflictFile));
        OnPropertyChanged(nameof(ResolvedFilesCaption));
        OnPropertyChanged(nameof(HasResolvedFiles));
        OnPropertyChanged(nameof(IsConflictInspector));
        OnPropertyChanged(nameof(IsCommitInspector));
        OnPropertyChanged(nameof(CanDiscardAllChanges));
        OnPropertyChanged(nameof(WorkingInspectorTitle));
        OnPropertyChanged(nameof(ConflictedFileCount));
        OnPropertyChanged(nameof(HasMergeConflicts));
        OnPropertyChanged(nameof(IsMergeInProgress));
        OnPropertyChanged(nameof(IsPullRebaseInProgress));
        OnPropertyChanged(nameof(CanContinuePullRebase));
        OnPropertyChanged(nameof(ShowMergeBanner));
        OnPropertyChanged(nameof(CanFinishMerge));
        OnPropertyChanged(nameof(MergeBannerText));
        OnPropertyChanged(nameof(WorkingChangesCaption));
        ContinueMergeCommand.NotifyCanExecuteChanged();
        AbortMergeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the first conflicted file in the resolver.</summary>
    [RelayCommand]
    private Task OpenConflictResolutionAsync() => OpenConflictFileAsync(ConflictedFiles.FirstOrDefault()?.Path);

    /// <summary>Opens a conflicted file in the resolver, with A (ours), B (theirs) and the output.</summary>
    [RelayCommand]
    private async Task OpenConflictFileAsync(string? path)
    {
        if (SelectedRepository is not { } repo || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var revision = Interlocked.Increment(ref _conflictOpenRevision);
        try
        {
            var document = await _conflictDocuments.ReadAsync(repo.Path, path);
            if (version != Interlocked.Read(ref _repositorySelectionVersion)
                || revision != Interlocked.Read(ref _conflictOpenRevision))
            {
                return;
            }

            var reused = _conflictDrafts.TryGetValue(path, out var resolver) && resolver.MatchesSource(document);
            if (!reused)
            {
                resolver = new ConflictResolverViewModel(document);
                _conflictDrafts[path] = resolver;
            }
            IsFileReview = false;
            IsWorkingTreeInspector = true;
            ConflictResolver = resolver;
            IsConflictResolver = true;
            resolver!.Navigate(reused ? resolver.CurrentConflict : 1);
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Open conflict resolver");
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private void CloseConflictResolver()
    {
        Interlocked.Increment(ref _conflictOpenRevision);
        IsConflictResolver = false;
    }

    /// <summary>Writes the resolver's output, marks the file resolved and moves on to the next conflicted file.</summary>
    [RelayCommand]
    private async Task SaveConflictResolutionAsync()
    {
        if (ConflictResolver is not { CanSave: true } resolver)
        {
            return;
        }

        var path = resolver.Path;
        var text = resolver.ResultText();
        await RunFeatureMutationAsync(
            repo => _conflictResolution.WriteAndResolveAsync(repo, path, text),
            $"Resolved {path}."
        );
        await ContinueAfterResolvingAsync(path);
    }

    /// <summary>Resolves a conflicted file by taking one whole side.</summary>
    public async Task UseConflictSideAsync(string path, GitConflictChoice choice)
    {
        await RunFeatureMutationAsync(
            repo => _conflictChoices.ResolveAsync(repo, path, choice),
            $"Resolved {path} using {(choice == GitConflictChoice.Ours ? "A (ours)" : "B (theirs)")}."
        );
        if (ConflictResolver?.Path == path)
        {
            await ContinueAfterResolvingAsync(path);
        }
    }

    [RelayCommand]
    private Task UseOursForConflictAsync() =>
        ConflictResolver is { } resolver
            ? UseConflictSideAsync(resolver.Path, GitConflictChoice.Ours)
            : Task.CompletedTask;

    [RelayCommand]
    private Task UseTheirsForConflictAsync() =>
        ConflictResolver is { } resolver
            ? UseConflictSideAsync(resolver.Path, GitConflictChoice.Theirs)
            : Task.CompletedTask;

    /// <summary>Marks conflicted files resolved as they are, except files that still contain conflict markers.</summary>
    [RelayCommand]
    private async Task MarkAllConflictsResolvedAsync()
    {
        IReadOnlyList<string> stillMarked = [];
        await RunFeatureMutationAsync(
            async repo => stillMarked = await _conflictResolution.MarkAllResolvedAsync(repo),
            "Marked all conflicted files resolved."
        );
        if (stillMarked.Count > 0)
        {
            Status =
                (
                    stillMarked.Count == 1
                        ? $"{stillMarked[0]} still has conflict markers, so it was left conflicted."
                        : $"{stillMarked.Count} files still have conflict markers, so they were left conflicted."
                ) + " Open them to choose lines.";
        }

        if (ConflictResolver is { } resolver && ConflictedFiles.All(file => file.Path != resolver.Path))
        {
            IsConflictResolver = false;
        }
    }

    private async Task ContinueAfterResolvingAsync(string path)
    {
        if (ConflictedFiles.Any(file => file.Path == path))
        {
            // Still conflicted, so saving failed; the status bar explains why.
            return;
        }

        if (ConflictedFiles.FirstOrDefault()?.Path is { } next)
        {
            await OpenConflictFileAsync(next);
        }
        else
        {
            IsConflictResolver = false;
            Status = IsPullRebaseInProgress ? "All conflicts are resolved. Continue the rebase."
                : "All conflicts are resolved. Finish the merge to commit it.";
        }
    }
}
