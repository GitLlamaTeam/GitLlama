using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>Local branch operations using LibGit2Sharp, without invoking Git.</summary>
public sealed class GitBranchService
{
    public Task<string> CreateAsync(
        string repositoryPath,
        string branchName,
        bool checkout = false,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare)
                {
                    throw new InvalidOperationException("Branch operations require a working tree.");
                }

                if (repository.Head.Tip is null)
                {
                    throw new InvalidOperationException("Create an initial commit before creating branches.");
                }

                if (!BranchNameIsValid(branchName))
                {
                    throw new ArgumentException("Invalid branch name.", nameof(branchName));
                }

                if (repository.Branches[branchName] is not null)
                {
                    throw new InvalidOperationException("Branch already exists.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var branch = repository.CreateBranch(branchName);
                if (checkout)
                {
                    try
                    {
                        GitCheckoutTransaction.Checkout(repository, branch, cancellationToken);
                    }
                    catch
                    {
                        if (repository.Head.CanonicalName != branch.CanonicalName) repository.Branches.Remove(branch);
                        throw;
                    }
                }

                return branch.FriendlyName;
            },
            cancellationToken
        );

    public Task<string> CheckoutAsync(
        string repositoryPath,
        string branchName,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare)
                {
                    throw new InvalidOperationException("Cannot checkout a bare repository.");
                }

                var branch =
                    repository.Branches[branchName] ?? throw new InvalidOperationException("Branch does not exist.");
                if (branch.IsRemote)
                {
                    throw new InvalidOperationException("Select a local branch.");
                }

                if (string.Equals(repository.Head.FriendlyName, branch.FriendlyName, StringComparison.Ordinal))
                {
                    return branch.FriendlyName;
                }

                cancellationToken.ThrowIfCancellationRequested();
                // Do not allow checkout to overwrite local work, including untracked files.
                GitCheckoutTransaction.Checkout(repository, branch, cancellationToken);
                return repository.Head.FriendlyName;
            },
            cancellationToken
        );

    /// <summary>Creates a local branch from a fetched remote branch, sets upstream and checks it out.</summary>
    public Task<string> CheckoutRemoteBranchAsync(
        string repositoryPath,
        string remoteName,
        string remoteBranchName,
        string localBranchName,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
                ArgumentException.ThrowIfNullOrWhiteSpace(remoteBranchName);
                ArgumentException.ThrowIfNullOrWhiteSpace(localBranchName);
                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare)
                {
                    throw new InvalidOperationException("Checkout requires a working tree.");
                }

                if (!BranchNameIsValid(localBranchName))
                {
                    throw new ArgumentException("Invalid local branch name.", nameof(localBranchName));
                }

                if (repository.Network.Remotes[remoteName] is null)
                {
                    throw new InvalidOperationException($"Remote '{remoteName}' does not exist.");
                }

                var remote = repository.Branches[$"refs/remotes/{remoteName}/{remoteBranchName}"];
                if (
                    remote is null
                    || !remote.IsRemote
                    || remote.Tip is null
                    || string.Equals(remoteBranchName, "HEAD", StringComparison.Ordinal)
                )
                {
                    throw new InvalidOperationException("Fetch the remote branch before checking it out.");
                }

                if (repository.Branches[$"refs/heads/{localBranchName}"] is not null)
                {
                    throw new InvalidOperationException($"Local branch '{localBranchName}' already exists.");
                }

                if (repository.RetrieveStatus().IsDirty)
                {
                    throw new InvalidOperationException(
                        "Commit or stash local changes before checking out a remote branch."
                    );
                }

                cancellationToken.ThrowIfCancellationRequested();
                var local = repository.CreateBranch(localBranchName, remote.Tip);
                try
                {
                    repository.Branches.Update(local, updater => updater.TrackedBranch = remote.CanonicalName);
                    cancellationToken.ThrowIfCancellationRequested();
                    GitCheckoutTransaction.Checkout(repository, local, cancellationToken);
                    return repository.Head.FriendlyName;
                }
                catch
                {
                    if (repository.Head.CanonicalName != local.CanonicalName)
                    {
                        repository.Branches.Remove(local);
                    }

                    throw;
                }
            },
            cancellationToken
        );

    /// <summary>Renames an existing local branch without overwriting another branch.</summary>
    public Task<string> RenameAsync(
        string repositoryPath,
        string currentName,
        string newName,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(currentName);
                ArgumentException.ThrowIfNullOrWhiteSpace(newName);
                if (!BranchNameIsValid(newName))
                {
                    throw new ArgumentException("Invalid new branch name.", nameof(newName));
                }

                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare)
                {
                    throw new InvalidOperationException("Renaming branches requires a working tree.");
                }

                var existing =
                    repository.Branches[$"refs/heads/{currentName}"]
                    ?? throw new InvalidOperationException($"Local branch '{currentName}' does not exist.");
                if (existing.IsRemote)
                {
                    throw new InvalidOperationException("Cannot rename a remote branch.");
                }

                if (string.Equals(currentName, newName, StringComparison.Ordinal))
                {
                    return existing.FriendlyName;
                }

                if (repository.Branches[$"refs/heads/{newName}"] is not null)
                {
                    throw new InvalidOperationException($"Local branch '{newName}' already exists.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                return repository.Branches.Rename(existing, newName, false).FriendlyName;
            },
            cancellationToken
        );

    public Task DeleteAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare)
                {
                    throw new InvalidOperationException("Branch operations require a working tree.");
                }

                var branch =
                    repository.Branches[branchName] ?? throw new InvalidOperationException("Branch does not exist.");
                if (branch.IsRemote)
                {
                    throw new InvalidOperationException("Cannot delete a remote branch.");
                }

                if (string.Equals(repository.Head.FriendlyName, branch.FriendlyName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Cannot delete the checked-out branch.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                repository.Branches.Remove(branch);
            },
            cancellationToken
        );

    internal static bool BranchNameIsValid(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith('-')
        && !name.StartsWith('/')
        && !name.EndsWith('/')
        && !name.EndsWith('.')
        && !name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.Contains("@{", StringComparison.Ordinal)
        && !name.Any(c => char.IsControl(c) || " ~^:?*[\\\\ ".Contains(c));
}
