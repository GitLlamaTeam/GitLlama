namespace GitLlama.Infrastructure;

/// <summary>Debounced local file notifications. No Git work is performed on the watcher thread.</summary>
public sealed class RepositoryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;
    private readonly string[] _gitDirectories;

    public RepositoryWatcher(IEnumerable<string> roots, Action changed, IEnumerable<string>? gitDirectories = null)
    {
        var watchedRoots = roots.Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator).Distinct(PathComparer).ToArray();
        _gitDirectories = (gitDirectories ?? watchedRoots.Where(root =>
            Path.GetFileName(root).Equals(".git", StringComparison.OrdinalIgnoreCase)
            || (File.Exists(Path.Combine(root, "HEAD")) && (Directory.Exists(Path.Combine(root, "objects")) || File.Exists(Path.Combine(root, "commondir"))))))
            .Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator).ToArray();
        _timer = new Timer(
            _ =>
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }

                changed();
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite
        );
        foreach (
            var root in watchedRoots
        )
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter =
                    NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 32 * 1024,
            };
            watcher.Changed += (_, e) => FileChanged(e);
            watcher.Created += (_, e) => FileChanged(e);
            watcher.Deleted += (_, e) => FileChanged(e);
            watcher.Renamed += (_, e) =>
            {
                if (IsRelevant(e.FullPath, e.ChangeType) || IsRelevant(e.OldFullPath, e.ChangeType)) Schedule();
            };
            watcher.Error += (_, _) => Schedule();
            _watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private void FileChanged(FileSystemEventArgs args)
    {
        if (IsRelevant(args.FullPath, args.ChangeType)) Schedule();
    }

    private bool IsRelevant(string path, WatcherChangeTypes change)
    {
        foreach (var directory in _gitDirectories)
        {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (relative == ".") return change != WatcherChangeTypes.Changed;
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal))
                return RelevantGitMetadata(relative) && !MetadataDirectoryWrite(path, change);
        }
        // The worktree watcher also sees .git, including nested repositories' metadata.
        var normalized = path.Replace('\\', '/');
        var marker = normalized.IndexOf("/.git/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (marker >= 0) return RelevantGitMetadata(normalized[(marker + 6)..]) && !MetadataDirectoryWrite(path, change);
        if (Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase))
            return change != WatcherChangeTypes.Changed || File.Exists(path);
        return true;
    }

    private static bool MetadataDirectoryWrite(string path, WatcherChangeTypes change) =>
        change == WatcherChangeTypes.Changed && Directory.Exists(path);

    private static bool RelevantGitMetadata(string path)
    {
        // Status/filter reads can write LFS temp objects and logs. Observing them feeds a refresh back into itself.
        if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = path.Split('/');
        if (parts[0] is "refs" or "rebase-merge" or "rebase-apply" or "sequencer") return true;
        // On Linux a file created immediately inside a new directory can predate the recursive watch.
        // Reconcile on relevant directory creation too; changes to existing cache directories stay ignored.
        if (parts[0] == "info") return path is "info" or "info/attributes" or "info/exclude" or "info/sparse-checkout";
        if (parts[0] is "modules" or "worktrees" && parts.Length <= 2) return true;
        if (parts[0] == "worktrees" && parts.Length > 2) return RelevantGitMetadata(string.Join('/', parts.Skip(2)));
        if (parts[0] == "modules" && parts.Length > 2)
        {
            // Module names can themselves contain folders. Observe their state files and refs, not LFS/object caches.
            for (var index = 2; index < parts.Length; index++)
                if (RelevantGitMetadata(string.Join('/', parts.Skip(index)))) return true;
            return false;
        }
        return parts.Length == 1 && parts[0] is "HEAD" or "index" or "config" or "config.worktree" or "packed-refs"
            or "FETCH_HEAD" or "ORIG_HEAD" or "MERGE_HEAD" or "MERGE_MSG" or "MERGE_MODE" or "CHERRY_PICK_HEAD"
            or "REVERT_HEAD" or "REBASE_HEAD" or "AUTO_MERGE" or "BISECT_LOG" or "BISECT_NAMES" or "BISECT_START"
            or "shallow" or "commondir" or "gitdir";
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _timer.Change(500, Timeout.Infinite);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }
}
