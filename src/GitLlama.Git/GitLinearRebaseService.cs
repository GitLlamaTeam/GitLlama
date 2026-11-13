using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>Conservative linear rebase: replay commits onto a different local branch.</summary>
public sealed class GitLinearRebaseService
{
    public Task<string> RebaseOntoAsync(
        string repositoryPath,
        string targetBranch,
        CancellationToken cancellationToken = default
    ) => RebaseWithPlanAsync(repositoryPath, targetBranch, null, cancellationToken);

    public Task<string> RebaseWithPlanAsync(
        string repositoryPath,
        string targetBranch,
        IReadOnlyList<GitRebaseStep>? plan,
        CancellationToken cancellationToken = default
    ) => RebaseWithMessagesAsync(repositoryPath, targetBranch, plan, null, cancellationToken);

    public Task<string> RebaseWithMessagesAsync(
        string repositoryPath,
        string targetBranch,
        IReadOnlyList<GitRebaseStep>? plan,
        IReadOnlyDictionary<string, string>? messages,
        CancellationToken cancellationToken = default
    ) => RebaseWithEditsAsync(repositoryPath, targetBranch, plan, messages, null, cancellationToken);

    /// <summary>Replays an Edit step using explicitly supplied file content and amends its commit.</summary>
    public Task<string> RebaseWithEditsAsync(
        string repositoryPath,
        string targetBranch,
        IReadOnlyList<GitRebaseStep>? plan,
        IReadOnlyDictionary<string, string>? messages,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? edits,
        CancellationToken cancellationToken = default
    ) => RebaseAsync(repositoryPath, targetBranch, plan, messages, edits, null, cancellationToken);

    public Task<string> RebaseOntoReferenceAsync(string repositoryPath, string reference, string expectedBranch,
        CancellationToken cancellationToken = default) =>
        RebaseAsync(repositoryPath, reference, null, null, null, expectedBranch, cancellationToken);

    private Task<string> RebaseAsync(string repositoryPath, string targetBranch,
        IReadOnlyList<GitRebaseStep>? plan, IReadOnlyDictionary<string, string>? messages,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? edits, string? expectedBranch,
        CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);
                cancellationToken.ThrowIfCancellationRequested();
                using var repo = new Repository(repositoryPath);
                if (repo.Info.IsBare || repo.Info.IsHeadDetached || repo.Head.Tip is null)
                {
                    throw new InvalidOperationException("Rebase requires a checked-out local branch.");
                }

                if (repo.Info.CurrentOperation != CurrentOperation.None || repo.RetrieveStatus().IsDirty)
                {
                    throw new InvalidOperationException(
                        "Finish current operations and commit or stash changes before rebasing."
                    );
                }

                Commit target;
                if (expectedBranch is not null)
                {
                    if (repo.Head.FriendlyName != expectedBranch)
                        throw new InvalidOperationException("The checked-out branch changed. Review the rebase target again.");
                    target = repo.Lookup<Commit>(targetBranch)
                        ?? throw new InvalidOperationException("The selected rebase target no longer exists.");
                }
                else
                {
                    var branch = repo.Branches[targetBranch];
                    if (branch is null || branch.IsRemote || branch.Tip is null)
                        throw new InvalidOperationException("Choose a local target branch.");
                    if (branch.CanonicalName == repo.Head.CanonicalName)
                        throw new InvalidOperationException("Cannot rebase a branch onto itself.");
                    target = branch.Tip;
                }

                var source = repo.Head.Tip;
                var baseCommit =
                    repo.ObjectDatabase.FindMergeBase(source, target)
                    ?? throw new InvalidOperationException("Branches have no common ancestor.");
                if (expectedBranch is not null && baseCommit.Id == target.Id)
                    return source.Sha;
                var commits = repo
                    .Commits.QueryBy(
                        new CommitFilter
                        {
                            IncludeReachableFrom = source,
                            ExcludeReachableFrom = baseCommit,
                            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse,
                        }
                    )
                    .ToArray();
                if (commits.Any(c => c.Parents.Count() != 1))
                {
                    throw new InvalidOperationException("This rebase supports only linear histories.");
                }

                if (commits.Length == 0 && expectedBranch is null)
                {
                    throw new InvalidOperationException("There are no commits to replay.");
                }

                if (expectedBranch is not null && commits.Length > 0)
                {
                    var outgoing = commits.Select(commit => commit.Id).ToHashSet();
                    var remoteTips = repo.Branches.Where(branch => branch.IsRemote && branch.Tip is not null)
                        .Select(branch => branch.Tip).ToArray();
                    if (remoteTips.Length > 0 && repo.Commits.QueryBy(new CommitFilter
                    {
                        IncludeReachableFrom = remoteTips,
                        ExcludeReachableFrom = baseCommit
                    }).Any(commit => outgoing.Contains(commit.Id)))
                        throw new InvalidOperationException("The rebase would rewrite commits in remote-tracking history.");
                }

                if (plan is not null)
                {
                    GitRebasePlanService.ValidatePlan(plan);
                    if (
                        plan.Count != commits.Length
                        || !plan.Select(x => x.CommitSha)
                            .OrderBy(x => x, StringComparer.Ordinal)
                            .SequenceEqual(commits.Select(x => x.Sha).OrderBy(x => x, StringComparer.Ordinal))
                    )
                    {
                        throw new InvalidOperationException(
                            "The rebase plan must contain each outgoing commit exactly once."
                        );
                    }

                    if (
                        plan.Any(x =>
                            x.Action == GitRebaseAction.Edit
                            && (edits is null || !edits.TryGetValue(x.CommitSha, out var changes) || changes.Count == 0)
                        )
                    )
                    {
                        throw new InvalidOperationException("Every Edit step needs explicit file changes.");
                    }

                    if (
                        plan.Any(x =>
                            x.Action == GitRebaseAction.Reword
                            && (
                                messages is null
                                || !messages.TryGetValue(x.CommitSha, out var message)
                                || string.IsNullOrWhiteSpace(message)
                            )
                        )
                    )
                    {
                        throw new InvalidOperationException(
                            "Every Reword action needs a non-empty replacement commit message."
                        );
                    }

                    commits = plan.Where(x => x.Action != GitRebaseAction.Drop)
                        .Select(x => commits.Single(c => c.Sha == x.CommitSha))
                        .ToArray();
                    if (commits.Length == 0)
                    {
                        throw new InvalidOperationException(
                            "Dropping every commit is not supported; reset the branch explicitly."
                        );
                    }
                }
                // Create a temporary replay branch so failure does not rewrite the source branch.
                var tempName = "gitllama-rebase-" + Guid.NewGuid().ToString("N");
                var originalBranch = repo.Head.FriendlyName;
                var originalCommitId = source.Id;
                var originalMoved = false;
                var temporary = repo.CreateBranch(tempName, target);
                var completed = false;
                try
                {
                    GitCheckoutTransaction.Checkout(repo, temporary, cancellationToken);
                    foreach (var commit in commits)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var action =
                            plan?.SingleOrDefault(x => x.CommitSha == commit.Sha)?.Action ?? GitRebaseAction.Pick;
                        var result = repo.CherryPick(
                            commit,
                            commit.Committer,
                            new CherryPickOptions { CommitOnSuccess = true }
                        );
                        if (result.Status != CherryPickStatus.CherryPicked)
                        {
                            throw new InvalidOperationException(
                                $"Rebase stopped at {commit.Sha[..7]}: {result.Status}. Original branch is unchanged."
                            );
                        }

                        if (action == GitRebaseAction.Edit)
                        {
                            var current = repo.Head.Tip!;
                            foreach (var change in edits![commit.Sha])
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var root = Path.GetFullPath(repo.Info.WorkingDirectory);
                                var full = Path.GetFullPath(Path.Combine(root, change.Key));
                                if (
                                    Path.IsPathRooted(change.Key)
                                    || !full.StartsWith(
                                        root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                        OperatingSystem.IsWindows()
                                            ? StringComparison.OrdinalIgnoreCase
                                            : StringComparison.Ordinal
                                    )
                                )
                                {
                                    throw new InvalidOperationException(
                                        "Edited path must remain inside the repository."
                                    );
                                }

                                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                                File.WriteAllText(full, change.Value);
                                Commands.Stage(repo, change.Key);
                            }

                            var tree = repo.Index.WriteToTree();
                            var amended = repo.ObjectDatabase.CreateCommit(
                                current.Author,
                                current.Committer,
                                current.Message,
                                tree,
                                current.Parents,
                                false
                            );
                            repo.Refs.UpdateTarget(repo.Head.Reference.ResolveToDirectReference(), amended.Id);
                            repo.Reset(ResetMode.Hard, amended);
                        }

                        if (action == GitRebaseAction.Reword)
                        {
                            var current = repo.Head.Tip!;
                            var rewrittenMessage = messages![commit.Sha];
                            var rewrittenCommit = repo.ObjectDatabase.CreateCommit(
                                current.Author,
                                current.Committer,
                                rewrittenMessage,
                                current.Tree,
                                current.Parents,
                                false
                            );
                            repo.Refs.UpdateTarget(repo.Head.Reference.ResolveToDirectReference(), rewrittenCommit.Id);
                            repo.Reset(ResetMode.Hard, rewrittenCommit);
                        }

                        if (action is GitRebaseAction.Squash or GitRebaseAction.Fixup)
                        {
                            var tip = repo.Head.Tip!;
                            var previous = tip.Parents.Single();
                            if (previous.Id == target.Id)
                            {
                                throw new InvalidOperationException("Cannot squash or fixup into the target branch.");
                            }

                            var combinedMessage =
                                action == GitRebaseAction.Squash
                                    ? previous.Message.TrimEnd() + "\n\n" + commit.Message.TrimEnd()
                                    : previous.Message;
                            var merged = repo.ObjectDatabase.CreateCommit(
                                previous.Author,
                                previous.Committer,
                                combinedMessage,
                                tip.Tree,
                                previous.Parents,
                                false
                            );
                            repo.Refs.UpdateTarget(repo.Head.Reference.ResolveToDirectReference(), merged.Id);
                            repo.Reset(ResetMode.Hard, merged);
                        }
                    }

                    var rewritten = repo.Head.Tip ?? throw new InvalidOperationException("Rebase produced no commit.");
                    // Move the original branch only after successful replay, then check out the
                    // rewritten branch to synchronize HEAD, index and working tree.
                    repo.Refs.UpdateTarget(repo.Branches[originalBranch].Reference, rewritten.Id);
                    originalMoved = true;
                    Commands.Checkout(
                        repo,
                        repo.Branches[originalBranch],
                        new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force }
                    );
                    completed = true;
                    return rewritten.Sha;
                }
                finally
                {
                    if (!completed && originalMoved)
                    {
                        repo.Refs.UpdateTarget(repo.Branches[originalBranch].Reference, originalCommitId);
                    }

                    if (!completed && repo.Info.CurrentOperation != CurrentOperation.None)
                    {
                        repo.Reset(ResetMode.Hard, repo.Head.Tip);
                    }

                    if (repo.Head.FriendlyName == tempName)
                    {
                        Commands.Checkout(
                            repo,
                            repo.Branches[originalBranch],
                            new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force }
                        );
                    }

                    if (repo.Head.FriendlyName != tempName)
                    {
                        repo.Branches.Remove(tempName);
                    }
                }
            },
            cancellationToken
        );
}
