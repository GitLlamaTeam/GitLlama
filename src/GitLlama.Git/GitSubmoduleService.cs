using LibGit2Sharp;

namespace GitLlama.Git;

public sealed record GitSubmoduleInfo(
    string Name,
    string Path,
    string Url,
    string State,
    string Commit,
    bool IsCheckedOut = false,
    string Summary = ""
)
{
    public string DisplayName =>
        Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? Path;

    public override string ToString() => $"{Path} · {State}";

    /// <summary>A short, readable status for the submodule.</summary>
    public static string Describe(SubmoduleStatus status, bool checkedOut)
    {
        if (!checkedOut)
        {
            return "Not initialized";
        }

        if (
            status.HasFlag(SubmoduleStatus.WorkDirFilesModified)
            || status.HasFlag(SubmoduleStatus.WorkDirFilesIndexDirty)
            || status.HasFlag(SubmoduleStatus.WorkDirFilesUntracked)
        )
        {
            return "Has changes";
        }

        if (status.HasFlag(SubmoduleStatus.WorkDirModified) || status.HasFlag(SubmoduleStatus.IndexModified))
        {
            return "Different commit";
        }

        return "Up to date";
    }
}

public sealed class GitSubmoduleService
{
    public Task<IReadOnlyList<GitSubmoduleInfo>> ListAsync(string path, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<GitSubmoduleInfo>>(
            () =>
            {
                using var repo = new Repository(path);
                using var lfsRead = GitLfsFilter.ReadOnly(repo);
                return repo
                    .Submodules.Select(module => (module, status: module.RetrieveStatus()))
                    // A submodule removed but not yet committed is only in HEAD; don't list it.
                    .Where(entry =>
                        entry.status.HasFlag(SubmoduleStatus.InIndex) || entry.status.HasFlag(SubmoduleStatus.InConfig)
                    )
                    .Select(entry =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var (module, status) = entry;
                        var checkedOut = Repository.IsValid(
                            System.IO.Path.Combine(repo.Info.WorkingDirectory, module.Path)
                        );
                        return new GitSubmoduleInfo(
                            module.Name,
                            module.Path,
                            module.Url,
                            status.ToString(),
                            module.WorkDirCommitId?.Sha ?? "Not initialized",
                            checkedOut,
                            GitSubmoduleInfo.Describe(status, checkedOut)
                        );
                    })
                    .ToArray();
            },
            ct
        );

    /// <summary>Suggests a folder for a new submodule from its URL, such as "UnrealSharp" for ".../UnrealSharp.git".</summary>
    public static string SuggestPath(string url)
    {
        var name = url.Trim().TrimEnd('/', '\\').Split('/', '\\', ':').LastOrDefault() ?? "";
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Clones <paramref name="url"/> into <paramref name="relativePath"/> and records it as a submodule.</summary>
    public async Task AddAsync(
        string path,
        string url,
        string relativePath,
        string? branch = null,
        GitNetworkOptions? network = null,
        CancellationToken ct = default
    )
    {
        url = url.Trim();
        if (url.Length == 0)
        {
            throw new ArgumentException("Enter the submodule's repository URL.", nameof(url));
        }

        string modulePath;
        using (var repo = new Repository(path))
        {
            if (repo.Info.IsBare || repo.Info.CurrentOperation != CurrentOperation.None)
            {
                throw new InvalidOperationException("Finish the current merge, rebase or other operation first.");
            }

            var absolute = GitPathGuard.RegularWorkingFile(repo, relativePath.Trim());
            if (
                File.Exists(absolute)
                || (Directory.Exists(absolute) && Directory.EnumerateFileSystemEntries(absolute).Any())
            )
            {
                throw new InvalidOperationException("Choose a folder that doesn't exist yet or is empty.");
            }

            modulePath = Path.GetRelativePath(repo.Info.WorkingDirectory, absolute).Replace('\\', '/');
        }

        var options = network ?? new GitNetworkOptions();
        options.EnsureAzureAccess(url);
        var ssh = GitSystemTransport.IsSsh(url);
        List<string> arguments = ["submodule", "add"];
        if (!string.IsNullOrWhiteSpace(branch))
        {
            arguments.AddRange(["-b", branch.Trim()]);
        }

        arguments.AddRange(["--", url, modulePath]);
        // A session token is only sent to the host it belongs to; other hosts use the credential helper.
        var tokenHost =
            !string.IsNullOrEmpty(options.Secret)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == "https"
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.Authority, options.CredentialHost, StringComparison.OrdinalIgnoreCase);
        await GitSystemTransport.RunAsync(
            path,
            arguments,
            options,
            ct,
            ssh: ssh,
            sshRemoteUrl: ssh ? url : null,
            httpsRemoteUrl: tokenHost ? url : null
        );
    }

    /// <summary>
    /// Removes a submodule from the working tree, index, .gitmodules and config. Its cloned history stays in
    /// .git/modules so nothing that wasn't pushed is lost.
    /// </summary>
    public async Task RemoveAsync(string path, string name, CancellationToken ct = default)
    {
        string modulePath;
        using (var repo = new Repository(path))
        {
            if (repo.Info.CurrentOperation != CurrentOperation.None)
            {
                throw new InvalidOperationException("Finish the current merge, rebase or other operation first.");
            }

            var module = repo.Submodules[name] ?? throw new InvalidOperationException("Submodule not found.");
            var directory = GitPathGuard.RegularWorkingFile(repo, module.Path);
            if (Repository.IsValid(directory))
            {
                using var child = new Repository(directory);
                if (child.RetrieveStatus().IsDirty)
                {
                    throw new InvalidOperationException(
                        "Commit or stash changes inside the submodule before removing it."
                    );
                }
            }

            modulePath = module.Path;
        }

        var options = new GitNetworkOptions();
        await GitSystemTransport.RunAsync(path, ["submodule", "deinit", "--force", "--", modulePath], options, ct);
        await GitSystemTransport.RunAsync(path, ["rm", "--force", "--", modulePath], options, ct);
    }

    public Task UpdateAsync(
        string path,
        string name,
        GitNetworkOptions? network = null,
        CancellationToken ct = default
    ) =>
        Task.Run(
            () =>
            {
                using var repo = new Repository(path);
                if (repo.Info.IsBare)
                {
                    throw new InvalidOperationException("Submodule checkout requires a working tree.");
                }

                var module = repo.Submodules[name] ?? throw new InvalidOperationException("Submodule not found.");
                var directory = GitPathGuard.RegularWorkingFile(repo, module.Path);
                if (Repository.IsValid(directory))
                {
                    using var child = new Repository(directory);
                    if (child.RetrieveStatus().IsDirty || child.Info.CurrentOperation != CurrentOperation.None)
                    {
                        throw new InvalidOperationException(
                            "Commit or stash changes inside the submodule before updating."
                        );
                    }
                }

                var options = new SubmoduleUpdateOptions { Init = true };
                network?.EnsureAzureAccess(module.Url);
                if (GitSystemTransport.IsSsh(module.Url) || network?.UsesAzureAuthorization(module.Url) == true)
                {
                    GitSystemTransport
                        .RunAsync(
                            path,
                            ["submodule", "update", "--init", "--", module.Path],
                            network ?? new GitNetworkOptions(),
                            ct,
                            ssh: GitSystemTransport.IsSsh(module.Url),
                            sshRemoteUrl: module.Url
                        )
                        .GetAwaiter()
                        .GetResult();
                    return;
                }

                var fetch = (network ?? new GitNetworkOptions()).Fetch(ct);
                options.FetchOptions.CredentialsProvider = fetch.CredentialsProvider;
                options.FetchOptions.OnTransferProgress = fetch.OnTransferProgress;
                ct.ThrowIfCancellationRequested();
                repo.Submodules.Update(name, options);
            },
            ct
        );
}
