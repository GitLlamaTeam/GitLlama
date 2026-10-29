using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;
using GitLlama.Infrastructure;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel : IDisposable
{
    private RepositoryWatcher? _watcher;
    private bool _monitoringEnabled;
    private bool _disposed;
    private long _watcherRevision;
    private int _watchRefreshRunning;

    [ObservableProperty]
    private bool autoRefresh = true;

    [ObservableProperty]
    private string diagnosticsText = "";

    [ObservableProperty]
    private string capabilityDescription = "";

    partial void OnAutoRefreshChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _ = PersistAsync();
        }

        _ = ConfigureWatcherAsync();
    }

    // Off until settings load so nothing is requested before the user's preference is known.
    [ObservableProperty]
    private bool loadAvatars;

    partial void OnLoadAvatarsChanged(bool value)
    {
        AvatarCache.Enabled = value;
        if (!_loadingSettings)
        {
            _ = PersistAsync();
        }
    }

    internal void StartRepositoryMonitoring() => _ = StartRepositoryMonitoringAsync();

    internal async Task StartRepositoryMonitoringAsync()
    {
        _monitoringEnabled = true;
        await ConfigureWatcherAsync();
    }

    internal void StopRepositoryMonitoring()
    {
        _monitoringEnabled = false;
        Interlocked.Increment(ref _watcherRevision);
        _watcher?.Dispose();
        _watcher = null;
    }

    private async Task ConfigureWatcherAsync()
    {
        var revision = Interlocked.Increment(ref _watcherRevision);
        _watcher?.Dispose();
        _watcher = null;
        if (!_monitoringEnabled || !AutoRefresh || _disposed || SelectedRepository is not { } repo)
        {
            return;
        }

        try
        {
            var roots = await new GitRuntimeService().WatchRootsAsync(repo.Path);
            if (revision != Interlocked.Read(ref _watcherRevision) || _disposed)
            {
                return;
            }

            _watcher = new RepositoryWatcher(
                roots,
                () => Dispatcher.UIThread.Post(() => _ = RefreshFromWatcherAsync(repo.Path)),
                gitDirectories: [roots.Last()]
            );
            // Reconcile edits that occurred while the native metadata/watchers were being opened.
            await RefreshFromWatcherAsync(repo.Path);
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _watcherRevision))
            {
                Status = "Automatic refresh unavailable: " + PresentError(e);
            }
        }
    }

    internal async Task RefreshFromWatcherAsync(string? expectedPath = null)
    {
        if (
            _disposed
            || !AutoRefresh
            || SelectedRepository is null
            || (expectedPath is not null && SelectedRepository.Path != expectedPath)
        )
        {
            return;
        }

        if (Interlocked.Exchange(ref _watchRefreshRunning, 1) != 0)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            await _gitMutationLock.WaitAsync();
            try
            {
                if (!_disposed && version == Interlocked.Read(ref _repositorySelectionVersion))
                {
                    await RefreshAsync();
                }
            }
            finally
            {
                _gitMutationLock.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _watchRefreshRunning, 0);
        }
    }

    [RelayCommand]
    private void RefreshDiagnostics()
    {
        try
        {
            CapabilityDescription = GitRuntimeService.Describe();
        }
        catch (Exception e)
        {
            CapabilityDescription = "Native Git unavailable: " + SafeMessage(e);
        }

        DiagnosticsText = SecretRedactor.Redact(
            $"GitLlama {typeof(MainWindowViewModel).Assembly.GetName().Version}\n"
                + $"OS: {RuntimeInformation.OSDescription}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\nRuntime: {RuntimeInformation.FrameworkDescription}\n"
                + CapabilityDescription
                + "\n\nCompatibility\nHTTPS: host-scoped session tokens or the configured system Git credential helper.\nSSH: system Git with OpenSSH, in-app PuTTY key unlocking through SSH.NET, or optional Plink/Pageant. Trusted host verification required.\n"
                + "Worktrees: native create/open/lock/removal; removed working folders are preserved.\n"
                + "Rebase: guarded linear replay, not full Git CLI parity.\n"
                + "Git LFS files are filtered and transferred through git-lfs when it's installed. Native Git operations do not execute signing or custom hooks. System Git SSH operations follow the user's Git configuration.\n"
                + "\nRecent operation status\n"
                + string.Join('\n', OperationLog.Take(100))
        );
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRepositoryMonitoring();
        CancelNetwork();
        _authenticationCancellation?.Cancel();
        SshSession.Dispose();
        _githubConnectionCancellation?.Cancel();
        _githubConnectionCancellation?.Dispose();
        _azurePersonalAccessTokens.Clear();
        ClearAzureOAuthMemory();
        AzurePatInput = "";
        if (_ownsHostingProvider && _hostingProvider is IDisposable provider)
        {
            provider.Dispose();
        }
    }
}
