using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>Restores only explicitly selected files, using literal paths and checkout filters.</summary>
public sealed class GitDiscardSelectedService
{
    public Task DiscardAsync(string repositoryPath, IReadOnlyList<string> selectedPaths, string confirmation,
        CancellationToken ct = default) => Task.Run(() =>
    {
        if (confirmation != "DISCARD") throw new InvalidOperationException("Confirm discarding the selected changes first.");
        if (selectedPaths.Count == 0) throw new ArgumentException("Select files to discard.");
        using var repo = new Repository(repositoryPath);
        if (repo.Info.IsBare || repo.Index.Conflicts.Any() || repo.Info.CurrentOperation != CurrentOperation.None)
            throw new InvalidOperationException("Finish or abort the current Git operation before discarding changes.");
        var paths = new HashSet<string>(selectedPaths.Select(path => path.Replace('\\', '/')), StringComparer.Ordinal);
        var status = repo.RetrieveStatus(new StatusOptions
        {
            DetectRenamesInIndex = true, DetectRenamesInWorkDir = true, RecurseUntrackedDirs = true,
        });
        foreach (var entry in status)
        {
            var oldIndex = entry.HeadToIndexRenameDetails?.OldFilePath;
            var oldWorkdir = entry.IndexToWorkDirRenameDetails?.OldFilePath;
            var newIndex = entry.HeadToIndexRenameDetails?.NewFilePath;
            var newWorkdir = entry.IndexToWorkDirRenameDetails?.NewFilePath;
            var related = new[] { entry.FilePath, oldIndex, oldWorkdir, newIndex, newWorkdir };
            if (related.Any(path => path is not null && paths.Contains(path)))
            {
                foreach (var path in related) if (path is not null) paths.Add(path);
            }
        }
        var plan = new List<(string Path, string Absolute, Blob? Blob, Mode Mode)>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (path.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Repository metadata cannot be discarded.");
            var absolute = GitPathGuard.RegularWorkingFile(repo, path);
            var directory = Path.GetDirectoryName(absolute);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Info.WorkingDirectory));
            while (directory is not null && !string.Equals(directory, root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                if (Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git")))
                    throw new InvalidOperationException($"Nested repositories are preserved: {path}");
                directory = Path.GetDirectoryName(directory);
            }
            var head = repo.Head.Tip?.Tree[path];
            var index = repo.Index[path];
            if (Directory.Exists(absolute) || head?.Target is Tree || head?.Mode is Mode.GitLink or Mode.SymbolicLink
                || index?.Mode is Mode.GitLink or Mode.SymbolicLink)
                throw new InvalidOperationException($"Discard files individually; nested repositories, submodules, directories and links are preserved: {path}");
            if (head is null && index is null && repo.Ignore.IsPathIgnored(path)) continue;
            plan.Add((path, absolute, head?.Target as Blob, head?.Mode ?? Mode.NonExecutableFile));
        }
        // Validate every target before touching any file. No glob expansion or recursive deletion is used.
        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(item.Absolute))
                File.SetAttributes(item.Absolute, File.GetAttributes(item.Absolute) & ~FileAttributes.ReadOnly);
            if (item.Blob is { } blob)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Absolute)!);
                using (var source = blob.GetContentStream(new FilteringOptions(item.Path)))
                using (var destination = File.Create(item.Absolute)) source.CopyTo(destination);
                repo.Index.Add(blob, item.Path, item.Mode);
                if (!OperatingSystem.IsWindows())
                {
                    var mode = File.GetUnixFileMode(item.Absolute);
                    var execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                    File.SetUnixFileMode(item.Absolute, item.Mode == Mode.ExecutableFile ? mode | execute : mode & ~execute);
                }
            }
            else
            {
                if (repo.Index[item.Path] is not null) repo.Index.Remove(item.Path);
                if (File.Exists(item.Absolute)) File.Delete(item.Absolute);
            }
        }
        repo.Index.Write();
    }, ct);
}
