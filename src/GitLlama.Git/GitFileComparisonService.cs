using System.Text;
using LibGit2Sharp;

namespace GitLlama.Git;

public sealed record GitFileSnapshot(string Text, bool Exists);
public sealed record GitFileComparison(GitFileSnapshot Left, GitFileSnapshot Right, string Patch);

/// <summary>Reads revisions and working files without changing the index, refs or object database.</summary>
public sealed class GitFileComparisonService
{
    public Task<GitFileComparison> GetAsync(string repositoryPath, string path, string leftSha,
        string? rightSha = null, CancellationToken ct = default) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            || path.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Choose a repository-relative file path.", nameof(path));
        path = path.Replace('\\', '/');
        using var repo = new Repository(repositoryPath);
        var left = repo.Lookup<Commit>(leftSha) ?? throw new ArgumentException("The selected revision was not found.");
        var right = rightSha is null ? null
            : repo.Lookup<Commit>(rightSha) ?? throw new ArgumentException("The comparison revision was not found.");
        ct.ThrowIfCancellationRequested();
        var before = Snapshot(left.Tree[path]);
        GitFileSnapshot after;
        if (right is not null) after = Snapshot(right.Tree[path]);
        else
        {
            var file = GitPathGuard.RegularWorkingFile(repo, path);
            if (!File.Exists(file)) after = new("", false);
            else
            {
                if (new FileInfo(file).Length > 1_000_000)
                    throw new InvalidOperationException("The working file exceeds the 1 MB comparison preview limit.");
                after = new(Decode(File.ReadAllBytes(file)), true);
            }
        }
        ct.ThrowIfCancellationRequested();
        using var patch = right is not null
            ? repo.Diff.Compare<Patch>(left.Tree, right.Tree, new[] { path })
            : repo.Diff.Compare<Patch>(left.Tree, DiffTargets.WorkingDirectory, new[] { path });
        return new GitFileComparison(before, after, patch.Content);

        GitFileSnapshot Snapshot(TreeEntry? entry)
        {
            if (entry is null) return new("", false);
            if (entry.Mode == Mode.SymbolicLink || entry.Target is not Blob blob)
                throw new InvalidOperationException("Only regular text files can be compared.");
            if (blob.Size > 1_000_000)
                throw new InvalidOperationException("This revision exceeds the 1 MB comparison preview limit.");
            using var stream = blob.GetContentStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return new(Decode(buffer.ToArray()), true);
        }
    }, ct);

    private static string Decode(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) throw new InvalidOperationException("Binary files cannot be compared as text.");
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
