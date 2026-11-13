using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class GitPullModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitllama-pull-modes-" + Guid.NewGuid().ToString("N"));
    private readonly Signature _author = new("Pull Test", "pull@example.invalid", DateTimeOffset.Now);
    private string Source => Path.Combine(_root, "source");
    private string Local => Path.Combine(_root, "local");

    private (string Initial, string Local, string Remote) Prepare(bool divergent, bool conflict = false)
    {
        Repository.Init(Source);
        string initial;
        using (var repo = new Repository(Source))
        {
            File.WriteAllText(Path.Combine(Source, "readme.txt"), "initial\n");
            initial = Commit(repo, "Initial");
        }
        Repository.Clone(Source, Local);
        var local = initial;
        if (divergent)
        {
            using var repo = new Repository(Local);
            File.WriteAllText(Path.Combine(Local, conflict ? "readme.txt" : "local.txt"), "local\n");
            local = Commit(repo, "Local work");
        }
        using (var repo = new Repository(Source))
        {
            File.WriteAllText(Path.Combine(Source, conflict ? "readme.txt" : "remote.txt"), "remote\n");
            return (initial, local, Commit(repo, "Remote work"));
        }
    }

    [Theory]
    [InlineData(GitPullMode.Merge, false)]
    [InlineData(GitPullMode.Merge, true)]
    [InlineData(GitPullMode.FastForwardOnly, false)]
    [InlineData(GitPullMode.FastForwardOnly, true)]
    [InlineData(GitPullMode.Rebase, false)]
    [InlineData(GitPullMode.Rebase, true)]
    public async Task PullHonorsSelectedModeAndPreservesLocalCommits(GitPullMode mode, bool divergent)
    {
        var commits = Prepare(divergent);
        var service = new GitRemoteService();
        if (mode == GitPullMode.FastForwardOnly && divergent)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PullAsync(Local, _author, mode: mode));
            Assert.Contains("Pull options", error.Message);
            using var rejected = new Repository(Local);
            Assert.Equal(commits.Local, rejected.Head.Tip.Sha);
            Assert.Equal(CurrentOperation.None, rejected.Info.CurrentOperation);
            Assert.False(File.Exists(Path.Combine(Local, "remote.txt")));
            return;
        }
        var status = await service.PullAsync(Local, _author, mode: mode);
        using var result = new Repository(Local);
        Assert.Equal(divergent ? MergeStatus.NonFastForward : MergeStatus.FastForward, status);
        Assert.True(File.Exists(Path.Combine(Local, "remote.txt")));
        Assert.False(result.Info.IsHeadDetached);
        Assert.Equal(CurrentOperation.None, result.Info.CurrentOperation);
        Assert.False(result.RetrieveStatus().IsDirty);
        if (!divergent) Assert.Equal(commits.Remote, result.Head.Tip.Sha);
        else
        {
            Assert.Equal("local\n", File.ReadAllText(Path.Combine(Local, "local.txt")).Replace("\r\n", "\n"));
            Assert.Equal(_author.Name, result.Head.Tip.Committer.Name);
            if (mode == GitPullMode.Merge)
                Assert.Equal(new[] { commits.Local, commits.Remote }, result.Head.Tip.Parents.Select(parent => parent.Sha));
            else
            {
                Assert.Equal(commits.Remote, Assert.Single(result.Head.Tip.Parents).Sha);
                Assert.Equal("Local work", result.Head.Tip.MessageShort);
                Assert.NotEqual(commits.Local, result.Head.Tip.Sha);
            }
        }
    }

    [Theory]
    [InlineData(GitPullMode.Merge)]
    [InlineData(GitPullMode.FastForwardOnly)]
    [InlineData(GitPullMode.Rebase)]
    public async Task PullAfterResetToAnOlderCommitFastForwards(GitPullMode mode)
    {
        var commits = Prepare(true);
        using (var repo = new Repository(Local)) repo.Reset(ResetMode.Hard, repo.Lookup<Commit>(commits.Initial));
        Assert.Equal(MergeStatus.FastForward, await new GitRemoteService().PullAsync(Local, _author, mode: mode));
        using var result = new Repository(Local);
        Assert.Equal(commits.Remote, result.Head.Tip.Sha);
        Assert.False(File.Exists(Path.Combine(Local, "local.txt")));
    }

    [Fact]
    public async Task MergePullExposesConflictsForResolution()
    {
        var commits = Prepare(true, conflict: true);
        Assert.Equal(MergeStatus.Conflicts, await new GitRemoteService().PullAsync(Local, _author));
        using var repo = new Repository(Local);
        Assert.Equal(commits.Local, repo.Head.Tip.Sha);
        Assert.Equal(CurrentOperation.Merge, repo.Info.CurrentOperation);
        Assert.Single(repo.Index.Conflicts);
        Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(Local, "readme.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebaseConflictsCanBeContinuedOrAborted(bool abort)
    {
        var commits = Prepare(true, conflict: true);
        var service = new GitRemoteService();
        Assert.Equal(MergeStatus.Conflicts, await service.PullAsync(Local, _author, mode: GitPullMode.Rebase));
        using (var repo = new Repository(Local)) Assert.Single(repo.Index.Conflicts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ContinuePullRebaseAsync(Local, _author));
        if (abort) await service.AbortPullRebaseAsync(Local);
        else
        {
            File.WriteAllText(Path.Combine(Local, "readme.txt"), "resolved\n");
            using (var repo = new Repository(Local)) Commands.Stage(repo, "readme.txt");
            Assert.Equal(RebaseStatus.Complete, await service.ContinuePullRebaseAsync(Local, _author));
        }
        using var result = new Repository(Local);
        Assert.False(result.Info.IsHeadDetached);
        Assert.Equal(CurrentOperation.None, result.Info.CurrentOperation);
        Assert.False(result.RetrieveStatus().IsDirty);
        Assert.Equal(abort ? "local\n" : "resolved\n", File.ReadAllText(Path.Combine(Local, "readme.txt")).Replace("\r\n", "\n"));
        if (abort) Assert.Equal(commits.Local, result.Head.Tip.Sha);
        else Assert.Equal(commits.Remote, Assert.Single(result.Head.Tip.Parents).Sha);
    }

    [Theory]
    [InlineData(GitPullMode.Merge)]
    [InlineData(GitPullMode.FastForwardOnly)]
    [InlineData(GitPullMode.Rebase)]
    public async Task PullProtectsUncommittedFiles(GitPullMode mode)
    {
        var commits = Prepare(false);
        File.WriteAllText(Path.Combine(Local, "readme.txt"), "unsaved edits\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitRemoteService().PullAsync(Local, _author, mode: mode));
        using var repo = new Repository(Local);
        Assert.Equal(commits.Local, repo.Head.Tip.Sha);
        Assert.Equal("unsaved edits\n", File.ReadAllText(Path.Combine(Local, "readme.txt")));
    }

    private string Commit(Repository repo, string message)
    {
        Commands.Stage(repo, "*");
        return repo.Commit(message, _author, _author).Sha;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
