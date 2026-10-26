using LibGit2Sharp;

namespace GitLlama.Git;

public sealed record GitFileHistoryEntry(string Sha, string Subject)
{
    public string ShortSha => Sha[..7];
    public override string ToString() => $"{ShortSha} · {Subject}";
}

public sealed class GitFileHistoryService
{
    public Task<IReadOnlyList<GitFileHistoryEntry>> GetAsync(
        string repositoryPath,
        string relativePath,
        int maxCount = 100,
        CancellationToken cancellationToken = default,
        string? startCommit = null
    ) =>
        Task.Run<IReadOnlyList<GitFileHistoryEntry>>(
            () =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
                if (Path.IsPathRooted(relativePath) || relativePath.Split('/', '\\').Contains(".."))
                {
                    throw new ArgumentException("A repository-relative path is required.", nameof(relativePath));
                }

                if (maxCount is < 1 or > 1000)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxCount));
                }

                using var repo = new Repository(repositoryPath);
                var path = relativePath.Replace('\\', '/');
                var result = new List<GitFileHistoryEntry>();
                if (startCommit is null && repo.Head.Tip is null) return result;
                var commits = repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = startCommit ?? "HEAD" });
                foreach (var commit in commits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (
                        commit.Parents.Any()
                        && !commit.Parents.Any(parent =>
                            repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree)
                                .Any(change => change.Path == path || change.OldPath == path)
                        )
                    )
                    {
                        continue;
                    }

                    if (!commit.Parents.Any() && commit.Tree[path] is null)
                    {
                        continue;
                    }

                    result.Add(new GitFileHistoryEntry(commit.Sha, commit.MessageShort));
                    if (result.Count == maxCount)
                    {
                        break;
                    }
                }

                return result;
            },
            cancellationToken
        );
}
