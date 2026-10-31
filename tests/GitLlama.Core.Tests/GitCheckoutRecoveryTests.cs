using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class GitCheckoutRecoveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "gitllama-checkout-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly string _original;

    public GitCheckoutRecoveryTests()
    {
        Repository.Init(_path);
        using var repo = new Repository(_path);
        Write("a-delete.txt", "keep this deleted file");
        Write("local.txt", "committed");
        Write(".gitignore", "*.ignored\n");
        Write("folder/old.txt", "old directory content");
        Write("node", "old regular file");
        File.WriteAllBytes(Path.Combine(_path, "z-update.bin"), Enumerable.Repeat((byte)37, 4096).ToArray());
        Commit(repo, "original");
        _original = repo.Head.FriendlyName;
        Commands.Checkout(repo, repo.CreateBranch("target"));
        File.Delete(Path.Combine(_path, "a-delete.txt"));
        File.Delete(Path.Combine(_path, "folder", "old.txt"));
        Directory.Delete(Path.Combine(_path, "folder"));
        Write("folder", "new regular file");
        File.Delete(Path.Combine(_path, "node"));
        Write("node/child.txt", "new directory content");
        Write("new-dir/added.txt", "new tracked file");
        File.WriteAllBytes(Path.Combine(_path, "z-update.bin"), Enumerable.Repeat((byte)92, 8192).ToArray());
        Commit(repo, "target");
        Commands.Checkout(repo, repo.Branches[_original]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedFileBlocksBeforeAnyChanges(bool denyReads)
    {
        if (!OperatingSystem.IsWindows()) return; // Native Unix checkout doesn't honor Windows sharing locks.
        AddLocalWork();
        var before = Files();
        var index = File.ReadAllBytes(Path.Combine(_path, ".git", "index"));
        using var locked = new FileStream(Path.Combine(_path, "z-update.bin"), FileMode.Open,
            FileAccess.Read, denyReads ? FileShare.None : FileShare.ReadWrite);
        var error = await Assert.ThrowsAsync<IOException>(() => new GitBranchService().CheckoutAsync(_path, "target"));
        Assert.Contains("No working files were changed", error.Message);
        // Release the lock before comparing bytes.
        locked.Dispose();
        Assert.Equal(before, Files());
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(_path, ".git", "index")));
        using var repo = new Repository(_path);
        Assert.Equal(_original, repo.Head.FriendlyName);
        AssertNoBackups();
    }

    [Fact]
    public void FailureAfterFilesChangedRestoresExactWorkingTreeIndexHeadAndReflog()
    {
        AddLocalWork();
        var before = Files();
        var index = File.ReadAllBytes(Path.Combine(_path, ".git", "index"));
        var head = File.ReadAllBytes(Path.Combine(_path, ".git", "HEAD"));
        var log = File.ReadAllBytes(Path.Combine(_path, ".git", "logs", "HEAD"));
        using var repo = new Repository(_path);
        var changed = false;
        var error = Assert.Throws<IOException>(() => GitCheckoutTransaction.Checkout(repo, repo.Branches["target"], default,
            (_, completed, _) =>
            {
                if (completed > 0 && !File.Exists(Path.Combine(_path, "a-delete.txt")))
                {
                    changed = true;
                    throw new IOException("Simulated checkout callback failure");
                }
            }));
        Assert.True(changed, "The checkout must actually delete a file before the injected failure.");
        Assert.True(error.Message.Contains("were restored"), error.ToString());
        Assert.Equal(before, Files());
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(_path, ".git", "index")));
        Assert.Equal(head, File.ReadAllBytes(Path.Combine(_path, ".git", "HEAD")));
        Assert.Equal(log, File.ReadAllBytes(Path.Combine(_path, ".git", "logs", "HEAD")));
        using var reopened = new Repository(_path);
        Assert.Equal(_original, reopened.Head.FriendlyName);
        AssertNoBackups();
    }

    [Fact]
    public void FileLockedDuringCheckoutRestoresEarlierDeletions()
    {
        if (!OperatingSystem.IsWindows()) return;
        var before = Files();
        var index = File.ReadAllBytes(Path.Combine(_path, ".git", "index"));
        using var repo = new Repository(_path);
        FileStream? locked = null;
        try
        {
            var error = Assert.Throws<IOException>(() => GitCheckoutTransaction.Checkout(repo, repo.Branches["target"], default,
                (_, completed, _) =>
                {
                    if (completed > 0 && !File.Exists(Path.Combine(_path, "a-delete.txt")) && locked is null)
                    {
                        locked = new FileStream(Path.Combine(_path, "z-update.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                }));
            Assert.NotNull(locked);
            Assert.Contains("were restored", error.Message);
        }
        finally { locked?.Dispose(); }

        Assert.Equal(before, Files());
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(_path, ".git", "index")));
        using var reopened = new Repository(_path);
        Assert.Equal(_original, reopened.Head.FriendlyName);
        AssertNoBackups();
    }

    [Fact]
    public void IncompleteRestorationKeepsRecoveryBytesAndReportsTheirLocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var repo = new Repository(_path);
        FileStream? locked = null;
        try
        {
            var error = Assert.Throws<IOException>(() => GitCheckoutTransaction.Checkout(repo, repo.Branches["target"], default,
                (_, completed, _) =>
                {
                    if (completed > 0 && !File.Exists(Path.Combine(_path, "a-delete.txt")) && locked is null)
                    {
                        // Simulate another app creating and locking a path after checkout removed it.
                        Write("a-delete.txt", "another app has this open");
                        locked = new FileStream(Path.Combine(_path, "a-delete.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
                        throw new IOException("Injected failure after another application locks a changed path");
                    }
                }));
            Assert.NotNull(locked);
            Assert.Contains("restoration is incomplete", error.Message);
            var backup = Assert.Single(Directory.GetDirectories(Path.Combine(_path, ".git", "gitllama-checkout-recovery")));
            Assert.Contains(backup, error.Message);
            Assert.Equal("keep this deleted file", File.ReadAllText(Path.Combine(backup, "worktree", "a-delete.txt")));
            Assert.True(File.Exists(Path.Combine(backup, "recovery.json")));
            Assert.Equal("old directory content", File.ReadAllText(Path.Combine(_path, "folder", "old.txt")));
            using var reopened = new Repository(_path);
            Assert.Equal(_original, reopened.Head.FriendlyName);
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public async Task FailedRemoteCheckoutRemovesOnlyItsNewLocalBranch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var repo = new Repository(_path))
        {
            repo.Network.Remotes.Add("origin", "https://example.invalid/repository.git");
            repo.Refs.Add("refs/remotes/origin/target", repo.Branches["target"].Tip.Id);
        }
        var before = Files();
        using var locked = new FileStream(Path.Combine(_path, "z-update.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await Assert.ThrowsAsync<IOException>(() => new GitBranchService().CheckoutRemoteBranchAsync(_path, "origin", "target", "from-remote"));
        locked.Dispose();
        Assert.Equal(before, Files());
        using var reopened = new Repository(_path);
        Assert.Equal(_original, reopened.Head.FriendlyName);
        Assert.Null(reopened.Branches["from-remote"]);
        Assert.NotNull(reopened.Branches["target"]);
        Assert.NotNull(reopened.Branches["origin/target"]);
        AssertNoBackups();
    }

    [Fact]
    public async Task FailedCreateAndCheckoutRemovesItsNewBranch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var locked = new FileStream(Path.Combine(_path, ".git", "HEAD"), FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(() => new GitBranchService().CreateAsync(_path, "new-local", checkout: true));
        locked.Dispose();
        using var repo = new Repository(_path);
        Assert.Equal(_original, repo.Head.FriendlyName);
        Assert.Null(repo.Branches["new-local"]);
        AssertNoBackups();
    }

    [Fact]
    public void CancellationAfterCheckoutBeginsRestoresOriginalState()
    {
        var before = Files();
        using var repo = new Repository(_path);
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<IOException>(() => GitCheckoutTransaction.Checkout(repo, repo.Branches["target"], cancellation.Token,
            (_, completed, _) => { if (completed > 0) cancellation.Cancel(); }));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(before, Files());
        using var reopened = new Repository(_path);
        Assert.Equal(_original, reopened.Head.FriendlyName);
    }

    [Fact]
    public void RollbackRestoresBrokenSymbolicLinksAndExecutablePermissions()
    {
        if (OperatingSystem.IsWindows()) return; // Creating symbolic links can require elevated permissions on Windows.
        using var repo = new Repository(_path);
        File.CreateSymbolicLink(Path.Combine(_path, "broken-link"), "missing-target");
        var executable = Path.Combine(_path, "z-update.bin");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(executable, mode);
        Commit(repo, "Add symbolic link and executable mode");
        Assert.Throws<IOException>(() => GitCheckoutTransaction.Checkout(repo, repo.Branches["target"], default,
            (_, completed, _) => { if (completed > 0) throw new IOException("Injected failure"); }));
        Assert.Equal("missing-target", new FileInfo(Path.Combine(_path, "broken-link")).LinkTarget);
        Assert.Equal(mode, File.GetUnixFileMode(executable));
        AssertNoBackups();
    }

    [Fact]
    public async Task SuccessfulCheckoutPreservesUnrelatedStagedUnstagedIgnoredAndUntrackedFiles()
    {
        AddLocalWork();
        Assert.Equal("target", await new GitBranchService().CheckoutAsync(_path, "target"));
        Assert.Equal("unstaged", File.ReadAllText(Path.Combine(_path, "local.txt")));
        Assert.Equal("untracked", File.ReadAllText(Path.Combine(_path, "untracked.txt")));
        Assert.Equal("ignored", File.ReadAllText(Path.Combine(_path, "local.ignored")));
        Assert.False(File.Exists(Path.Combine(_path, "a-delete.txt")));
        using var repo = new Repository(_path);
        Assert.Equal("staged", repo.Lookup<Blob>(repo.Index["local.txt"].Id).GetContentText());
        AssertNoBackups();
    }

    private void AddLocalWork()
    {
        using var repo = new Repository(_path);
        Write("local.txt", "staged");
        Commands.Stage(repo, "local.txt");
        Write("local.txt", "unstaged");
        Write("untracked.txt", "untracked");
        Write("local.ignored", "ignored");
    }

    private string[] Files() => Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(_path, path).StartsWith(".git" + Path.DirectorySeparatorChar))
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(_path, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();

    private void AssertNoBackups()
    {
        var parent = Path.Combine(_path, ".git", "gitllama-checkout-recovery");
        Assert.True(!Directory.Exists(parent) || !Directory.EnumerateFileSystemEntries(parent).Any());
    }

    private void Write(string path, string text)
    {
        var absolute = Path.Combine(_path, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, text);
    }

    private static void Commit(Repository repo, string message)
    {
        Commands.Stage(repo, "*");
        var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.UtcNow);
        repo.Commit(message, signature, signature);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) File.Delete(file);
            else File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_path, true);
    }
}
