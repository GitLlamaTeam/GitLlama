using LibGit2Sharp;
using GitLlama.Core;

namespace GitLlama.Git;

public sealed record GitFileInfo(string Path, string Status)
{
    public override string ToString() => Path;
}

public sealed class GitFileBrowserService
{
    public Task<string> ReadContentAsync(
        string repositoryPath,
        string path,
        bool staged,
        CancellationToken ct = default
    ) =>
        Task.Run(
            () =>
            {
                using var repo = new Repository(repositoryPath);
                ct.ThrowIfCancellationRequested();
                if (staged)
                {
                    var entry = repo.Index[path];
                    if (entry is null)
                    {
                        return "This file is absent from the index.";
                    }

                    var blob = repo.Lookup<Blob>(entry.Id);
                    if (blob is null || blob.IsBinary || blob.Size > 2 * 1024 * 1024)
                    {
                        return "Binary or large files cannot be previewed as text.";
                    }

                    return blob.GetContentText();
                }

                var absolute = GitPathGuard.RegularWorkingFile(repo, path);
                if (!File.Exists(absolute))
                {
                    return "This file is deleted from the working directory.";
                }

                if (new FileInfo(absolute).Length > 2 * 1024 * 1024)
                {
                    return "Large files cannot be previewed as text.";
                }

                var text = File.ReadAllText(absolute);
                return text.Contains('\0') ? "Binary files cannot be previewed as text." : text;
            },
            ct
        );

    public Task<IReadOnlyList<GitFileInfo>> ListAsync(string path, CancellationToken ct = default) =>
        ListCoreAsync(path, null, ct);

    /// <summary>Uses the working-tree snapshot already read during this refresh.</summary>
    public Task<IReadOnlyList<GitFileInfo>> ListAsync(
        string path, IReadOnlyList<ChangedFile> changes, CancellationToken ct = default) =>
        ListCoreAsync(path, changes, ct);

    private Task<IReadOnlyList<GitFileInfo>> ListCoreAsync(
        string path, IReadOnlyList<ChangedFile>? changes, CancellationToken ct) =>
        Task.Run<IReadOnlyList<GitFileInfo>>(
            () =>
            {
                using var repo = new Repository(path);
                using var lfsRead = GitLfsFilter.ReadOnly(repo);
                if (repo.Info.IsBare)
                {
                    return [];
                }

                var status = changes is not null
                    ? changes.ToDictionary(entry => entry.Path, entry => entry.State, StringComparer.Ordinal)
                    : repo.RetrieveStatus(
                        new StatusOptions { IncludeIgnored = false, RecurseUntrackedDirs = true }
                    )
                    .Where(e => (e.State & FileStatus.Ignored) == 0)
                    .ToDictionary(e => e.FilePath, e => e.State.ToString(), StringComparer.Ordinal);
                var files = repo
                    .Index.Select(entry => entry.Path)
                    .Concat(status.Keys)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal);
                return files
                    .Select(file =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return new GitFileInfo(file, status.GetValueOrDefault(file, "Unmodified"));
                    })
                    .ToArray();
            },
            ct
        );
}
