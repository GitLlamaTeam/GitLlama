using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>Reads staged and unstaged patches without modifying the repository.</summary>
public sealed class GitDiffService
{
    public Task<GitFileDiff> GetFileDiffAsync(
        string repositoryPath,
        string relativePath,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
                using var repository = new Repository(repositoryPath);
                using var lfsRead = GitLfsFilter.ReadOnly(repository);
                if (repository.Info.IsBare)
                {
                    throw new InvalidOperationException("A bare repository has no working tree.");
                }

                var paths = new[] { relativePath };
                var options = new CompareOptions { ContextLines = 3 };
                var headTree = repository.Head.Tip?.Tree;
                cancellationToken.ThrowIfCancellationRequested();

                using var staged = repository.Diff.Compare<Patch>(headTree, DiffTargets.Index, paths, null, options);
                // LibGit2Sharp compares the index against the working directory directly.
                // Include untracked content instead of fabricating patches from text files.
                using var unstaged = repository.Diff.Compare<Patch>(paths, includeUntracked: true);
                cancellationToken.ThrowIfCancellationRequested();
                return new GitFileDiff(relativePath, staged.Content, unstaged.Content);
            },
            cancellationToken
        );
}

public sealed record GitFileDiff(string Path, string StagedPatch, string UnstagedPatch);
