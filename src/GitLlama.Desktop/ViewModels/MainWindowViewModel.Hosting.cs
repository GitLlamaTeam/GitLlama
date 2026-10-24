using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private long _hostingCommentRevision;

    partial void OnGithubOwnerChanged(string value) => InvalidateHosting(clearPulls: false);

    partial void OnGithubRepositoryChanged(string value) => InvalidateHosting(clearPulls: false);

    partial void OnHostingStateChanged(string value) => InvalidateHosting();

    private void InvalidateHosting(bool clearPulls = true)
    {
        Interlocked.Increment(ref _hostingRevision);
        if (clearPulls || !IsPullRequestWorkspace) Interlocked.Increment(ref _hostingCommentRevision);
        if (clearPulls)
        {
            Interlocked.Increment(ref _pullRequestRevision);
            _pullRequestLogin = "";
            PullRequests.Clear();
            SelectedPullRequest = null;
            HasMorePullRequests = false;
        }
        Issues.Clear();
        if (clearPulls || !IsPullRequestWorkspace) HostingComments.Clear();
        SelectedIssue = null;
        HasMoreIssues = false;
        _pullPage = _issuePage = 1;
        if (clearPulls || !IsPullRequestWorkspace) HostingDetails = "Select a pull request or issue.";
    }

    partial void OnSelectedPullRequestChanged(HostingItem? value)
    {
        if (value is not null)
        {
            IsPullRequestWorkspace = true;
            _ = ShowHostingItemAsync(value);
        }
    }

    partial void OnSelectedIssueChanged(HostingItem? value)
    {
        if (value is not null)
        {
            IsPullRequestWorkspace = false;
            _ = ShowHostingItemAsync(value);
        }
    }

    internal void ActivateHostingView(bool pulls)
    {
        IsPullRequestWorkspace = pulls;
        var selected = pulls ? SelectedPullRequest : SelectedIssue;
        if (selected is not null)
        {
            _ = ShowHostingItemAsync(selected);
        }
        else
        {
            Interlocked.Increment(ref _hostingCommentRevision);
            HostingDetails = "Select a " + (pulls ? "pull request." : "issue.");
            HostingComments.Clear();
        }
    }

    private HostingRepository HostingTarget(HostingItem? item = null) => item?.Repository ?? new(GithubOwner.Trim(), GithubRepository.Trim());

    private async Task ShowHostingItemAsync(HostingItem item)
    {
        var revision = Interlocked.Increment(ref _hostingCommentRevision);
        var repository = HostingTarget(item);
        var token = GithubToken;
        HostingDetails =
            $"{repository.Owner}/{repository.Name} · #{item.Number} {item.Title}\n{item.State} · {item.Author}\n{item.HeadBranch}{(item.IsPullRequest ? " → " + item.BaseBranch : "")}\n\n{item.Body}";
        HostingComments.Clear();
        HostingComment = "";
        try
        {
            var comments = await _hostingProvider.CommentsAsync(repository, item.Number, token);
            if (revision != Interlocked.Read(ref _hostingCommentRevision))
            {
                return;
            }

            foreach (var comment in comments)
            {
                HostingComments.Add(comment);
            }
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _hostingCommentRevision))
            {
                HostingStatus = PresentError(e);
            }
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private Task RefreshPullRequestsAsync(CancellationToken ct) => LoadAllPullRequestsAsync(ct);

    [RelayCommand]
    private Task RefreshIssuesAsync() => LoadHostingItemsAsync(false, false);

    [RelayCommand]
    private Task LoadMorePullRequestsAsync() => LoadAllPullRequestsAsync();

    [RelayCommand]
    private Task LoadMoreIssuesAsync() => LoadHostingItemsAsync(false, true);

    private async Task LoadHostingItemsAsync(bool pulls, bool more)
    {
        if (pulls) { await LoadAllPullRequestsAsync(); return; }
        if (IsHostingBusy)
        {
            return;
        }

        var repository = HostingTarget();
        var token = GithubToken;
        var state = HostingState;
        var revision = Interlocked.Increment(ref _hostingRevision);
        var page = more ? (pulls ? _pullPage : _issuePage) + 1 : 1;
        IsHostingBusy = true;
        HostingStatus = "Loading from GitHub…";
        try
        {
            var result = await _hostingProvider.ListAsync(repository, false, state, page, token);
            if (revision != Interlocked.Read(ref _hostingRevision)) return;
            var items = Issues;
            if (!more) items.Clear();
            foreach (var item in result.Items.Where(item => !items.Any(existing => existing.Number == item.Number)))
                items.Add(item);
            _issuePage = page;
            HasMoreIssues = result.HasMore;
            HostingStatus =
                items.Count == 0
                    ? "No matching items."
                    : $"{items.Count} {(pulls ? "pull requests" : "issues")} loaded.";
        }
        catch (OperationCanceledException)
        {
            if (revision == Interlocked.Read(ref _hostingRevision))
            {
                HostingStatus = "GitHub request timed out. Refresh to retry.";
            }
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _hostingRevision))
            {
                HostingStatus = "Could not reach GitHub: " + PresentError(e);
            }
        }
        finally
        {
            _updatingPullRequests = false;
            IsHostingBusy = false;
        }
    }

    private async Task RunHostingChangeAsync(Func<HostingRepository, string, Task> change, bool pulls, string success,
        HostingRepository? target = null)
    {
        if (IsHostingBusy)
        {
            return;
        }

        var repository = target ?? HostingTarget();
        var token = GithubToken;
        var revision = Interlocked.Read(ref _hostingRevision);
        IsHostingBusy = true;
        var completed = false;
        try
        {
            await change(repository, token);
            completed = true;
            if (revision == Interlocked.Read(ref _hostingRevision))
            {
                HostingStatus = success;
            }
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _hostingRevision))
            {
                HostingStatus = PresentError(e);
            }
        }
        finally
        {
            IsHostingBusy = false;
        }

        if (completed && revision == Interlocked.Read(ref _hostingRevision))
        {
            await LoadHostingItemsAsync(pulls, false);
            HostingStatus = success;
        }
    }

    [RelayCommand]
    private Task CreateIssueAsync()
    {
        var title = HostingDraftTitle;
        var body = HostingDraftBody;
        return RunHostingChangeAsync(
            async (repo, token) => await _hostingProvider.CreateAsync(repo, title, body, null, null, token),
            false,
            "Issue created."
        );
    }

    [RelayCommand]
    private Task CreatePullRequestAsync()
    {
        var title = HostingDraftTitle;
        var body = HostingDraftBody;
        var head = PullRequestHead;
        var @base = PullRequestBase;
        return RunHostingChangeAsync(
            async (repo, token) => await _hostingProvider.CreateAsync(repo, title, body, head, @base, token),
            true,
            "Pull request created."
        );
    }

    [RelayCommand]
    private Task PostHostingCommentAsync()
    {
        var item = IsPullRequestWorkspace ? SelectedPullRequest : SelectedIssue;
        var body = HostingComment;
        return item is null
            ? Task.CompletedTask
            : RunHostingChangeAsync(
                (repo, token) => _hostingProvider.CommentAsync(repo, item.Number, body, token),
                item.IsPullRequest,
                "Comment posted.", HostingTarget(item)
            );
    }

    [RelayCommand]
    private Task ApprovePullRequestAsync() => ReviewPullRequestAsync("APPROVE");

    [RelayCommand]
    private Task RequestPullRequestChangesAsync() => ReviewPullRequestAsync("REQUEST_CHANGES");

    private Task ReviewPullRequestAsync(string action)
    {
        var item = SelectedPullRequest;
        var body = HostingComment;
        return item is null
            ? Task.CompletedTask
            : RunHostingChangeAsync(
                (repo, token) => _hostingProvider.ReviewAsync(repo, item.Number, item.HeadSha, action, body, token),
                true,
                "Review submitted.", HostingTarget(item)
            );
    }

    [RelayCommand]
    private async Task MergePullRequestAsync()
    {
        var item = SelectedPullRequest;
        if (item is null)
        {
            return;
        }

        var target = HostingTarget(item);
        if (!await ConfirmActionAsync("Merge pull request on server?",
            $"Merge {target.Owner}/{target.Name} PR #{item.Number}: {item.Title}? This merges {item.HeadBranch} into {item.BaseBranch} on the server.",
            "Merge pull request", () => SelectedPullRequest == item && HostingTarget(item) == target))
            return;

        await RunHostingChangeAsync(
            (repo, token) => _hostingProvider.MergeAsync(repo, item.Number, item.HeadSha, token),
            true,
            "Pull request merged.", target
        );
    }

    [RelayCommand]
    private Task ToggleIssueStateAsync()
    {
        var item = SelectedIssue;
        return item is null
            ? Task.CompletedTask
            : RunHostingChangeAsync(
                (repo, token) =>
                    _hostingProvider.SetIssueStateAsync(
                        repo,
                        item.Number,
                        item.State == "open" ? "closed" : "open",
                        token
                    ),
                false,
                "Issue state updated."
            );
    }
}
