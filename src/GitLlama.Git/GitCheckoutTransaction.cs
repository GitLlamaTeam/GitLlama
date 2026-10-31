using System.Runtime.InteropServices;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.Win32.SafeHandles;

namespace GitLlama.Git;

/// <summary>Undo a failed branch checkout from exact working-file and index backups, never a hard reset.</summary>
internal sealed class GitCheckoutTransaction : IDisposable
{
    private readonly Repository _repo;
    private readonly string _root;
    private readonly string _backup;
    private readonly Dictionary<string, SavedPath> _paths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly List<SavedPath> _metadata = [];
    private readonly HashSet<string> _captured = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _retain;

    private sealed record SavedPath(string Path, string? Copy, bool Exists, bool Directory,
        string? Link, FileAttributes Attributes, DateTime Modified, UnixFileMode? Permissions = null);

    private GitCheckoutTransaction(Repository repo)
    {
        _repo = repo;
        _root = repo.Info.WorkingDirectory;
        _backup = System.IO.Path.Combine(repo.Info.Path, "gitllama-checkout-recovery", Guid.NewGuid().ToString("N"));
    }

    internal static void Checkout(Repository repo, Branch branch, CancellationToken ct,
        Action<string, int, int>? progress = null)
    {
        using var transaction = new GitCheckoutTransaction(repo);
        transaction.Run(branch, ct, progress);
    }

    private void Run(Branch branch, CancellationToken ct, Action<string, int, int>? progress)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(System.IO.Path.Combine(_repo.Info.Path, "index.lock")))
        {
            throw new InvalidOperationException("Cannot switch branches while the Git index is locked by another operation.");
        }

        foreach (var name in new[] { "index", "HEAD", "logs/HEAD" })
        {
            var path = System.IO.Path.Combine(_repo.Info.Path, name);
            _metadata.Add(Save(path, System.IO.Path.Combine(_backup, "metadata", name)));
        }

        Exception? preparationError = null;
        Exception? progressError = null;
        try
        {
            // libgit2's Updated notifications omit some removals. Include every old/new tree path
            // before invoking checkout so an early deletion always has an original byte-for-byte copy.
            using var changes = _repo.Diff.Compare<TreeChanges>(_repo.Head.Tip?.Tree, branch.Tip.Tree);
            foreach (var change in changes)
            {
                foreach (var path in new[] { change.OldPath, change.Path }.Where(path => !string.IsNullOrEmpty(path)).Distinct())
                {
                    try
                    {
                        if (_repo.Index[path]?.Mode != Mode.GitLink && branch.Tip.Tree[path]?.Mode != Mode.GitLink)
                            Capture(path, ct);
                    }
                    catch (Exception failure)
                    {
                        preparationError = new IOException($"Cannot prepare '{path}' for checkout: {failure.Message}", failure);
                        throw preparationError;
                    }
                }
            }

            Commands.Checkout(_repo, branch, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.None,
                CheckoutNotifyFlags = CheckoutNotifyFlags.Updated,
                // libgit2 sends all notifications before changing any working files.
                OnCheckoutNotify = (path, _) =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_repo.Index[path]?.Mode != Mode.GitLink && branch.Tip.Tree[path]?.Mode != Mode.GitLink)
                        {
                            Capture(path, ct);
                        }

                        return true;
                    }
                    catch (Exception error)
                    {
                        preparationError = new IOException($"Cannot prepare '{path}' for checkout: {error.Message}", error);
                        return false;
                    }
                },
                OnCheckoutProgress = (path, completed, total) =>
                {
                    // Native progress callbacks cannot abort checkout. Remember errors and roll back afterwards.
                    try { progress?.Invoke(path, completed, total); }
                    catch (Exception error) { progressError ??= error; }
                }
            });
            if (progressError is not null) throw progressError;
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            var failures = new List<string>();
            // A rejected notification guarantees checkout hasn't changed working files yet.
            if (preparationError is null)
            {
                foreach (var saved in _paths.Values.OrderByDescending(saved => Depth(saved.Path)))
                {
                    if (!saved.Exists) TryRestore(saved, failures);
                }

                foreach (var saved in _paths.Values.Where(saved => saved.Exists).OrderBy(saved => Depth(saved.Path)))
                {
                    TryRestore(saved, failures);
                }

                foreach (var saved in _paths.Values.Where(saved => saved.Exists && saved.Directory && saved.Link is null)
                    .OrderByDescending(saved => Depth(saved.Path)))
                {
                    try { RestoreAttributes(saved); }
                    catch (Exception failure) { failures.Add($"{saved.Path}: {failure.Message}"); }
                }
            }

            // Restore the index byte-for-byte, including staged edits, then HEAD and its reflog.
            foreach (var saved in _metadata) TryRestore(saved, failures);
            var cause = preparationError ?? progressError ?? error;
            if (failures.Count > 0)
            {
                _retain = true;
                try
                {
                    File.WriteAllText(System.IO.Path.Combine(_backup, "recovery.json"),
                        JsonSerializer.Serialize(new { Paths = _paths.Values, Metadata = _metadata, Failures = failures }));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                throw new IOException($"Branch switch failed: {cause.Message} Automatic restoration is incomplete. "
                    + $"Close programs locking these files: {string.Join("; ", failures)}. "
                    + $"Recovery copies have been kept at '{_backup}'.", cause);
            }

            throw new IOException($"Branch switch failed: {cause.Message} "
                + (preparationError is not null ? "No working files were changed." : "The previous files, staged changes and branch were restored."), cause);
        }
    }

    private void Capture(string relative, CancellationToken ct)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (System.IO.Path.IsPathRooted(relative) || parts.Any(part => part is "" or "." or ".."
            || part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("Checkout supplied an unsafe file path.");
        }

        var path = _root;
        foreach (var part in parts.SkipLast(1))
        {
            path = System.IO.Path.Combine(path, part);
            if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Checkout cannot pass through a linked directory.");
            }

            if (!_paths.ContainsKey(path))
            {
                _paths[path] = Save(path, System.IO.Path.Combine(_backup, "worktree", System.IO.Path.GetRelativePath(_root, path)));
            }
        }

        CapturePath(System.IO.Path.Combine(_root, relative), ct);
    }

    private void CapturePath(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        path = System.IO.Path.GetFullPath(path);
        if (!_captured.Add(path)) return;
        var copy = System.IO.Path.Combine(_backup, "worktree", System.IO.Path.GetRelativePath(_root, path));
        var saved = _paths.GetValueOrDefault(path) ?? Save(path, copy);
        _paths[path] = saved;
        if (saved.Exists && saved.Directory && saved.Link is null)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) CapturePath(child, ct);
        }
        else if (saved.Exists && saved.Link is null)
        {
            // A handle that permits reads/writes but denies deletion can still stop libgit2's replacement.
            if (OperatingSystem.IsWindows())
            {
                using var handle = CreateFile(path, 0x00010000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) throw new IOException("The file is locked or cannot be replaced.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
    }

    private static SavedPath Save(string path, string? copy)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return new(path, null, false, false, null, 0, default); }
        catch (DirectoryNotFoundException) { return new(path, null, false, false, null, 0, default); }
        var directory = attributes.HasFlag(FileAttributes.Directory);
        var link = attributes.HasFlag(FileAttributes.ReparsePoint)
            ? (directory ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget) : null;
        if (!directory && link is null && copy is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(copy)!);
            File.Copy(path, copy);
        }

        return new(path, copy, true, directory, link, attributes, File.GetLastWriteTimeUtc(path),
            !OperatingSystem.IsWindows() && link is null ? File.GetUnixFileMode(path) : null);
    }

    private static void TryRestore(SavedPath saved, List<string> failures)
    {
        try { Restore(saved); }
        catch (Exception error) { failures.Add($"{saved.Path}: {error.Message}"); }
    }

    private static void Restore(SavedPath saved)
    {
        var exists = AttributesOf(saved.Path) is not null;
        if (!saved.Exists)
        {
            if (!exists) return;
            RemoveEntry(saved.Path);
            return;
        }

        if (saved.Link is not null)
        {
            if (exists && (saved.Directory ? new DirectoryInfo(saved.Path).LinkTarget : new FileInfo(saved.Path).LinkTarget) == saved.Link) return;
            if (exists)
            {
                RemoveEntry(saved.Path);
            }

            if (saved.Directory) Directory.CreateSymbolicLink(saved.Path, saved.Link);
            else File.CreateSymbolicLink(saved.Path, saved.Link);
            return;
        }

        if (saved.Directory)
        {
            if (exists && (!File.GetAttributes(saved.Path).HasFlag(FileAttributes.Directory)
                || File.GetAttributes(saved.Path).HasFlag(FileAttributes.ReparsePoint))) RemoveEntry(saved.Path);
            Directory.CreateDirectory(saved.Path);
            return;
        }

        if (exists && File.GetAttributes(saved.Path).HasFlag(FileAttributes.Directory))
        {
            RemoveEntry(saved.Path);
            exists = false;
        }
        else if (exists && File.GetAttributes(saved.Path).HasFlag(FileAttributes.ReparsePoint))
        {
            RemoveEntry(saved.Path);
            exists = false;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(saved.Path)!);
        if (!exists || !EqualContent(saved.Path, saved.Copy!))
        {
            if (exists) File.SetAttributes(saved.Path, File.GetAttributes(saved.Path) & ~FileAttributes.ReadOnly);
            File.Copy(saved.Copy!, saved.Path, true);
        }

        RestoreAttributes(saved);
    }

    private static void RestoreAttributes(SavedPath saved)
    {
        if (File.GetAttributes(saved.Path) != saved.Attributes) File.SetAttributes(saved.Path, saved.Attributes);
        if (!OperatingSystem.IsWindows() && saved.Permissions is { } permissions) File.SetUnixFileMode(saved.Path, permissions);
        if (File.GetLastWriteTimeUtc(saved.Path) != saved.Modified)
        {
            if (saved.Directory) Directory.SetLastWriteTimeUtc(saved.Path, saved.Modified);
            else File.SetLastWriteTimeUtc(saved.Path, saved.Modified);
        }
    }

    private static FileAttributes? AttributesOf(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void RemoveEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        // Empty directories only; never recursively remove concurrent user files or follow a link.
        if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(path);
        else File.Delete(path);
    }

    private static bool EqualContent(string left, string right)
    {
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        if (a.Length != b.Length) return false;
        var first = new byte[65536];
        var second = new byte[first.Length];
        int read;
        while ((read = a.Read(first)) > 0)
        {
            b.ReadExactly(second.AsSpan(0, read));
            if (!first.AsSpan(0, read).SequenceEqual(second.AsSpan(0, read))) return false;
        }

        return true;
    }

    private static int Depth(string path) => path.Count(c => c == System.IO.Path.DirectorySeparatorChar || c == '/');

    public void Dispose()
    {
        if (_retain || !Directory.Exists(_backup)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_backup, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_backup, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
