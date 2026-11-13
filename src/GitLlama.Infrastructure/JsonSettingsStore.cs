using System.Text.Json;
using GitLlama.Core;

namespace GitLlama.Infrastructure;

public sealed record SettingsProfile(
    string Id,
    string Name,
    string AuthorName = "",
    string AuthorEmail = "",
    bool UseCredentialManager = true,
    bool UseSshAgent = true,
    string SshPrivateKeyPath = "",
    string SshPublicKeyPath = ""
);

public sealed record GitLlamaSettings(
    List<RepositoryInfo> Repositories,
    string? ActiveRepository,
    List<string>? Tabs = null,
    string ActiveTab = "History",
    string LeftPanel = "Repositories",
    string RightPanel = "Inspector",
    string LayoutName = "Standard",
    bool AutoRefresh = true,
    int SchemaVersion = 1,
    List<string?>? OpenRepositoryPaths = null,
    int ActiveRepositoryTab = 0,
    bool UseCredentialManager = true,
    bool UseSshAgent = true,
    string SshPrivateKeyPath = "",
    string SshPublicKeyPath = "",
    List<SettingsProfile>? Profiles = null,
    string? ActiveProfileId = null,
    bool LoadAvatars = true,
    string PullMode = "Merge"
);

public sealed class JsonSettingsStore
{
    private readonly string _file;
    public string DirectoryPath => Path.GetDirectoryName(_file)!;

    public JsonSettingsStore(string? root = null)
    {
        var directory = root ?? AppDataPaths.SettingsDirectory();
        _file = Path.Combine(directory, "settings.json");
    }

    public async Task<GitLlamaSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_file))
        {
            return new GitLlamaSettings([], null);
        }

        try
        {
            await using var stream = File.OpenRead(_file);
            var settings = await JsonSerializer.DeserializeAsync<GitLlamaSettings>(stream, cancellationToken: ct);
            if (settings?.Repositories is null)
            {
                throw new JsonException("Settings must contain a repository list.");
            }

            return settings;
        }
        catch (JsonException error)
        {
            FileLog.Error(error, "Invalid settings file");
            // Preserve the invalid file for troubleshooting rather than overwriting it.
            var backup = _file + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try
            {
                File.Move(_file, backup);
            }
            catch (IOException moveError)
            {
                FileLog.Error(moveError, "Back up invalid settings");
            }
            catch (UnauthorizedAccessException moveError)
            {
                FileLog.Error(moveError, "Back up invalid settings");
            }

            return new GitLlamaSettings([], null);
        }
    }

    public async Task SaveAsync(GitLlamaSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: ct);
        }

        File.Move(temporary, _file, true);
    }
}
