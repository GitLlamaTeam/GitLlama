using System.Text;
using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

// LFS tests share the process-wide filter and its git-lfs processes, so they run one class at a time.
[Collection("Git LFS")]
public sealed class GitLfsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitLlama-Lfs-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly byte[] _content = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 % 251)).ToArray();

    public GitLfsTests()
    {
        GitLfsFilter.EnsureRegistered();
        _path = Path.Combine(_root, "Game");
        Directory.CreateDirectory(_path);
        Repository.Init(_path);
        using var repo = new Repository(_path);
        repo.Config.Set("user.name", "Test");
        repo.Config.Set("user.email", "test@example.com");
    }

    [Fact]
    public async Task StatusRefreshDoesNotFeedBackFromLfsWritesIntoAnotherRefresh()
    {
        await new GitLfsService().TrackAsync(_path, "*.bin");
        File.WriteAllBytes(Path.Combine(_path, "big.bin"), _content);
        using (var repo = new Repository(_path))
        {
            Commands.Stage(repo, [".gitattributes", "big.bin"]);
            Commit(repo, "Add LFS content");
        }
        var cacheWrites = 0;
        var callbacks = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rawLfsWatcher = new FileSystemWatcher(Path.Combine(_path, ".git", "lfs")) { IncludeSubdirectories = true };
        rawLfsWatcher.Created += (_, _) => Interlocked.Increment(ref cacheWrites);
        rawLfsWatcher.Changed += (_, _) => Interlocked.Increment(ref cacheWrites);
        rawLfsWatcher.EnableRaisingEvents = true;
        using var watcher = new GitLlama.Infrastructure.RepositoryWatcher([_path, Path.Combine(_path, ".git")], () =>
        {
            Interlocked.Increment(ref callbacks);
            try
            {
                new LibGitOverviewReader().ReadOverviewAsync(_path).GetAwaiter().GetResult();
                completed.TrySetResult();
            }
            catch (Exception e) { completed.TrySetException(e); }
        });
        var edited = _content.ToArray();
        edited[0] ^= 0x5a;
        File.WriteAllBytes(Path.Combine(_path, "big.bin"), edited);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(1600);
        Assert.True(Volatile.Read(ref cacheWrites) > 0, "The status refresh must exercise real LFS cache writes.");
        Assert.Equal(1, Volatile.Read(ref callbacks));
    }

    [Fact]
    public void ParsesDownloadedAndPointerOnlyFiles()
    {
        var files = GitLfsService.ParseFiles("4d7a2146 * Art/Hero.psd\n9b2c11aa - Audio/Theme song.wav\n");
        Assert.Equal([new GitLfsFile("Art/Hero.psd", true), new GitLfsFile("Audio/Theme song.wav", false)], files);
    }

    [Theory]
    [InlineData("prune: 3 local objects, 1 retained, done.\n", 2)]
    [InlineData("3 local objects, 1 retained, done.\n", 2)]
    [InlineData("1 local object, 1 retained, done.\n", 0)]
    [InlineData("3 local objects, 3 retained, done.\n", 0)]
    [InlineData("3 local objects, 1 retained, done.\n2 files would be pruned (400 KB)\n", 2)]
    [InlineData("3 local objects, 1 retained, 1 not on remote, done.\n1 file would be pruned (200 KB)\n", 1)]
    [InlineData("1 local object, 0 retained\r3 local objects, 1 retained, done.\n", 2)]
    [InlineData("", 0)]
    public void ParsesPruneSummariesAcrossLfsVersions(string output, int expected) =>
        Assert.Equal(expected, GitLfsService.ParsePruneCount(output));

    [Fact]
    public async Task StagingATrackedFileCommitsAPointerAndCheckoutRestoresIt()
    {
        var lfs = new GitLfsService();
        await lfs.TrackAsync(_path, "*.bin");
        File.WriteAllBytes(Path.Combine(_path, "big.bin"), _content);
        using (var repo = new Repository(_path))
        {
            Commands.Stage(repo, [".gitattributes", "big.bin"]);
            Commit(repo, "Add big file");
            var blob = (Blob)repo.Head.Tip["big.bin"].Target;
            Assert.StartsWith("version https://git-lfs.github.com/spec/v1", blob.GetContentText());
            Assert.True(blob.Size < 200);
            Assert.False(repo.RetrieveStatus().IsDirty);
        }

        File.Delete(Path.Combine(_path, "big.bin"));
        using (var repo = new Repository(_path))
        {
            repo.CheckoutPaths(
                "HEAD",
                ["big.bin"],
                new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force }
            );
        }

        Assert.Equal(_content, File.ReadAllBytes(Path.Combine(_path, "big.bin")));
        var status = await lfs.GetStatusAsync(_path);
        Assert.True(status.UsesLfs);
        Assert.Equal(["*.bin"], status.Patterns);
        Assert.Equal([new GitLfsFile("big.bin", true)], status.Files);
    }

    [Fact]
    public async Task CheckoutKeepsThePointerWhenContentCantBeDownloaded()
    {
        await new GitLfsService().TrackAsync(_path, "*.bin");
        File.WriteAllBytes(Path.Combine(_path, "big.bin"), _content);
        using (var repo = new Repository(_path))
        {
            Commands.Stage(repo, [".gitattributes", "big.bin"]);
            Commit(repo, "Add big file");
        }

        // Lose the local object, with no remote to download it from.
        Directory.Delete(Path.Combine(_path, ".git", "lfs"), true);
        File.Delete(Path.Combine(_path, "big.bin"));
        GitLfsFilter.TakeSkippedDownloads();
        using (var repo = new Repository(_path))
        {
            repo.CheckoutPaths(
                "HEAD",
                ["big.bin"],
                new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force }
            );
        }

        Assert.StartsWith(
            "version https://git-lfs.github.com/spec/v1",
            Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_path, "big.bin")))
        );
        Assert.Equal(1, GitLfsFilter.TakeSkippedDownloads());
        var status = await new GitLfsService().GetStatusAsync(_path);
        Assert.Equal(1, status.MissingCount);
    }

    [Fact]
    public async Task ParallelOperationsFilterEachFileCorrectlyAndLeaveNoProcesses()
    {
        await new GitLfsService().TrackAsync(_path, "*.bin");
        var contents = Enumerable
            .Range(0, 6)
            .Select(n => Enumerable.Range(0, 50_000 + n * 1000).Select(i => (byte)((i + n) % 251)).ToArray())
            .ToArray();
        for (var n = 0; n < contents.Length; n++)
        {
            File.WriteAllBytes(Path.Combine(_path, $"file{n}.bin"), contents[n]);
        }

        using (var repo = new Repository(_path))
        {
            Commands.Stage(repo, "*");
            Commit(repo, "Add files");
        }

        // Make every file look changed so status and diffs run the clean filter on many threads at once,
        // as background refreshes and previews do.
        foreach (var file in Directory.EnumerateFiles(_path, "*.bin"))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        }

        await Task.WhenAll(
            Enumerable
                .Range(0, contents.Length)
                .Select(n =>
                    Task.Run(() =>
                    {
                        using var repo = new Repository(_path);
                        Assert.Equal(FileStatus.Unaltered, repo.RetrieveStatus($"file{n}.bin"));
                        Assert.Empty(repo.Diff.Compare<Patch>([$"file{n}.bin"], true));
                    })
                )
        );

        // Read every file's checked-out content on its own thread, running the smudge filter in parallel.
        await Task.WhenAll(
            Enumerable
                .Range(0, contents.Length)
                .Select(n =>
                    Task.Run(() =>
                    {
                        using var repo = new Repository(_path);
                        var blob = (Blob)repo.Head.Tip[$"file{n}.bin"].Target;
                        using var stream = blob.GetContentStream(new FilteringOptions($"file{n}.bin"));
                        using var copy = new MemoryStream();
                        stream.CopyTo(copy);
                        Assert.Equal(contents[n], copy.ToArray());
                    })
                )
        );

        // And check them all out again.
        foreach (var file in Directory.EnumerateFiles(_path, "*.bin"))
        {
            File.Delete(file);
        }

        using (var repo = new Repository(_path))
        {
            repo.CheckoutPaths("HEAD", ["*.bin"], new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        }

        for (var n = 0; n < contents.Length; n++)
        {
            Assert.Equal(contents[n], File.ReadAllBytes(Path.Combine(_path, $"file{n}.bin")));
        }

        // Every file went through one long-running git-lfs process for this repository.
        Assert.Equal(1, GitLfsFilter.RunningProcesses);
    }

    [Theory]
    [InlineData("", "*.psd", "Art/Hero.psd", true)]
    [InlineData("", "*.psd", "Hero.psd", true)]
    [InlineData("", "*.psd", "Hero.psd.txt", false)]
    [InlineData("", "Art/*.psd", "Art/Hero.psd", true)]
    [InlineData("", "Art/*.psd", "Art/Old/Hero.psd", false)]
    [InlineData("", "Art/*.psd", "Hero.psd", false)]
    [InlineData("", "/Hero.psd", "Hero.psd", true)]
    [InlineData("", "/Hero.psd", "Art/Hero.psd", false)]
    [InlineData("", "Assets/**/*.uasset", "Assets/Maps/Level1/Level.uasset", true)]
    [InlineData("", "Assets/**/*.uasset", "Assets/Level.uasset", true)]
    [InlineData("", "Assets/**/*.uasset", "Other/Level.uasset", false)]
    [InlineData("", "[Bb]ig?.bin", "Data/big1.bin", true)]
    [InlineData("Audio/", "*.wav", "Audio/Music/Theme.wav", true)]
    [InlineData("Audio/", "*.wav", "Theme.wav", false)]
    public void RulesMatchPathsLikeGitAttributes(string folder, string pattern, string path, bool expected) =>
        Assert.Equal(expected, new GitLfsRule(folder, pattern).Matches(path));

    [Fact]
    public async Task StatusKnowsWhichPathsAreStoredInLfs()
    {
        await new GitLfsService().TrackAsync(_path, "Art/*.psd");
        Directory.CreateDirectory(Path.Combine(_path, "Audio"));
        File.WriteAllText(
            Path.Combine(_path, "Audio", ".gitattributes"),
            "*.wav filter=lfs diff=lfs merge=lfs -text\n"
        );
        using (var repo = new Repository(_path))
        {
            Commands.Stage(repo, [".gitattributes", "Audio/.gitattributes"]);
        }

        var status = await new GitLfsService().GetStatusAsync(_path);
        Assert.True(status.IsLfsPath("Art/Hero.psd"));
        Assert.True(status.IsLfsPath("Audio/Theme.wav"));
        Assert.False(status.IsLfsPath("Theme.wav"));
        Assert.False(status.IsLfsPath("README.md"));
    }

    [Fact]
    public async Task PrunePreviewsAndRemovesOnlyPushedOldContent()
    {
        var remotePath = Path.Combine(_root, "remote.git");
        Repository.Init(remotePath, isBare: true);
        var lfs = new GitLfsService();
        await lfs.TrackAsync(_path, "*.bin");
        var versions = Enumerable.Range(1, 3).Select(v => _content.Select(b => (byte)(b ^ v)).ToArray()).ToArray();
        using (var repo = new Repository(_path))
        {
            repo.Network.Remotes.Add("origin", new Uri(remotePath).AbsoluteUri);
            // Keep the fixture outside the retention cutoff even when the test finishes in one second.
            repo.Config.Set("lfs.fetchrecentcommitsdays", 0);
            repo.Config.Set("lfs.fetchrecentrefsdays", 0);
            repo.Config.Set("lfs.pruneoffsetdays", 0);
            foreach (var version in versions)
            {
                File.WriteAllBytes(Path.Combine(_path, "big.bin"), version);
                Commands.Stage(repo, [".gitattributes", "big.bin"]);
                var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow.AddDays(-30));
                repo.Commit("Version", signature, signature);
            }
        }

        var unpushed = await lfs.PreviewPruneAsync(_path);
        Assert.Equal(0, unpushed.Objects);

        await new GitRemoteService().PushCurrentBranchAsync(_path, "origin");
        await lfs.PushAllAsync(_path, "origin", new Uri(remotePath).AbsoluteUri, new GitNetworkOptions());
        var preview = await lfs.PreviewPruneAsync(_path);
        Assert.Equal(2, preview.Objects);
        Assert.Equal(3L * _content.Length, preview.StorageBytes);

        Assert.Equal(2L * _content.Length, await lfs.PruneAsync(_path));
        Assert.Single(
            Directory.EnumerateFiles(Path.Combine(_path, ".git", "lfs", "objects"), "*", SearchOption.AllDirectories)
        );
        Assert.Equal(versions[^1], File.ReadAllBytes(Path.Combine(_path, "big.bin")));
    }

    [Fact]
    public async Task PushAllUploadsContentForEveryBranch()
    {
        var remotePath = Path.Combine(_root, "remote.git");
        Repository.Init(remotePath, isBare: true);
        await new GitLfsService().TrackAsync(_path, "*.bin");
        using (var repo = new Repository(_path))
        {
            repo.Network.Remotes.Add("origin", new Uri(remotePath).AbsoluteUri);
            File.WriteAllBytes(Path.Combine(_path, "main.bin"), _content);
            Commands.Stage(repo, [".gitattributes", "main.bin"]);
            Commit(repo, "Main art");
            var main = repo.Head;
            Commands.Checkout(repo, repo.CreateBranch("feature"));
            File.WriteAllBytes(Path.Combine(_path, "feature.bin"), Enumerable.Reverse(_content).ToArray());
            Commands.Stage(repo, "feature.bin");
            Commit(repo, "Feature art");
            Commands.Checkout(repo, main);
        }

        await new GitLfsService().PushAllAsync(
            _path,
            "origin",
            new Uri(remotePath).AbsoluteUri,
            new GitNetworkOptions()
        );

        var stored = Directory
            .EnumerateFiles(Path.Combine(remotePath, "lfs", "objects"), "*", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes)
            .ToArray();
        Assert.Contains(stored, bytes => bytes.SequenceEqual(_content));
        Assert.Contains(stored, bytes => bytes.SequenceEqual(Enumerable.Reverse(_content)));
    }

    [Fact]
    public async Task UntrackingRemovesThePattern()
    {
        var lfs = new GitLfsService();
        await lfs.TrackAsync(_path, "*.psd");
        await lfs.TrackAsync(_path, "*.wav");
        await lfs.UntrackAsync(_path, "*.psd");

        Assert.Equal(["*.wav"], (await lfs.GetStatusAsync(_path)).Patterns);
    }

    [Fact]
    public async Task PushUploadsLfsContentToTheRemote()
    {
        var remotePath = Path.Combine(_root, "remote.git");
        Repository.Init(remotePath, isBare: true);
        await new GitLfsService().TrackAsync(_path, "*.bin");
        File.WriteAllBytes(Path.Combine(_path, "big.bin"), _content);
        using (var repo = new Repository(_path))
        {
            Commands.Stage(repo, [".gitattributes", "big.bin"]);
            Commit(repo, "Add big file");
            repo.Network.Remotes.Add("origin", new Uri(remotePath).AbsoluteUri);
        }

        await new GitRemoteService().PushCurrentBranchAsync(_path, "origin");

        var stored = Directory.EnumerateFiles(
            Path.Combine(remotePath, "lfs", "objects"),
            "*",
            SearchOption.AllDirectories
        );
        Assert.Contains(stored, file => File.ReadAllBytes(file).SequenceEqual(_content));
    }

    public void Dispose()
    {
        // Running git-lfs processes keep the repository folder in use.
        GitLfsFilter.Shutdown();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, true);
    }

    private static void Commit(Repository repo, string message)
    {
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        repo.Commit(message, signature, signature);
    }
}
