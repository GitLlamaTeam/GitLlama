using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class DiscardAllTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gitllama-discard-all-" + Guid.NewGuid().ToString("N"));
    private readonly Signature signature = new("Test", "test@example.invalid", DateTimeOffset.UtcNow);

    public DiscardAllTests()
    {
        Repository.Init(root);
        using var repo = new Repository(root);
        repo.Config.Set("core.autocrlf", false);
    }

    private void Write(string path, string text) => File.WriteAllText(Path.Combine(root, path), text);

    [Fact]
    public async Task SelectedDiscardRestoresOnlyNamedFilesAndPreservesUnselectedIndexAndWorkingEdits()
    {
        string head;
        using (var repo = new Repository(root))
        {
            foreach (var path in new[] { "edit.txt", "deleted.txt", "old.txt", "keep.txt", "a[1].txt", "a1.txt" }) Write(path, "original\n");
            Write("old.txt", "unique rename content\n");
            Write(".gitignore", "ignored.tmp\n");
            Commands.Stage(repo, "*");
            head = repo.Commit("initial", signature, signature).Sha;
            Write("edit.txt", "staged edit\n");
            Write("added.txt", "staged addition\n");
            Write("keep.txt", "keep staged\n");
            Commands.Stage(repo, ["edit.txt", "added.txt", "keep.txt"]);
            Write("edit.txt", "working edit\n");
            Write("keep.txt", "keep working\n");
            Write("a[1].txt", "discard literal filename\n");
            Write("a1.txt", "keep similar filename\n");
            Commands.Move(repo, "old.txt", "renamed.txt");
            File.Delete(Path.Combine(root, "deleted.txt"));
            Write("new.txt", "untracked\n");
            Write("ignored.tmp", "keep ignored\n");
        }
        await new GitDiscardSelectedService().DiscardAsync(root,
            ["edit.txt", "deleted.txt", "renamed.txt", "added.txt", "new.txt", "a[1].txt", "ignored.tmp"], "DISCARD");
        using var check = new Repository(root);
        Assert.Equal(head, check.Head.Tip.Sha);
        Assert.Equal("unique rename content\n", File.ReadAllText(Path.Combine(root, "old.txt")));
        foreach (var path in new[] { "edit.txt", "deleted.txt", "a[1].txt" })
            Assert.Equal("original\n", File.ReadAllText(Path.Combine(root, path)));
        foreach (var path in new[] { "renamed.txt", "added.txt", "new.txt" }) Assert.False(File.Exists(Path.Combine(root, path)));
        Assert.Equal("keep working\n", File.ReadAllText(Path.Combine(root, "keep.txt")));
        Assert.Equal("keep staged\n", check.Lookup<Blob>(check.Index["keep.txt"].Id).GetContentText());
        Assert.Equal("keep similar filename\n", File.ReadAllText(Path.Combine(root, "a1.txt")));
        Assert.Equal("keep ignored\n", File.ReadAllText(Path.Combine(root, "ignored.tmp")));
        Assert.Equal(FileStatus.Unaltered, check.RetrieveStatus("edit.txt"));
    }

    [Fact]
    public async Task SelectedDiscardHandlesUnbornAndValidatesEveryPathBeforeDeletion()
    {
        Write("added.txt", "selected\n");
        Write("keep.txt", "not selected\n");
        using (var repo = new Repository(root)) Commands.Stage(repo, ["added.txt", "keep.txt"]);
        Repository.Init(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "nested", "file.txt"), "keep nested");
        var service = new GitDiscardSelectedService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscardAsync(root, ["added.txt"], "no"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscardAsync(root, ["added.txt", "nested/file.txt"], "DISCARD"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DiscardAsync(root, ["added.txt", "../outside.txt"], "DISCARD"));
        Assert.True(File.Exists(Path.Combine(root, "added.txt")));
        await service.DiscardAsync(root, ["added.txt"], "DISCARD");
        using var check = new Repository(root);
        Assert.Null(check.Head.Tip);
        Assert.False(File.Exists(Path.Combine(root, "added.txt")));
        Assert.Equal("keep.txt", Assert.Single(check.Index).Path);
        Assert.Equal("keep nested", File.ReadAllText(Path.Combine(root, "nested", "file.txt")));
    }

    [Fact]
    public async Task DiscardsStagedUnstagedAddedDeletedAndRenamedFilesWithoutMovingHead()
    {
        string head;
        using (var repo = new Repository(root))
        {
            Write("edited.txt", "original\n");
            Write("deleted.txt", "restore\n");
            Write("old.txt", "rename\n");
            Write(".gitignore", "ignored.tmp\n");
            Commands.Stage(repo, "*");
            head = repo.Commit("initial", signature, signature).Sha;
            Write("edited.txt", "staged\n");
            Write("added.txt", "staged addition\n");
            Commands.Move(repo, "old.txt", "renamed.txt");
            Commands.Stage(repo, ["edited.txt", "added.txt"]);
            Write("edited.txt", "unstaged\n");
            File.Delete(Path.Combine(root, "deleted.txt"));
            Write("new.txt", "untracked\n");
            Write("ignored.tmp", "keep\n");
        }

        await new GitDiscardAllService().DiscardAsync(root, "DISCARD");

        using var check = new Repository(root);
        Assert.Equal(head, check.Head.Tip.Sha);
        Assert.False(check.RetrieveStatus().IsDirty);
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(root, "edited.txt")));
        Assert.True(File.Exists(Path.Combine(root, "deleted.txt")));
        Assert.True(File.Exists(Path.Combine(root, "old.txt")));
        Assert.False(File.Exists(Path.Combine(root, "renamed.txt")));
        Assert.False(File.Exists(Path.Combine(root, "added.txt")));
        Assert.False(File.Exists(Path.Combine(root, "new.txt")));
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(root, "ignored.tmp")));
    }

    [Fact]
    public async Task UnbornRepositoryDiscardsChangesAndKeepsIgnoredAndNestedRepositories()
    {
        using (var repo = new Repository(root))
        {
            Write(".gitignore", "ignored.tmp\n");
            Write("added.txt", "new\n");
            Commands.Stage(repo, [".gitignore", "added.txt"]);
            Write("untracked.txt", "new\n");
            Write("ignored.tmp", "keep\n");
            Repository.Init(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "file.txt"), "keep nested");
        }

        await new GitDiscardAllService().DiscardAsync(root, "DISCARD");
        using var check = new Repository(root);
        Assert.Null(check.Head.Tip);
        Assert.Empty(check.Index);
        Assert.False(File.Exists(Path.Combine(root, "added.txt")));
        Assert.False(File.Exists(Path.Combine(root, "untracked.txt")));
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(root, "ignored.tmp")));
        Assert.True(Repository.IsValid(Path.Combine(root, "nested")));
        Assert.Equal("keep nested", File.ReadAllText(Path.Combine(root, "nested", "file.txt")));
    }

    [Fact]
    public async Task UnconfirmedOrConflictedDiscardLeavesAllFilesUntouched()
    {
        Write("notes.txt", "original\n");
        using (var repo = new Repository(root))
        {
            Commands.Stage(repo, "notes.txt");
            repo.Commit("initial", signature, signature);
            repo.CreateBranch("other");
            Write("notes.txt", "main\n");
            Commands.Stage(repo, "notes.txt");
            repo.Commit("main edit", signature, signature);
            var main = repo.Head.FriendlyName;
            Commands.Checkout(repo, "other");
            Write("notes.txt", "other\n");
            Commands.Stage(repo, "notes.txt");
            repo.Commit("other edit", signature, signature);
            Commands.Checkout(repo, main);
            repo.Merge(repo.Branches["other"], signature);
        }
        Write("untracked.txt", "keep\n");
        var before = File.ReadAllText(Path.Combine(root, "notes.txt"));
        var service = new GitDiscardAllService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscardAsync(root, "no"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscardAsync(root, "DISCARD"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitDiscardSelectedService().DiscardAsync(root, ["notes.txt"], "DISCARD"));
        Assert.Equal(before, File.ReadAllText(Path.Combine(root, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(root, "untracked.txt")));
        using var check = new Repository(root);
        Assert.NotEmpty(check.Index.Conflicts);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, true);
    }
}
