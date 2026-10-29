using GitLlama.Infrastructure;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class RepositoryWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitLlama-watcher-" + Guid.NewGuid().ToString("N"));
    private string Git => Path.Combine(_root, ".git");

    public RepositoryWatcherTests()
    {
        Directory.CreateDirectory(Path.Combine(Git, "objects"));
        File.WriteAllText(Path.Combine(Git, "HEAD"), "ref: refs/heads/main\n");
    }

    [Fact]
    public async Task LfsCachesObjectWritesLogsAndLocksDoNotTriggerRefresh()
    {
        var calls = 0;
        using var watcher = new RepositoryWatcher([_root, Git], () => Interlocked.Increment(ref calls));
        foreach (var path in new[] { "lfs/tmp/clean.tmp", "lfs/objects/aa/bb/object", "lfs/logs/log.txt", "objects/pack/tmp_pack", "logs/HEAD", "index.lock", "refs/heads/main.lock", "modules/plugin/lfs/tmp/clean.tmp", "modules/plugin/objects/aa/bb" })
        {
            var absolute = Path.Combine(Git, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, "internal write");
            File.AppendAllText(absolute, "another write");
        }
        await Task.Delay(1300);
        Assert.Equal(0, Volatile.Read(ref calls));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sourceWatcher = new RepositoryWatcher([_root, Git], () => changed.TrySetResult());
        Directory.CreateDirectory(Path.Combine(_root, "lfs"));
        File.WriteAllText(Path.Combine(_root, "lfs", "real-source.txt"), "a real worktree edit");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("index")]
    [InlineData("refs/heads/main")]
    [InlineData("packed-refs")]
    [InlineData("config")]
    [InlineData("info/attributes")]
    [InlineData("MERGE_HEAD")]
    [InlineData("rebase-merge/done")]
    [InlineData("modules/plugin/HEAD")]
    [InlineData("modules/group/plugin/refs/heads/main")]
    [InlineData("worktrees/another/HEAD")]
    public async Task ImportantGitMetadataStillTriggersRefresh(string path)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new RepositoryWatcher([_root, Git], () => changed.TrySetResult());
        var absolute = Path.Combine(Git, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "updated metadata");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task IndexLockRenameIsObservedAndExternalGitDirectoryCachesAreIgnored()
    {
        var metadata = Path.Combine(_root, "external-metadata");
        Directory.CreateDirectory(metadata);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new RepositoryWatcher([metadata], () => changed.TrySetResult(), gitDirectories: [metadata]);
        Directory.CreateDirectory(Path.Combine(metadata, "lfs", "tmp"));
        File.WriteAllText(Path.Combine(metadata, "lfs", "tmp", "object"), "internal");
        File.WriteAllText(Path.Combine(metadata, "index.lock"), "new index");
        await Task.Delay(1000);
        Assert.False(changed.Task.IsCompleted);
        File.Move(Path.Combine(metadata, "index.lock"), Path.Combine(metadata, "index"));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
