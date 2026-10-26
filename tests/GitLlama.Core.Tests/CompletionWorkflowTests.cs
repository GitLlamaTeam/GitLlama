using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class CompletionWorkflowTests
{
    [Fact]
    public async Task FileComparisonsReadRevisionsAndWorkingCopyWithoutChangingRepository()
    {
        using var fixture = new Fixture();
        string before, after;
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        using (var repo = new Repository(fixture.Path))
        {
            before = repo.Head.Tip.Sha;
            File.WriteAllText(Path.Combine(fixture.Path, "file.txt"), "one\nnew value\nthree\n");
            Commands.Stage(repo, "file.txt");
            after = repo.Commit("Change value", signature, signature).Sha;
            File.WriteAllText(Path.Combine(fixture.Path, "file.txt"), "staged value\n");
            Commands.Stage(repo, "file.txt");
        }
        var current = "working value\nnot staged\n";
        File.WriteAllText(Path.Combine(fixture.Path, "file.txt"), current);
        var index = File.ReadAllBytes(Path.Combine(fixture.Path, ".git", "index"));
        var service = new GitFileComparisonService();
        var revisions = await service.GetAsync(fixture.Path, "file.txt", before, after);
        Assert.Equal(fixture.Original, revisions.Left.Text);
        Assert.Equal("one\nnew value\nthree\n", revisions.Right.Text);
        Assert.Contains("+new value", revisions.Patch);
        var working = await service.GetAsync(fixture.Path, "file.txt", before);
        Assert.Equal(current, working.Right.Text);
        Assert.Contains("+working value", working.Patch);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Path, ".git", "index")));
        Assert.Equal(current, File.ReadAllText(Path.Combine(fixture.Path, "file.txt")));
        using (var repo = new Repository(fixture.Path)) Assert.Equal(after, repo.Head.Tip.Sha);
        var history = await new GitFileHistoryService().GetAsync(fixture.Path, "file.txt", startCommit: before);
        Assert.Equal(before, Assert.Single(history).Sha);
    }

    [Fact]
    public async Task FileComparisonHandlesMissingVersionsAndRejectsBinaryOrUnsafePaths()
    {
        using var fixture = new Fixture();
        string before, deleted;
        using (var repo = new Repository(fixture.Path))
        {
            before = repo.Head.Tip.Sha;
            Commands.Remove(repo, "file.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            deleted = repo.Commit("Delete file", signature, signature).Sha;
        }
        var service = new GitFileComparisonService();
        var result = await service.GetAsync(fixture.Path, "file.txt", before, deleted);
        Assert.True(result.Left.Exists);
        Assert.False(result.Right.Exists);
        Assert.Empty(result.Right.Text);
        Assert.Contains("-one", result.Patch);
        var restored = await service.GetAsync(fixture.Path, "file.txt", deleted, before);
        Assert.False(restored.Left.Exists);
        Assert.Equal(fixture.Original, restored.Right.Text);
        var missing = await service.GetAsync(fixture.Path, "file.txt", before);
        Assert.False(missing.Right.Exists);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(fixture.Path, "../outside.txt", before));
        File.WriteAllBytes(Path.Combine(fixture.Path, "file.txt"), new byte[] { 1, 0, 2 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync(fixture.Path, "file.txt", before));
    }

    [Fact]
    public async Task InitializeCloneAndReflogRecoveryRoundTrip()
    {
        using var fixture = new Fixture();
        var service = new GitRepositoryLifecycleService();
        var initialized = (await service.InitializeAsync(Path.Combine(fixture.Root, "initialized"))).Path;
        using (var repo = new Repository(initialized))
        {
            Assert.Equal("main", repo.Head.FriendlyName);
        }

        // The current branch is listed even before its first commit.
        Assert.Equal(["main"], (await new LibGitOverviewReader().ReadOverviewAsync(initialized)).Branches);

        // With a first commit, the branch exists straight away and the README is the only file committed.
        var committedPath = Path.Combine(fixture.Root, "committed");
        Directory.CreateDirectory(committedPath);
        File.WriteAllText(Path.Combine(committedPath, "notes.txt"), "keep me out of the first commit");
        var committed = await service.InitializeAsync(
            committedPath,
            "trunk",
            initialCommit: true,
            authorName: "Test",
            authorEmail: "test@example.invalid"
        );
        Assert.True(committed.Committed);
        using (var repo = new Repository(committed.Path))
        {
            Assert.Equal("trunk", repo.Head.FriendlyName);
            Assert.Equal(["README.md"], repo.Head.Tip.Tree.Select(entry => entry.Name));
            Assert.Equal("test@example.invalid", repo.Head.Tip.Author.Email);
            Assert.Equal(FileStatus.NewInWorkdir, repo.RetrieveStatus("notes.txt"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InitializeAsync(Path.Combine(fixture.Root, "bad"), "bad name")
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync(fixture.Path));
        var clone = await service.CloneAsync(fixture.Path, Path.Combine(fixture.Root, "clone"));
        using (var repo = new Repository(clone))
        {
            Assert.NotNull(repo.Head.Tip);
        }

        var reflog = new GitReflogService();
        var entries = await reflog.ListAsync(fixture.Path);
        Assert.NotEmpty(entries);
        await reflog.RecoverBranchAsync(fixture.Path, entries[0].Sha, "recovered");
        using var repository = new Repository(fixture.Path);
        Assert.Equal(entries[0].Sha, repository.Branches["recovered"].Tip.Sha);
    }

    [Fact]
    public async Task NativeWorktreeCreationLockAndRemovalPreserveFiles()
    {
        using var fixture = new Fixture();
        using (var repo = new Repository(fixture.Path))
        {
            repo.CreateBranch("feature");
        }

        var service = new GitWorktreeService();
        var target = Path.Combine(fixture.Root, "feature-worktree");
        await service.CreateAsync(fixture.Path, "feature-tree", target, "feature");
        var linked = Assert.Single(await service.ListAsync(fixture.Path));
        Assert.Equal("feature", linked.Branch);
        Assert.True(File.Exists(Path.Combine(target, "file.txt")));
        await service.SetLockedAsync(fixture.Path, "feature-tree", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveAsync(fixture.Path, "feature-tree", "feature-tree")
        );
        await service.SetLockedAsync(fixture.Path, "feature-tree", false);
        File.AppendAllText(Path.Combine(target, "file.txt"), "dirty");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveAsync(fixture.Path, "feature-tree", "feature-tree")
        );
        File.WriteAllText(Path.Combine(target, "file.txt"), fixture.Original);
        var backup = await service.RemoveAsync(fixture.Path, "feature-tree", "feature-tree");
        Assert.Empty(await service.ListAsync(fixture.Path));
        Assert.Equal(fixture.Original, File.ReadAllText(Path.Combine(backup, "file.txt")));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task SelectedLinesStageAndUnstageWithoutChangingWorkingContent()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Path, "file.txt");
        File.WriteAllText(file, "one\nfirst addition\ntwo\nsecond addition\nthree\n");
        var diff = await new GitDiffService().GetFileDiffAsync(fixture.Path, "file.txt");
        var first = GitPartialStageService.Lines(diff.UnstagedPatch).Single(line => line.Text == "+first addition");
        var partial = new GitPartialStageService();
        await partial.ApplyAsync(fixture.Path, "file.txt", diff.UnstagedPatch, [first.Index]);
        using (var repo = new Repository(fixture.Path))
        {
            var index = repo.Lookup<Blob>(repo.Index["file.txt"].Id);
            Assert.Equal("one\nfirst addition\ntwo\nthree\n", index.GetContentText());
        }

        var staged = await new GitDiffService().GetFileDiffAsync(fixture.Path, "file.txt");
        var selected = GitPartialStageService.Lines(staged.StagedPatch).Single(line => line.Text == "+first addition");
        await partial.ApplyAsync(fixture.Path, "file.txt", staged.StagedPatch, [selected.Index], unstage: true);
        using (var repo = new Repository(fixture.Path))
        {
            Assert.Equal(fixture.Original, repo.Lookup<Blob>(repo.Index["file.txt"].Id).GetContentText());
        }

        Assert.Equal("one\nfirst addition\ntwo\nsecond addition\nthree\n", File.ReadAllText(file));
        File.AppendAllText(file, "later edit\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            partial.ApplyAsync(fixture.Path, "file.txt", diff.UnstagedPatch, [first.Index])
        );
    }

    [Fact]
    public async Task PartialNewFileCanBeUnstagedAndCrLfIsPreserved()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, "new.txt"), "alpha\r\nbeta\r\n");
        var diff = await new GitDiffService().GetFileDiffAsync(fixture.Path, "new.txt");
        var partial = new GitPartialStageService();
        var lines = GitPartialStageService.Lines(diff.UnstagedPatch).Where(line => line.Kind == "Added").ToArray();
        await partial.ApplyAsync(fixture.Path, "new.txt", diff.UnstagedPatch, [lines[0].Index]);
        using (var repo = new Repository(fixture.Path))
        {
            Assert.Equal("alpha\r\n", repo.Lookup<Blob>(repo.Index["new.txt"].Id).GetContentText());
        }

        diff = await new GitDiffService().GetFileDiffAsync(fixture.Path, "new.txt");
        await partial.ApplyAsync(
            fixture.Path,
            "new.txt",
            diff.StagedPatch,
            GitPartialStageService
                .Lines(diff.StagedPatch)
                .Where(line => line.IsChange)
                .Select(line => line.Index)
                .ToArray(),
            true
        );
        using var repository = new Repository(fixture.Path);
        Assert.Null(repository.Index["new.txt"]);
    }

    [Fact]
    public async Task PagedHistoryIncludesOtherBranchesAndDoesNotOverlapPages()
    {
        using var fixture = new Fixture();
        using (var repo = new Repository(fixture.Path))
        {
            var original = repo.Head.FriendlyName;
            var other = repo.CreateBranch("other");
            Commands.Checkout(repo, other);
            var signature = new Signature("Other", "other@example.com", DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(fixture.Path, "other.txt"), "other");
            Commands.Stage(repo, "other.txt");
            repo.Commit("Other branch only", signature, signature);
            Commands.Checkout(repo, original);
            for (var i = 0; i < 205; i++)
            {
                File.AppendAllText(Path.Combine(fixture.Path, "file.txt"), $"{i}\n");
                Commands.Stage(repo, "file.txt");
                repo.Commit("History " + i, signature, signature);
            }
        }

        var history = new GitCommitGraphService();
        var first = await history.GetPageAsync(fixture.Path);
        var second = await history.GetPageAsync(fixture.Path, first.Commits.Count);
        Assert.Equal(200, first.Commits.Count);
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Empty(first.Commits.Select(c => c.Sha).Intersect(second.Commits.Select(c => c.Sha)));
        Assert.Contains(first.Commits.Concat(second.Commits), commit => commit.Subject == "Other branch only");
        var head = await history.GetPageAsync(fixture.Path, 0, 300, "HEAD");
        Assert.DoesNotContain(head.Commits, commit => commit.Subject == "Other branch only");
    }

    [Fact]
    public async Task TextConflictResultCanBeSavedAndMerged()
    {
        using var fixture = new Fixture();
        using (var repo = new Repository(fixture.Path))
        {
            var original = repo.Head.FriendlyName;
            var side = repo.CreateBranch("side");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            Commands.Checkout(repo, side);
            File.WriteAllText(Path.Combine(fixture.Path, "file.txt"), "their edit\n");
            Commands.Stage(repo, "file.txt");
            repo.Commit("Side", signature, signature);
            Commands.Checkout(repo, original);
            File.WriteAllText(Path.Combine(fixture.Path, "file.txt"), "our edit\n");
            Commands.Stage(repo, "file.txt");
            repo.Commit("Main", signature, signature);
            Assert.Equal(MergeStatus.Conflicts, repo.Merge(side, signature).Status);
        }

        var service = new GitConflictResolutionService();
        await service.WriteAndResolveAsync(fixture.Path, "file.txt", "resolved edit\n");
        Assert.Empty(await new GitConflictDetectionService().ListAsync(fixture.Path));
        using var repository = new Repository(fixture.Path);
        Assert.Equal("resolved edit\n", repository.Lookup<Blob>(repository.Index["file.txt"].Id).GetContentText());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.WriteAndResolveAsync(fixture.Path, "../outside.txt", "unsafe")
        );
    }

    [Fact]
    public async Task EmptyRepositorySupportsHistoryFilesAndReflog()
    {
        using var fixture = new Fixture();
        var path = (
            await new GitRepositoryLifecycleService().InitializeAsync(Path.Combine(fixture.Root, "empty"))
        ).Path;
        Assert.Empty((await new GitCommitGraphService().GetPageAsync(path)).Commits);
        Assert.Empty(await new GitFileBrowserService().ListAsync(path));
        Assert.Empty(await new GitReflogService().ListAsync(path));
    }

    [Fact]
    public async Task IgnoredBuildFoldersAreExcludedAndUntrackedDirectoriesExpandToFiles()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, ".gitignore"), "build/\n**/bin/\n**/obj/\n*.user\n");
        Directory.CreateDirectory(Path.Combine(fixture.Path, "build"));
        File.WriteAllText(Path.Combine(fixture.Path, "build", "ignored.bin"), "ignored");
        Directory.CreateDirectory(Path.Combine(fixture.Path, "new-folder"));
        File.WriteAllText(Path.Combine(fixture.Path, "new-folder", "included.txt"), "included");
        Directory.CreateDirectory(Path.Combine(fixture.Path, "API", "bin"));
        Directory.CreateDirectory(Path.Combine(fixture.Path, "API", "obj"));
        File.WriteAllText(Path.Combine(fixture.Path, "API", "bin", "build.dll"), "ignored");
        File.WriteAllText(Path.Combine(fixture.Path, "API", "obj", "assets.json"), "ignored");
        File.WriteAllText(Path.Combine(fixture.Path, "API", "project.csproj.user"), "ignored");
        var overview = await new LibGitOverviewReader().ReadOverviewAsync(fixture.Path);
        Assert.DoesNotContain(overview.Changes, entry => entry.Path.StartsWith("API/", StringComparison.Ordinal));
        Assert.DoesNotContain(overview.Changes, entry => entry.Path.StartsWith("build/", StringComparison.Ordinal));
        Assert.Contains(overview.Changes, entry => entry.Path == "new-folder/included.txt");
        var files = await new GitFileBrowserService().ListAsync(fixture.Path);
        Assert.DoesNotContain(files, entry => entry.Path.StartsWith("build/", StringComparison.Ordinal));
        Assert.Contains(files, entry => entry.Path == "new-folder/included.txt");
    }

    [Fact]
    public async Task CenterContentDistinguishesIndexAndWorkingFilesAndRefusesTraversal()
    {
        using var fixture = new Fixture();
        var service = new GitFileBrowserService();
        File.AppendAllText(Path.Combine(fixture.Path, "file.txt"), "working-only\n");
        Assert.Equal(fixture.Original, await service.ReadContentAsync(fixture.Path, "file.txt", true));
        Assert.Contains("working-only", await service.ReadContentAsync(fixture.Path, "file.txt", false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReadContentAsync(fixture.Path, "../outside.txt", false)
        );
        var lines = GitPartialStageService.Lines("@@ -4,2 +4,2 @@\n-old\n+new\n context\n");
        Assert.Equal(4, lines.Single(line => line.Kind == "Deleted").OldNumber);
        Assert.Null(lines.Single(line => line.Kind == "Deleted").NewNumber);
        Assert.Equal(4, lines.Single(line => line.Kind == "Added").NewNumber);
        Assert.Equal(5, lines.Single(line => line.Kind == "Context").OldNumber);
        Assert.Equal(5, lines.Single(line => line.Kind == "Context").NewNumber);
    }

    [Fact]
    public async Task InitializeExistingProjectFolderPreservesFilesAndRefusesExistingRepository()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "existing-project");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "source.cs"), "public class Project {}\n");
        var service = new GitRepositoryLifecycleService();
        await service.InitializeAsync(target);
        Assert.Equal("public class Project {}\n", File.ReadAllText(Path.Combine(target, "source.cs")));
        using (var repo = new Repository(target))
        {
            Assert.Equal("main", repo.Head.FriendlyName);
            Assert.Equal(FileStatus.NewInWorkdir, repo.RetrieveStatus("source.cs"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync(target));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloneAsync(fixture.Path, target));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GitLlama-complete-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "repository");
        public string Original => "one\ntwo\nthree\n";

        public Fixture()
        {
            Directory.CreateDirectory(Path);
            Repository.Init(Path);
            using var repo = new Repository(Path);
            repo.Config.Set("core.autocrlf", false);
            File.WriteAllText(System.IO.Path.Combine(Path, "file.txt"), Original);
            Commands.Stage(repo, "file.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            repo.Commit("Initial", signature, signature);
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, true);
        }
    }
}
