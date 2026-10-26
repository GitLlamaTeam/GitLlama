using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitLlama.Application;
using GitLlama.Core;
using GitLlama.Desktop;
using GitLlama.Desktop.Docking;
using GitLlama.Desktop.ViewModels;
using GitLlama.Desktop.Views;
using GitLlama.Git;
using GitLlama.Infrastructure;
using LibGit2Sharp;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(GitLlama.Desktop.Tests.TestAppBuilder))]

namespace GitLlama.Desktop.Tests;

public sealed class TestApp : App
{
    // Use the production styles without creating the real user's workspace or settings.
    public override void OnFrameworkInitializationCompleted() => AvatarCache.NetworkAvailable = false;
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class WorkspaceTests
{
    private sealed class FakeAzureOAuth : GitLlama.Providers.IAzureDevOpsOAuth
    {
        public int TokenRequests { get; private set; }
        public bool Reject { get; set; }
        public bool BackgroundCallback { get; set; }
        public TaskCompletionSource? ContinueSignIn { get; set; }
        public async Task<GitLlama.Providers.AzureOAuthIdentity> SignInAsync(Func<GitLlama.Providers.AzureDeviceCode, Task> showCode, CancellationToken ct)
        {
            var code = new GitLlama.Providers.AzureDeviceCode("TEST-CODE", new Uri("https://microsoft.com/devicelogin"));
            if (BackgroundCallback) await Task.Run(() => showCode(code), ct);
            else await showCode(code);
            if (ContinueSignIn is not null) await ContinueSignIn.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (Reject) throw new InvalidOperationException("Test sign-in rejected.");
            return new("account-in-tenant", "test@example.invalid");
        }
        public Task<string> AccessTokenAsync(string accountId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TokenRequests++;
            Assert.Equal("account-in-tenant", accountId);
            return Task.FromResult("oauth-access-secret");
        }
        public byte[] ExportCache() => System.Text.Encoding.UTF8.GetBytes("cache-with-refresh-secret");
    }

    [AvaloniaFact]
    public async Task AzureOAuthCoexistsWithPatGrantsRevokesBrowsesAndRestoresPerProfile()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var oauth = new FakeAzureOAuth { BackgroundCallback = true };
        vm.AzureOAuthFactory = (_, _, _) => oauth;
        vm.AzureOrganizationLoader = (token, _) =>
        {
            Assert.Equal("oauth-access-secret", token);
            return Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureDiscoveredOrganization>>([new("oauth-team"), new("other-team")]);
        };
        vm.AzureRepositoryLoader = (org, token, _) =>
        {
            Assert.Equal("pat-secret", token);
            return Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureRepository>>([new("pat", "repo", "project", "https://dev.azure.com/pat-team/project/_git/repo", "")]);
        };
        vm.AzureOAuthRepositoryLoader = (org, token, _) =>
        {
            Assert.Equal("oauth-team", org);
            Assert.Equal("oauth-access-secret", token);
            return Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureRepository>>([new("oauth", "repo", "project", "https://dev.azure.com/oauth-team/project/_git/repo", "")]);
        };
        vm.OpenIntegrationBrowser = uri =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            Assert.True(IntegrationBrowserPolicy.Allows(uri));
            Assert.Equal("https://microsoft.com/devicelogin", uri.AbsoluteUri.TrimEnd('/'));
            Assert.True(vm.HasAzureOAuthCode);
            return Task.FromResult(true);
        };
        vm.AzureOrganizationInput = "pat-team";
        vm.AzurePatInput = "pat-secret";
        await vm.ConnectAzurePatCommand.ExecuteAsync(null);
        await vm.ConnectAzureOAuthCommand.ExecuteAsync(null);
        Assert.True(vm.IsAzureOAuthConnected);
        Assert.False(vm.HasAzureOAuthCode);
        Assert.Equal("pat-team", Assert.Single(vm.AzureOrganizations).Name);
        Assert.Equal(2, vm.AzureOAuthOrganizations.Count);
        var url = "https://dev.azure.com/oauth-team/project/_git/repo";
        Assert.Throws<InvalidOperationException>(() => vm.NetworkOptions(0, remoteUrl: url).EnsureAzureAccess(url));
        await vm.ToggleAzureOAuthOrganizationCommand.ExecuteAsync(vm.AzureOAuthOrganizations.Single(org => org.Name == "oauth-team"));
        Assert.Equal(2, vm.AzureOrganizations.Count);
        var options = vm.NetworkOptions(0, remoteUrl: url);
        Assert.True(options.UsesAzureAuthorization(url));
        Assert.Empty(options.AzurePersonalAccessToken);
        Assert.Equal("oauth-access-secret", await options.AzureOAuthTokenProvider!(CancellationToken.None));
        Assert.Null(vm.NetworkOptions(0, remoteUrl: "git@ssh.dev.azure.com:v3/oauth-team/project/repo").AzureOAuthTokenProvider);
        await vm.RefreshCloneableRepositoriesCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.CloneableRepositories.Count);
        Assert.Contains(vm.CloneableRepositories, repo => repo.CloneUrl == url);
        await vm.ToggleAzureOAuthOrganizationCommand.ExecuteAsync(vm.AzureOAuthOrganizations.Single(org => org.Name == "oauth-team"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => options.AzureOAuthTokenProvider!(CancellationToken.None));
        Assert.Equal("pat-team", Assert.Single(vm.AzureOrganizations).Name);
        await vm.ToggleAzureOAuthOrganizationCommand.ExecuteAsync(vm.AzureOAuthOrganizations.Single(org => org.Name == "oauth-team"));

        if (OperatingSystem.IsWindows())
        {
            var originalProfile = vm.ActiveProfile!;
            await vm.CreateProfileCommand.ExecuteAsync(null);
            Assert.False(vm.IsAzureOAuthConnected);
            Assert.Empty(vm.AzureOrganizations);
            vm.ProfileToSwitch = originalProfile;
            await vm.SwitchProfileCommand.ExecuteAsync(null);
            Assert.True(vm.IsAzureOAuthConnected);
            Assert.Equal(2, vm.AzureOrganizations.Count);
            Assert.True(vm.AzureOAuthOrganizations.Single(org => org.Name == "oauth-team").IsAllowed);
            var encrypted = await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(fixture.SettingsPath, "connections"), "azure-*.dat").Single());
            Assert.DoesNotContain("cache-with-refresh-secret", System.Text.Encoding.UTF8.GetString(encrypted));
            Assert.DoesNotContain("pat-secret", File.ReadAllText(Path.Combine(fixture.SettingsPath, "settings.json")));
        }
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            window.ShowDocument("Settings");
            vm.SelectedSettingsSection = "Integrations";
            Dispatcher.UIThread.RunJobs();
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            settings.FindControl<Expander>("AzureIntegrationExpander")!.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(settings.FindControl<Button>("AzureSignInButton")!.IsVisible);
            Assert.NotNull(settings.FindControl<Button>("AzurePatConnectButton"));
            Assert.Null(settings.FindControl<TextBox>("AzureOAuthTenantInput"));
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(), block => block.Text is "Directory (tenant) ID" or "Application (client) ID");
            settings.FindControl<Expander>("AzureIntegrationExpander")!.BringIntoView();
            SavePreview(window, "azure-oauth-and-pat.png");
        }
        finally { window.Close(); }
        await vm.DisconnectAzureOAuthCommand.ExecuteAsync(null);
        Assert.False(vm.IsAzureOAuthConnected);
        Assert.Equal("pat-team", Assert.Single(vm.AzureOrganizations).Name);
        Assert.Equal("pat-secret", vm.NetworkOptions(0, remoteUrl: "https://dev.azure.com/pat-team/project/_git/repo").AzurePersonalAccessToken);
    }

    [AvaloniaFact]
    public async Task AzureOAuthEmptyDiscoveryAndFailedSignInPreservePatConnections()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var oauth = new FakeAzureOAuth();
        vm.AzureOAuthFactory = (_, _, _) => oauth;
        vm.AzureOrganizationLoader = (_, _) => Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureDiscoveredOrganization>>([]);
        vm.AzureRepositoryLoader = (_, _, _) => Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureRepository>>([]);
        vm.AzureOrganizationInput = "pat-team";
        vm.AzurePatInput = "pat-secret";
        await vm.ConnectAzurePatCommand.ExecuteAsync(null);
        await vm.ConnectAzureOAuthCommand.ExecuteAsync(null);
        Assert.Contains("directory identity", vm.AzureOAuthFeedback);
        Assert.Empty(vm.AzureOAuthOrganizations);
        Assert.True(vm.IsAzureOAuthConnected);
        oauth.Reject = true;
        await vm.ConnectAzureOAuthCommand.ExecuteAsync(null);
        Assert.True(vm.IsAzureOAuthConnected);
        Assert.Single(vm.AzureOrganizations);
        Assert.False(vm.IsAuthenticationBusy);
        Assert.False(vm.HasAzureOAuthCode);
        oauth.Reject = false;
        vm.OpenIntegrationBrowser = _ => { vm.CancelAuthenticationCommand.Execute(null); return Task.FromResult(true); };
        await vm.ConnectAzureOAuthCommand.ExecuteAsync(null);
        Assert.True(vm.IsAzureOAuthConnected);
        Assert.Single(vm.AzureOrganizations);
        Assert.Contains("canceled", vm.AzureOAuthFeedback);
    }

    private static Avalonia.Input.RawInputModifiers RawModifiers(Avalonia.Input.KeyModifiers modifiers)
    {
        var raw = Avalonia.Input.RawInputModifiers.None;
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) raw |= Avalonia.Input.RawInputModifiers.Control;
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta)) raw |= Avalonia.Input.RawInputModifiers.Meta;
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) raw |= Avalonia.Input.RawInputModifiers.Shift;
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) raw |= Avalonia.Input.RawInputModifiers.Alt;
        return raw;
    }

    private static void PressShortcut(Window window, Avalonia.Input.KeyGesture gesture, Avalonia.Input.PhysicalKey physicalKey)
    {
        var modifiers = RawModifiers(gesture.KeyModifiers);
        window.KeyPressQwerty(physicalKey, modifiers);
        window.KeyReleaseQwerty(physicalKey, modifiers);
    }

    private static void ClickInspectorFile(Window window, ListBox list, int index, bool shift = false, bool control = false)
    {
        list.ScrollIntoView(list.Items[index]!);
        Dispatcher.UIThread.RunJobs();
        var row = list.ContainerFromIndex(index)!;
        var point = row.TranslatePoint(new Point(130, row.Bounds.Height / 2), window)!.Value;
        var modifiers = shift ? Avalonia.Input.RawInputModifiers.Shift
            : control ? RawModifiers(Avalonia.Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers)
            : Avalonia.Input.RawInputModifiers.None;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left, modifiers);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task InspectorShiftSelectionStagesAndUnstagesOnlySelectedFiles()
    {
        using var fixture = new WorkspaceFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "extra.cs"), "// Leave this file unstaged\n");
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.IsWorkingTreeInspector = true;
        var inspector = new InspectorView();
        var review = new FileReviewView();
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("400,*") };
        layout.Children.Add(inspector);
        Grid.SetColumn(review, 1);
        layout.Children.Add(review);
        var window = new Window { Width = 1300, Height = 850, DataContext = vm, Content = layout };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = inspector.FindControl<ListBox>("UnstagedFileList")!;
            vm.SelectedWorkingFile = vm.UnstagedFiles[2];
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(vm.SelectedWorkingFile, Assert.Single(list.SelectedItems!.Cast<WorkingFileRow>()));
            ClickInspectorFile(window, list, 0);
            Assert.Equal("✓ Stage all", vm.InspectorStageLabel);
            ClickInspectorFile(window, list, 1, shift: true);
            ClickInspectorFile(window, list, 2, control: true);
            Assert.Equal(3, vm.SelectedUnstagedFiles.Count);
            ClickInspectorFile(window, list, 2, control: true);
            Assert.Equal(2, vm.SelectedUnstagedFiles.Count);
            Assert.Equal("Stage selected (2)", vm.InspectorStageLabel);
            Assert.False(vm.CanInspectorUnstage);
            Assert.True(vm.IsMultiFileReview);
            var selected = vm.SelectedUnstagedFiles.Select(file => file.Path).ToArray();
            await vm.LoadMultiFilePreviewsAsync();
            Assert.Equal(selected, vm.MultiFilePreviews.Select(preview => preview.Path));
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.SelectedUnstagedFiles.Count);
            Assert.True(vm.IsMultiFileReview);
            Assert.Equal(2, vm.MultiFilePreviews.Count);
            SavePreview(window, "multi-file-working-selection.png");
            await vm.StageInspectorFilesCommand.ExecuteAsync(null);
            using (var repo = new Repository(fixture.RepositoryPath))
            {
                var staged = repo.RetrieveStatus().Where(entry => (entry.State & (FileStatus.NewInIndex | FileStatus.ModifiedInIndex)) != 0)
                    .Select(entry => entry.FilePath).ToArray();
                Assert.Equal(selected.Order(), staged.Order());
            }
            Assert.Single(vm.UnstagedFiles);
            var stagedList = inspector.FindControl<ListBox>("StagedFileList")!;
            ClickInspectorFile(window, stagedList, 0);
            Assert.Equal("Unstage all", vm.InspectorUnstageLabel);
            Assert.True(vm.CanInspectorStage);
            ClickInspectorFile(window, stagedList, 1, shift: true);
            Assert.Equal("Unstage selected (2)", vm.InspectorUnstageLabel);
            Assert.False(vm.CanInspectorStage);
            await vm.UnstageInspectorFilesCommand.ExecuteAsync(null);
            Assert.Empty(vm.StagedFiles);
            Assert.Equal(3, vm.UnstagedFiles.Count);
            vm.ClearInspectorFileSelectionCommand.Execute(null);
            Assert.Equal("✓ Stage all", vm.InspectorStageLabel);
            Assert.False(vm.IsMultiFileReview);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SingleInspectorSelectionKeepsStageAllAndUnstageAllBehavior()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var count = vm.UnstagedFiles.Count;
        Assert.True(count > 1);
        vm.SelectedUnstagedFiles.Add(vm.UnstagedFiles.First());
        vm.ActivateWorkingFileSelection(false, vm.SelectedUnstagedFiles.First());
        Assert.Equal("✓ Stage all", vm.InspectorStageLabel);
        await vm.StageInspectorFilesCommand.ExecuteAsync(null);
        Assert.Equal(count, vm.StagedFiles.Count);
        Assert.Empty(vm.UnstagedFiles);
        vm.SelectedStagedFiles.Add(vm.StagedFiles.First());
        vm.ActivateWorkingFileSelection(true, vm.SelectedStagedFiles.First());
        Assert.Equal("Unstage all", vm.InspectorUnstageLabel);
        await vm.UnstageInspectorFilesCommand.ExecuteAsync(null);
        Assert.Empty(vm.StagedFiles);
        Assert.Equal(count, vm.UnstagedFiles.Count);
    }

    [AvaloniaFact]
    public async Task CommitInspectorShiftSelectionOpensMultipleDiffsAndFilePreviews()
    {
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            File.WriteAllText(Path.Combine(fixture.RepositoryPath, "extra.cs"), "// Third changed file\n");
            Commands.Stage(repo, "*");
            var author = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            repo.Commit("Three files for review", author, author);
        }
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        await WaitUntilAsync(() => vm.HistoryFileRows.Count == 3);
        var inspector = new InspectorView();
        var review = new FileReviewView();
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("400,*") };
        layout.Children.Add(inspector);
        Grid.SetColumn(review, 1);
        layout.Children.Add(review);
        var window = new Window { Width = 1300, Height = 850, DataContext = vm, Content = layout };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = inspector.FindControl<ListBox>("CommitFileList")!;
            ClickInspectorFile(window, list, 0);
            ClickInspectorFile(window, list, 1, shift: true);
            Assert.Equal(2, vm.SelectedCommitFiles.Count);
            Assert.True(vm.IsMultiFileReview);
            Assert.Equal("2 selected files", vm.ReviewTitle);
            await vm.LoadMultiFilePreviewsAsync();
            Assert.Equal(2, vm.MultiFilePreviews.Count);
            Assert.All(vm.MultiFilePreviews, preview => { Assert.True(preview.IsDiff); Assert.Contains("diff --git", preview.Content); });
            SavePreview(window, "multi-file-commit-diffs.png");
            vm.ReviewFileCommand.Execute(null);
            await vm.LoadMultiFilePreviewsAsync();
            Assert.All(vm.MultiFilePreviews, preview => Assert.True(preview.IsFile));
            SavePreview(window, "multi-file-commit-content.png");
            ClickInspectorFile(window, list, 2);
            Assert.Single(vm.SelectedCommitFiles);
            Assert.False(vm.IsMultiFileReview);
            ClickInspectorFile(window, list, 0);
            ClickInspectorFile(window, list, 1, shift: true);
            var pending = vm.LoadMultiFilePreviewsAsync();
            vm.SelectedHistoryRow = vm.HistoryRows.Skip(1).First();
            await pending;
            Assert.Empty(vm.SelectedCommitFiles);
            Assert.Empty(vm.MultiFilePreviews);
            Assert.False(vm.IsMultiFileReview);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FileHistoryPreviewsSelectionAndComparesWorkingOrOtherRevisions()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.OpenFileReview("Working", "README.md");
        await vm.ReviewFileHistoryCommand.ExecuteAsync(null);
        Assert.True(vm.IsHistoryMode);
        Assert.False(vm.IsContentReview);
        Assert.True(vm.FileRevisions.Count > 1);
        vm.SelectedFileRevision = vm.FileRevisions.Last();
        await vm.LoadFileRevisionPreviewAsync();
        Assert.Contains("Initial commit", vm.FileRevisionContent);
        Assert.DoesNotContain("Documentation updates", vm.FileRevisionContent);
        vm.FileComparisonMode = "Current file";
        await vm.LoadFileRevisionPreviewAsync();
        Assert.Contains("Documentation updates", vm.FileComparisonContent);
        Assert.NotEmpty(vm.FileComparisonLineKinds);
        Assert.Equal("Current working file", vm.FileComparisonLabel);
        var view = new FileReviewView();
        var window = new Window { Width = 1380, Height = 700, DataContext = vm, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<GitLlama.Desktop.Controls.CodeView>("HistoryRevisionPreview")!.Editor.IsReadOnly);
            Assert.True(view.FindControl<GitLlama.Desktop.Controls.CodeView>("HistoryComparisonPreview")!.Editor.IsReadOnly);
            SavePreview(window, "file-history-current-comparison.png");
            vm.FileComparisonMode = "Another revision";
            vm.SelectedComparisonRevision = vm.FileRevisions.First();
            await vm.LoadFileRevisionPreviewAsync();
            Assert.DoesNotContain("Documentation updates", vm.FileComparisonContent);
            Assert.Contains("Initial commit", vm.FileComparisonContent);
            Assert.NotEqual(vm.FileRevisionContent, vm.FileComparisonContent);
            Assert.True(view.FindControl<ComboBox>("ComparisonRevisionPicker")!.IsVisible);
            SavePreview(window, "file-history-revision-comparison.png");
            vm.FileComparisonMode = "Preview";
            view.FindControl<ListBox>("FileRevisionList")!.SelectedItem = vm.FileRevisions.First();
            await vm.LoadFileRevisionPreviewAsync();
            Assert.Equal(vm.FileRevisions.First(), vm.SelectedFileRevision);
            Assert.False(vm.IsHistoryComparison);
            SavePreview(window, "file-history-preview.png");
            var pending = vm.LoadFileRevisionPreviewAsync();
            vm.FileHistoryPath = "src/Auth.cs";
            await pending;
            Assert.Empty(vm.FileRevisionContent);
            Assert.Empty(vm.FileComparisonContent);
            Assert.Empty(vm.FileRevisions);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureSignInKeepsCodeVisibleWhenBrowserCannotOpen(bool throws)
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.AzureOAuthFactory = (_, _, _) => new FakeAzureOAuth
        {
            BackgroundCallback = true,
            ContinueSignIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var launches = 0;
        vm.OpenIntegrationBrowser = uri =>
        {
            launches++;
            Assert.True(Dispatcher.UIThread.CheckAccess());
            Assert.True(IntegrationBrowserPolicy.Allows(uri));
            return throws ? throw new IOException("Browser unavailable") : Task.FromResult(false);
        };
        var signIn = vm.ConnectAzureOAuthCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.HasAzureOAuthCode && vm.AzureOAuthFeedback.Contains("https://microsoft.com/devicelogin"));
        Assert.Equal("TEST-CODE", vm.AzureOAuthUserCode);
        Assert.True(vm.IsAuthenticationBusy);
        Assert.False(signIn.IsCompleted);
        await vm.OpenAzureOAuthPageCommand.ExecuteAsync(null);
        Assert.Equal(2, launches);
        Assert.True(vm.HasAzureOAuthCode);
        vm.CancelAuthenticationCommand.Execute(null);
        await signIn;
        Assert.False(vm.IsAuthenticationBusy);
        Assert.False(vm.HasAzureOAuthCode);
        Assert.Contains("canceled", vm.AzureOAuthFeedback);
    }

    [AvaloniaFact]
    public async Task DiscardAllInspectorActionIsRedConfirmsAndHidesForCleanOrConflictedWorkspaces()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.IsWorkingTreeInspector = true;
        var view = new InspectorView { DataContext = vm };
        var window = new Window { Content = view, Width = 500, Height = 850 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<Button>("DiscardAllChangesButton")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal("#fff28b93", button.Foreground!.ToString()!.ToLowerInvariant());
            var conflict = new WorkingFileRow("conflict", "conflict.txt", "Conflicted");
            vm.WorkingFiles.Add(conflict);
            Assert.False(button.IsVisible);
            vm.WorkingFiles.Remove(conflict);
            vm.RepositoryOperation = "Merge";
            Assert.False(button.IsVisible);
            vm.RepositoryOperation = "None";
            Assert.True(button.IsVisible);

            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !dialog.IsVisible);
            Assert.NotEmpty(vm.WorkingFiles);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => window.OwnedWindows.Any());
            dialog = window.OwnedWindows.Single();
            var confirm = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Discard all changes"));
            Assert.False(confirm.IsDefault);
            confirm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !vm.IsFeatureBusy && vm.WorkingFiles.Count == 0);
            Assert.False(button.IsVisible);
            Assert.Equal("Discarded all changes.", vm.Status);
            Assert.Empty(vm.ErrorNotifications);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DiscardInspectorWarnsForSelectedFilesAndLeavesOtherChangesUntouched()
    {
        using var fixture = new WorkspaceFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "extra.cs"), "// selected new file\n");
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.IsWorkingTreeInspector = true;
        var view = new InspectorView();
        var window = new Window { Content = view, DataContext = vm, Width = 500, Height = 850 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("UnstagedFileList")!;
            var button = view.FindControl<Button>("DiscardAllChangesButton")!;
            ClickInspectorFile(window, list, 0);
            Assert.Equal("Discard all changes…", button.Content);
            ClickInspectorFile(window, list, 1, shift: true);
            var selected = vm.InspectorDiscardPaths();
            Assert.Equal(2, selected.Length);
            Assert.Equal("Discard selected changes (2)…", button.Content);
            var kept = vm.UnstagedFiles.Single(file => !selected.Contains(file.Path)).Path;
            var keptContent = File.ReadAllText(Path.Combine(fixture.RepositoryPath, kept));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => window.OwnedWindows.Any());
                var dialog = window.OwnedWindows.Single();
                var warning = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
                Assert.All(selected, path => Assert.Contains(path, warning));
                Assert.Contains("staged and unstaged", warning);
                var confirm = dialog.GetVisualDescendants().OfType<Button>().Single(item => Equals(item.Content, "Discard selected changes"));
                Assert.False(confirm.IsDefault);
                if (attempt == 0)
                {
                    SavePreview(dialog, "discard-selected-warning.png");
                    dialog.GetVisualDescendants().OfType<Button>().Single(item => Equals(item.Content, "Cancel"))
                        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => !dialog.IsVisible);
                    Assert.Equal(3, vm.WorkingFiles.Count);
                }
                else
                {
                    confirm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => !vm.IsFeatureBusy && vm.WorkingFiles.Count == 1);
                }
            }
            Assert.Equal(kept, Assert.Single(vm.WorkingFiles).Path);
            Assert.Equal(keptContent, File.ReadAllText(Path.Combine(fixture.RepositoryPath, kept)));
            Assert.Equal("Discarded changes in 2 selected files.", vm.Status);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkingChangesAndHistoryActivateTheirGroupedTool(bool groupOnLeft)
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => !vm.IsLoading);
            var control = window.FindControl<Dock.Avalonia.Controls.DockControl>("WorkspaceDock")!;
            var tools = MainWindow.WalkDockables(control.Layout).ToArray();
            var repository = tools.Single(d => d.Id == "repositories");
            var inspector = tools.Single(d => d.Id == "inspector");
            var left = (Dock.Model.Core.IDock)repository.Owner!;
            var right = (Dock.Model.Core.IDock)inspector.Owner!;
            var group = groupOnLeft ? left : right;
            control.Factory!.MoveDockable(
                groupOnLeft ? right : left, group,
                groupOnLeft ? inspector : repository,
                groupOnLeft ? repository : inspector);
            group.ActiveDockable = repository;

            window.ShowDocument("Changes");
            Assert.True(vm.IsWorkingTreeInspector);
            Assert.Same(inspector, group.ActiveDockable);

            window.ShowDocument("History");
            Assert.False(vm.IsWorkingTreeInspector);
            Assert.False(vm.IsFileReview);
            Assert.Same(repository, group.ActiveDockable);

            // Commands used inside panels must reveal the tool too, even without the toolbar handler.
            vm.ShowWorkingTreeCommand.Execute(null);
            Assert.Same(inspector, group.ActiveDockable);
            window.ShowDocument("History");
            Assert.Same(repository, group.ActiveDockable);
            Assert.Same(group, repository.Owner);
            Assert.Same(group, inspector.Owner);
            Assert.Equal(2, group.VisibleDockables!.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedActionsShowPersistentErrorCardsAndGroupRepeatedFailures()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        Assert.Empty(vm.ErrorNotifications);
        vm.SelectedBranchLine = "missing-error-test";
        await vm.CheckoutBranchCommand.ExecuteAsync(null);
        var error = Assert.Single(vm.ErrorNotifications);
        Assert.Contains("does not exist", error.Message);
        await vm.CheckoutBranchCommand.ExecuteAsync(null);
        Assert.Same(error, Assert.Single(vm.ErrorNotifications));
        Assert.Equal(2, error.Occurrences);
        vm.Status = "Ready";
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Same(error, Assert.Single(vm.ErrorNotifications));
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => !vm.IsLoading);
            var notifications = window.FindControl<ErrorNotificationsView>("ErrorNotifications")!;
            Assert.True(notifications.IsVisible);
            SavePreview(window, "visible-error-cards.png");
            var dismiss = notifications.GetVisualDescendants().OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, vm.DismissErrorCommand));
            dismiss.Command!.Execute(dismiss.CommandParameter);
            Assert.Empty(vm.ErrorNotifications);
            Assert.False(notifications.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ErrorCardsRedactCredentialsAndDoNotTreatCancellationAsFailure()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.NetworkSecret = "private-test-credential";
        vm.PresentError(new IOException("Cannot connect: private-test-credential https://user:password@example.com/repo"));
        var error = Assert.Single(vm.ErrorNotifications);
        Assert.DoesNotContain("private-test-credential", error.Message);
        Assert.DoesNotContain("user:password", error.Message);
        Assert.Contains("[redacted]", error.Message);
        vm.PresentError(new OperationCanceledException());
        Assert.Single(vm.ErrorNotifications);
        vm.DismissAllErrorsCommand.Execute(null);
        Assert.False(vm.HasErrorNotifications);
    }

    [AvaloniaFact]
    public async Task MissingRepositoryIsRejectedWithoutChangingTheCurrentWorkspace()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var originalRepository = vm.SelectedRepository;
        var originalTab = vm.SelectedRepositoryWorkspaceTab;
        var originalHistory = vm.HistoryRows.ToArray();
        var count = vm.RepositoryWorkspaceTabs.Count;
        var missing = new RepositoryInfo(Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "missing"), "Missing");
        vm.OpenRepositoryTab(missing);
        Assert.Same(originalRepository, vm.SelectedRepository);
        Assert.Same(originalTab, vm.SelectedRepositoryWorkspaceTab);
        Assert.Equal(count, vm.RepositoryWorkspaceTabs.Count);
        Assert.Equal(originalHistory, vm.HistoryRows);
        Assert.Contains("missing or unavailable", vm.RepositoryOpenError);
        Assert.Contains(missing.Path, vm.RepositoryOpenError);

        vm.SelectedRepository = missing;
        Assert.Same(originalRepository, vm.SelectedRepository);
        Assert.Equal(originalHistory, vm.HistoryRows);
        var missingTab = new RepositoryWorkspaceTab { Repository = missing };
        vm.RepositoryWorkspaceTabs.Add(missingTab);
        vm.SelectedRepositoryWorkspaceTab = missingTab;
        Assert.Same(originalTab, vm.SelectedRepositoryWorkspaceTab);
        Assert.Same(originalRepository, vm.SelectedRepository);

        Directory.CreateDirectory(missing.Path);
        vm.OpenRepositoryTab(missing);
        Assert.Contains("no longer a Git repository", vm.RepositoryOpenError);
        Assert.Same(originalRepository, vm.SelectedRepository);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => !vm.IsLoading);
            Assert.True(window.FindControl<Border>("RepositoryOpenErrorBanner")!.IsVisible);
            vm.DismissRepositoryOpenErrorCommand.Execute(null);
            Assert.False(window.FindControl<Border>("RepositoryOpenErrorBanner")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task StartupDoesNotOpenMissingSavedRepositoryTabs()
    {
        using var fixture = new WorkspaceFixture();
        var missing = new RepositoryInfo(Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "missing-startup"), "Missing");
        var store = new JsonSettingsStore(fixture.SettingsPath);
        await store.SaveAsync(new GitLlamaSettings([missing], missing.Path,
            OpenRepositoryPaths: [missing.Path], AutoRefresh: false, LoadAvatars: false));
        using var sessions = new RepositorySessionManager(new LibGitRepositoryReader(), new LibGitOverviewReader());
        using var vm = new MainWindowViewModel(sessions, store, new GitWorkingTreeService());
        await vm.Initialization;
        Assert.Null(vm.SelectedRepository);
        Assert.True(vm.IsRepositoryManagerSelected);
        Assert.DoesNotContain(vm.RepositoryWorkspaceTabs, tab => tab.Repository?.Path == missing.Path);
        Assert.Contains(vm.Repositories, repository => repository.Path == missing.Path);
        Assert.Contains(missing.Path, vm.RepositoryOpenError);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupOverlayWaitsForSavedRepositoryAndClearsAfterFailure(bool fail)
    {
        using var fixture = new WorkspaceFixture();
        var reader = new PausedStartupReader(fail);
        using var sessions = new RepositorySessionManager(reader, new LibGitOverviewReader());
        var store = new JsonSettingsStore(fixture.SettingsPath);
        await store.SaveAsync(new GitLlamaSettings(
            [new RepositoryInfo(fixture.RepositoryPath, "StoneForge")], fixture.RepositoryPath,
            AutoRefresh: true, LoadAvatars: false));
        using var vm = new MainWindowViewModel(sessions, store, new GitWorkingTreeService());
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => reader.Entered);
            Assert.True(vm.IsStarting);
            Assert.False(vm.Initialization.IsCompleted);
            Assert.False(vm.IsWorkspaceInteractive);
            Assert.Equal("Starting GitLlama", vm.LoadingTitle);
            Assert.True(window.FindControl<BranchSwitchView>("BranchSwitchOverlay")!.IsVisible);
            SavePreview(window, "startup-llama.png");
            reader.Release.TrySetResult();
            await vm.Initialization;
            await WaitUntilAsync(() => !vm.IsLoading);
            Assert.False(vm.IsStarting);
            Assert.False(vm.IsLoading);
            Assert.True(vm.IsWorkspaceInteractive);
            Assert.False(window.FindControl<BranchSwitchView>("BranchSwitchOverlay")!.IsVisible);
            if (fail)
                Assert.Contains("Startup repository unavailable", vm.Status);
            else
                Assert.NotEmpty(vm.HistoryRows);
            var reads = reader.Reads;
            await Task.Delay(800);
            Assert.Equal(reads, reader.Reads);
        }
        finally
        {
            reader.Release.TrySetResult();
            await vm.Initialization;
            window.Close();
        }
    }

    private sealed class PausedStartupReader(bool fail) : IRepositoryReader
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Entered { get; private set; }
        public int Reads { get; private set; }
        public bool IsRepository(string path) => Repository.IsValid(path);
        public async Task<RepositorySnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Entered = true;
            Reads++;
            await Release.Task.WaitAsync(cancellationToken);
            if (fail)
                throw new IOException("Startup repository unavailable");
            return await new LibGitRepositoryReader().ReadAsync(path, cancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task BranchSwitchOverlayShowsLlamaAndAlwaysRestoresWorkspace()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            vm.BranchSwitchTarget = "feature/authentication";
            vm.IsBranchSwitching = true;
            Dispatcher.UIThread.RunJobs();
            var overlay = window.FindControl<BranchSwitchView>("BranchSwitchOverlay")!;
            Assert.True(overlay.IsVisible);
            Assert.False(window.FindControl<DockPanel>("WorkspaceContent")!.IsEnabled);
            Assert.Single(overlay.GetVisualDescendants().OfType<GitLlama.Desktop.Controls.LlamaIcon>());
            SavePreview(window, "branch-switch-llama.png");
            vm.IsBranchSwitching = false;
            await new GitBranchService().CreateAsync(fixture.RepositoryPath, "llama-checkout");
            var transitions = new List<bool>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.IsBranchSwitching))
                    transitions.Add(vm.IsBranchSwitching);
            };
            vm.SelectedBranchLine = "llama-checkout";
            await vm.CheckoutBranchCommand.ExecuteAsync(null);
            Assert.Equal("llama-checkout", vm.CurrentBranch);
            Assert.Equal("llama-checkout", vm.BranchSwitchTarget);
            Assert.Equal(new[] { true, false }, transitions);
            Assert.False(overlay.IsVisible);
            Assert.True(vm.IsWorkspaceInteractive);

            transitions.Clear();
            vm.SelectedBranchLine = "missing-branch";
            await vm.CheckoutBranchCommand.ExecuteAsync(null);
            Assert.Equal(new[] { true, false }, transitions);
            Assert.False(vm.IsBranchSwitching);
            Assert.Contains("does not exist", vm.Status);
            Assert.True(window.FindControl<DockPanel>("WorkspaceContent")!.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LargeWorkingChangeRefreshBatchesListsAndPreservesUnchangedRows()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var rows = Enumerable.Range(0, 5000)
            .Select(i => new WorkingFileRow("ModifiedInWorkdir  " + i, $"src/File{i:D5}.cs", "ModifiedInWorkdir"))
            .ToArray();
        var updates = 0;
        vm.UnstagedFiles.CollectionChanged += (_, _) => updates++;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        CollectionUpdates.Sync(vm.WorkingFiles, rows, row => row.Path);
        elapsed.Stop();
        Console.WriteLine($"5,000 working rows: {elapsed.Elapsed.TotalMilliseconds:F0} ms; unstaged list notifications: {updates}");
        Assert.Equal(5000, vm.UnstagedFiles.Count);
        Assert.Equal(1, updates);
        updates = 0;
        var retained = vm.UnstagedFiles[2500];
        CollectionUpdates.Sync(vm.WorkingFiles, rows.Select(row => row with { }), row => row.Path);
        Assert.Equal(0, updates);
        Assert.Same(retained, vm.UnstagedFiles[2500]);
        CollectionUpdates.Sync(vm.WorkingFiles, rows.Take(2), row => row.Path);
        Assert.Equal(2, vm.UnstagedFiles.Count);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckoutPreservesFiveThousandLocalFilesAndRefreshesTheirStatus(bool committed)
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var originalBranch = vm.CurrentBranch;
        await new GitBranchService().CreateAsync(fixture.RepositoryPath, "large-checkout", checkout: committed);
        var folder = Path.Combine(fixture.RepositoryPath, "checkout-local-files");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 5000; i++)
            File.WriteAllText(Path.Combine(folder, $"File{i:D5}.txt"), "preserve local work");
        if (committed)
        {
            using (var repository = new Repository(fixture.RepositoryPath))
            {
                Commands.Stage(repository, "checkout-local-files");
                var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.UtcNow);
                repository.Commit("Add 5,000 files on target branch", signature, signature);
            }
            await new GitBranchService().CheckoutAsync(fixture.RepositoryPath, originalBranch);
        }
        vm.SelectedBranchLine = "large-checkout";
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await vm.CheckoutBranchCommand.ExecuteAsync(null);
        elapsed.Stop();
        Console.WriteLine($"Checkout and full refresh with 5,000 files (committed={committed}): {elapsed.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal("large-checkout", vm.CurrentBranch);
        Assert.Equal(committed ? 0 : 5000, vm.UnstagedFiles.Count(file => file.Path.StartsWith("checkout-local-files/")));
        Assert.Equal(5000, vm.RepositoryFiles.Count(file => file.Path.StartsWith("checkout-local-files/")));
        Assert.Equal("preserve local work", File.ReadAllText(Path.Combine(folder, "File04999.txt")));
        var overview = await new LibGitOverviewReader().ReadOverviewAsync(fixture.RepositoryPath);
        var browser = new GitFileBrowserService();
        Assert.Equal(await browser.ListAsync(fixture.RepositoryPath),
            await browser.ListAsync(fixture.RepositoryPath, overview.Changes));
    }

    [AvaloniaFact]
    public async Task WorkspaceRendersAndSearchKeepsRepositoryOpen()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.HistoryFiles.Count > 0);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(window.GetVisualDescendants().OfType<HistoryView>());
            Assert.NotEmpty(window.GetVisualDescendants().OfType<InspectorView>());
            Assert.NotEmpty(window.GetVisualDescendants().OfType<NavigationView>());
            Assert.All(
                window
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .Where(button => button.IsVisible && button.Classes.Contains("window-control")),
                button => Assert.Equal(new Thickness(0), button.Padding)
            );
            SavePreview(window, "history.png");
            window.Width = 1080;
            window.Height = 640;
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "compact.png");
            window.Width = 1500;
            window.Height = 920;

            var selectedRepository = vm.SelectedRepository;
            vm.RepositorySearch = "no matching repository";
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.FilteredRepositories);
            Assert.Equal(selectedRepository, vm.SelectedRepository);
            vm.RepositorySearch = "";
            vm.HistorySearch = "authentication";
            Assert.NotEmpty(vm.HistoryRows);
            Assert.All(
                vm.HistoryRows,
                row => Assert.Contains("authentication", row.Subject, StringComparison.OrdinalIgnoreCase)
            );
            Assert.All(vm.HistoryRows, row => Assert.Empty(row.Segments));
            vm.HistorySearch = "no matching commit";
            Assert.True(vm.HasNoHistory);
            Assert.Empty(vm.HistoryFiles);
            vm.HistorySearch = "";
            Assert.Contains(vm.HistoryRows, row => row.Segments.Count > 0);

            vm.SelectedRepository = null;
            Assert.Empty(vm.HistoryRows);
            Assert.Empty(vm.HistoryFiles);
            Assert.Empty(vm.BranchLines);
            Assert.False(vm.CommitCommand.CanExecute(null));
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "empty.png");
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task StagingAndCommitUseTheSelectedFileAndRetainWorkingInspector()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.CommitMessage = "Review authentication changes";
        vm.CommitDescriptionText = "Commit only the staged authentication file.";
        Assert.False(vm.CommitCommand.CanExecute(null));
        vm.SelectedChangeLine = vm.ChangeLines.First(line => line.Contains("Auth.cs"));
        await vm.StageSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.StagedFileCount);
        Assert.True(vm.IsWorkingTreeInspector);
        Assert.Contains("Auth.cs", vm.SelectedChangeLine);
        Assert.True(vm.CommitCommand.CanExecute(null));
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "working-changes.png");
            await vm.CommitCommand.ExecuteAsync(null);
            Assert.Equal("", vm.CommitMessage);
            Assert.False(vm.CommitCommand.CanExecute(null));
            using var repository = new Repository(fixture.RepositoryPath);
            Assert.Equal("Review authentication changes", repository.Head.Tip.MessageShort);
            Assert.Contains("Commit only the staged authentication file.", repository.Head.Tip.Message);
            Assert.Contains(repository.RetrieveStatus(), entry => entry.FilePath == "README.md");
            Assert.DoesNotContain(repository.RetrieveStatus(), entry => entry.FilePath == "src/Auth.cs");
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [Fact]
    public void MergeGraphKeepsParallelLanesAndConnectsBothParents()
    {
        GitCommitNode Node(string sha, params string[] parents) =>
            new(sha, sha, "Author", DateTimeOffset.UtcNow, parents, []);
        var rows = HistoryRow.Layout([
            Node("merge", "main", "feature"),
            Node("main", "root"),
            Node("feature", "root"),
            Node("root"),
        ]);
        Assert.NotEqual(rows[1].Lane, rows[2].Lane);
        Assert.Equal(2, rows[0].Segments.Count);
        Assert.Equal(2, rows[3].Segments.Count);
        Assert.All(rows, row => Assert.True(row.GraphWidth >= 84));
        Assert.Empty(HistoryRow.Layout([]));
    }

    [AvaloniaFact]
    public async Task GraphHighlightsCheckedOutLocalBranchAfterSwitchAndRefresh()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        using var repo = new Repository(fixture.RepositoryPath);
        var initial = Assert.Single(vm.HistoryRows, row => row.IsCurrentBranchTip);
        Assert.Equal(repo.Head.Tip.Sha, initial.Sha);
        Assert.True(initial.ReferenceLabels.First().IsCurrentBranch);
        Assert.Equal("✓ " + repo.Head.FriendlyName, initial.ReferenceLabels.First().DisplayName);
        vm.SelectedHistoryRow = vm.HistoryRows.Last();
        Assert.True(initial.IsCurrentBranchTip);
        File.WriteAllText(
            Path.Combine(fixture.RepositoryPath, "src/Auth.cs"),
            repo.Head.Tip["src/Auth.cs"].Target is Blob blob ? blob.GetContentText() : ""
        );
        File.WriteAllText(
            Path.Combine(fixture.RepositoryPath, "README.md"),
            ((Blob)repo.Head.Tip["README.md"].Target).GetContentText()
        );
        Commands.Checkout(repo, "feature/authentication");
        await vm.RefreshCommand.ExecuteAsync(null);
        var current = Assert.Single(vm.HistoryRows, row => row.IsCurrentBranchTip);
        Assert.Equal("feature/authentication", vm.CurrentBranch);
        Assert.Equal(repo.Head.Tip.Sha, current.Sha);
        Assert.DoesNotContain(
            vm.HistoryRows.SelectMany(row => row.ReferenceLabels),
            label => label.Name == initial.CurrentLocalBranch && label.IsCurrentBranch
        );
        vm.HistorySearch = current.Subject;
        Assert.True(Assert.Single(vm.HistoryRows, row => row.IsCurrentBranchTip).IsCurrentBranchTip);
        vm.HistorySearch = "";
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            window.ShowDocument("History");
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "checked-out-branch.png");
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }

        Commands.Checkout(repo, repo.Head.Tip);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.HistoryRows, row => row.IsCurrentBranchTip);
        Assert.Contains(vm.HistoryRows.Single(row => row.Sha == repo.Head.Tip.Sha).Labels, label => label == "HEAD");
    }

    [AvaloniaFact]
    public async Task ConflictedMergeIsSurfacedAndCanBeResolvedAndFinished()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        await WaitUntilAsync(() => vm.HistoryRows.Count > 0);
        string current;
        using (var repository = new Repository(fixture.RepositoryPath))
        {
            repository.Reset(ResetMode.Hard);
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.UtcNow);
            var file = Path.Combine(fixture.RepositoryPath, "conflict.txt");
            current = repository.Head.FriendlyName;
            repository.CreateBranch("incoming");
            File.WriteAllText(file, "ours\n");
            Commands.Stage(repository, "conflict.txt");
            repository.Commit("ours", signature, signature);
            Commands.Checkout(repository, repository.Branches["incoming"]);
            File.WriteAllText(file, "theirs\n");
            Commands.Stage(repository, "conflict.txt");
            repository.Commit("theirs", signature, signature);
            Commands.Checkout(repository, repository.Branches[current]);
        }

        Assert.Equal(MergeStatus.Conflicts, await new GitMergeService().MergeAsync(fixture.RepositoryPath, "incoming"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsMergeInProgress);
        Assert.Equal(1, vm.ConflictedFileCount);
        Assert.True(vm.ShowMergeBanner);
        Assert.Equal("Merge conflicts (1) · Resolve", vm.WorkingChangesCaption);
        Assert.False(vm.ContinueMergeCommand.CanExecute(null));

        // The older Conflicts tab still lays out without a stray branch list over it.
        var view = new BranchesView { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            vm.MergeTabIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex);
            Assert.Empty(view.GetVisualDescendants().OfType<ListBox>());
        }
        finally
        {
            window.Close();
        }

        // Resolve conflicts opens the first conflicted file in the resolver.
        await vm.OpenConflictResolutionCommand.ExecuteAsync(null);
        Assert.True(vm.IsConflictResolver);
        Assert.Equal("conflict.txt", vm.ConflictResolver?.Path);
        await vm.UseTheirsForConflictCommand.ExecuteAsync(null);
        Assert.False(vm.IsConflictResolver);
        Assert.False(vm.HasMergeConflicts);
        Assert.True(vm.CanFinishMerge);
        Assert.Equal("Merge ready to finish · View changes", vm.WorkingChangesCaption);

        vm.AuthorName = "Merge Author";
        vm.AuthorEmail = "merge@example.invalid";
        await vm.ContinueMergeCommand.ExecuteAsync(null);
        Assert.False(vm.ShowMergeBanner);
        using (var repository = new Repository(fixture.RepositoryPath))
        {
            Assert.Equal(CurrentOperation.None, repository.Info.CurrentOperation);
            Assert.Equal(2, repository.Head.Tip.Parents.Count());
            Assert.Equal("merge@example.invalid", repository.Head.Tip.Author.Email);
            Assert.Contains("incoming", repository.Head.Tip.MessageShort);
            // Using a whole side writes the file as checkout would, so line endings follow core.autocrlf.
            Assert.Equal(
                "theirs\n",
                File.ReadAllText(Path.Combine(fixture.RepositoryPath, "conflict.txt")).Replace("\r\n", "\n")
            );
        }
    }

    [AvaloniaFact]
    public async Task StashIsOnlyAvailableWhenChangesAreStaged()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        using var repository = new Repository(fixture.RepositoryPath);
        repository.Reset(ResetMode.Hard);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.SaveStagedStashCommand.CanExecute(null));
        Assert.Equal("There are no changes to stash.", vm.StashBlockedReason);

        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "notes.txt"), "new");
        File.AppendAllText(Path.Combine(fixture.RepositoryPath, "src", "Auth.cs"), "// unstaged edit");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.SaveStagedStashCommand.CanExecute(null));
        Assert.Equal("Only staged changes are stashed. Stage the changes you want to stash.", vm.StashBlockedReason);

        Commands.Stage(repository, "notes.txt");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.SaveStagedStashCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ConflictResolverResolvesFilesOneAfterAnother()
    {
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            repo.Reset(ResetMode.Hard);
            var sig = new Signature("Robert Fryer", "robert@example.com", DateTimeOffset.UtcNow);
            void Write(string a, string b)
            {
                File.WriteAllText(Path.Combine(fixture.RepositoryPath, "a.txt"), a);
                File.WriteAllText(Path.Combine(fixture.RepositoryPath, "b.txt"), b);
            }
            Write("top\nbase\nend\n", "base\n");
            Commands.Stage(repo, "*");
            repo.Commit("base", sig, sig);
            var source = repo.Head.FriendlyName;
            repo.CreateBranch("incoming");
            Write("top\nours\nend\n", "ours\n");
            Commands.Stage(repo, "*");
            repo.Commit("ours", sig, sig);
            Commands.Checkout(repo, repo.Branches["incoming"]);
            Write("top\ntheirs\nend\n", "theirs\n");
            Commands.Stage(repo, "*");
            repo.Commit("theirs", sig, sig);
            Commands.Checkout(repo, repo.Branches[source]);
        }

        await new GitMergeService().MergeAsync(fixture.RepositoryPath, "incoming");
        var vm = await fixture.OpenAsync();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsConflictInspector);
        Assert.Equal(["a.txt", "b.txt"], vm.ConflictedFiles.Select(file => file.Path));

        await vm.OpenConflictResolutionCommand.ExecuteAsync(null);
        Assert.True(vm.IsConflictResolver);
        var resolver = vm.ConflictResolver!;
        Assert.Equal("a.txt", resolver.Path);
        Assert.Equal("incoming", resolver.TheirsName);
        // Lines go into the output in the order they were ticked: B first, then A.
        resolver.Chunks[0].TakeTheirs = true;
        resolver.Chunks[0].TakeOurs = true;
        var chosenOutput = resolver.OutputText;
        vm.CloseConflictResolverCommand.Execute(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.OpenConflictFileCommand.ExecuteAsync("a.txt");
        Assert.Same(resolver, vm.ConflictResolver);
        Assert.True(resolver.Chunks[0].TakeTheirs);
        Assert.True(resolver.Chunks[0].TakeOurs);
        Assert.Equal(chosenOutput, resolver.OutputText);
        await vm.OpenConflictFileCommand.ExecuteAsync("b.txt");
        var otherDraft = vm.ConflictResolver!;
        otherDraft.Chunks[0].TakeOurs = true;
        otherDraft.OutputText = "manual draft for b";
        otherDraft.IsEditingOutput = true;
        await vm.OpenConflictFileCommand.ExecuteAsync("a.txt");
        Assert.Same(resolver, vm.ConflictResolver);
        await vm.SaveConflictResolutionCommand.ExecuteAsync(null);

        Assert.Equal(
            "top\ntheirs\nours\nend\n",
            File.ReadAllText(Path.Combine(fixture.RepositoryPath, "a.txt")).Replace("\r\n", "\n")
        );
        Assert.Equal("b.txt", vm.ConflictResolver?.Path);
        Assert.Same(otherDraft, vm.ConflictResolver);
        Assert.Equal("manual draft for b", otherDraft.OutputText);
        Assert.True(otherDraft.OutputEdited);
        Assert.True(otherDraft.IsEditingOutput);
        Assert.Equal(["b.txt"], vm.ConflictedFiles.Select(file => file.Path));
        Assert.Contains(vm.ResolvedFiles, file => file.Path == "a.txt");

        await vm.UseTheirsForConflictCommand.ExecuteAsync(null);
        Assert.False(vm.IsConflictResolver);
        Assert.Empty(vm.ConflictedFiles);
        Assert.True(vm.CanFinishMerge);
        Assert.Equal("theirs\n", File.ReadAllText(Path.Combine(fixture.RepositoryPath, "b.txt")).Replace("\r\n", "\n"));
    }

    [AvaloniaFact]
    public async Task DiscardFromTheFileMenuRemovesTheFileFromUnstaged()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        // Viewing the file first keeps its diff loading in the background, as it does when clicked in the app.
        vm.SelectedWorkingFile = vm.WorkingFiles.Single(file => file.Path == "README.md");
        Assert.True(vm.IsFileReview);

        await vm.DiscardFileAsync(vm.WorkingFiles.Single(file => file.Path == "README.md"));

        // The file has no changes left, so the centre goes back to the history graph.
        Assert.False(vm.IsFileReview);
        Assert.Equal("History", vm.CenterSection);
        Assert.Null(vm.SelectedWorkingFile);
        Assert.DoesNotContain(vm.UnstagedFiles, file => file.Path == "README.md");
        Assert.Contains(vm.UnstagedFiles, file => file.Path == "src/Auth.cs");
        using var repository = new Repository(fixture.RepositoryPath);
        Assert.Equal(FileStatus.Unaltered, repository.RetrieveStatus("README.md"));
    }

    [AvaloniaFact]
    public async Task WorkingFileMenuOffersFileActions()
    {
        using var fixture = new WorkspaceFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "notes.draft"), "new");
        var vm = await fixture.OpenAsync();
        await vm.RefreshCommand.ExecuteAsync(null);
        var host = new Window();
        host.Show();
        try
        {
            string[] Headers(object[] items) => items.OfType<MenuItem>().Select(item => (string)item.Header!).ToArray();

            var readme = vm.WorkingFiles.Single(file => file.Path == "README.md");
            Assert.Equal(
                [
                    "Stage",
                    "Discard changes…",
                    "Ignore",
                    "Stash file",
                    "File history",
                    "File blame",
                    "Open file",
                    "Show in folder",
                    "Copy file path",
                    "Copy full path",
                    "Create patch from file changes…",
                    "Delete file…",
                ],
                Headers(WorkingFileMenu.Items(host, vm, readme, inStagedList: false))
            );
            Assert.Equal("Unstage", Headers(WorkingFileMenu.Items(host, vm, readme, inStagedList: true))[0]);
            var log = vm.WorkingFiles.Single(file => file.Path == "notes.draft");
            var logMenu = WorkingFileMenu.Items(host, vm, log, inStagedList: false);
            Assert.Equal("Discard new file…", Headers(logMenu)[1]);
            Assert.Equal(
                ["Ignore this file", "Ignore all .draft files"],
                (
                    (IEnumerable<object>)
                        logMenu.OfType<MenuItem>().Single(item => (string)item.Header! == "Ignore").ItemsSource!
                )
                    .OfType<MenuItem>()
                    .Select(item => (string)item.Header!)
            );
        }
        finally
        {
            host.Close();
        }

        await vm.IgnoreAsync("*.draft", tracked: false);
        Assert.DoesNotContain(vm.WorkingFiles, file => file.Path == "notes.draft");
        Assert.Contains("*.draft", File.ReadAllText(Path.Combine(fixture.RepositoryPath, ".gitignore")));

        await vm.StashFileAsync("README.md");
        Assert.DoesNotContain(vm.WorkingFiles, file => file.Path == "README.md");
        Assert.Contains(vm.WorkingFiles, file => file.Path == "src/Auth.cs");
        Assert.EndsWith(": README.md", vm.Stashes.Single().Message);

        await vm.DeleteFileAsync("src/Auth.cs");
        Assert.False(File.Exists(Path.Combine(fixture.RepositoryPath, "src", "Auth.cs")));
        Assert.Equal("Deleted", vm.WorkingFiles.Single(file => file.Path == "src/Auth.cs").Status);
        Assert.Contains("deleted file", await vm.FilePatchAsync("src/Auth.cs", staged: false));
    }

    [AvaloniaFact]
    public async Task ToolbarShowsAndChangesWhereTheBranchPushesAndPulls()
    {
        using var fixture = new WorkspaceFixture();
        var server = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "server.git");
        Repository.Init(server, isBare: true);
        using (var repository = new Repository(fixture.RepositoryPath))
        {
            repository.Network.Remotes.Add("origin", server);
        }

        var vm = await fixture.OpenAsync();
        await vm.RefreshCommand.ExecuteAsync(null);
        var branch = vm.CurrentBranch;
        Assert.Null(vm.Upstream);
        Assert.Equal("Not set", vm.UpstreamCaption);
        Assert.Equal($"Push {branch} to origin/{branch}", vm.PushToolTip);
        Assert.Equal("Fetch origin", vm.FetchToolTip);

        // The first push creates the branch on origin and tracks it.
        await vm.PushToUpstreamCommand.ExecuteAsync(null);
        Assert.Equal($"origin/{branch}", vm.UpstreamCaption);
        Assert.True(vm.Upstream!.Published);
        Assert.Equal($"Pull origin/{branch} into {branch} (fast-forward only)", vm.PullToolTip);

        // Choosing a branch that isn't on the server yet: Pull explains, Push creates it.
        await vm.SetUpstreamToAsync("origin", "shared/work");
        Assert.Equal("origin/shared/work", vm.UpstreamCaption);
        Assert.Equal("new", vm.UpstreamCounts);
        Assert.Equal($"Push {branch} to origin/shared/work", vm.PushToolTip);
        await vm.PullFastForwardCommand.ExecuteAsync(null);
        Assert.Contains("isn't on the server yet", vm.Status);
        await vm.PushToUpstreamCommand.ExecuteAsync(null);
        using (var bare = new Repository(server))
        {
            Assert.NotNull(bare.Branches["shared/work"]);
        }

        Assert.True(vm.Upstream!.Published);
        Assert.Contains("shared/work", vm.RemoteBranchesOf("origin"));

        var host = new Window();
        host.Show();
        try
        {
            var menu = UpstreamMenu.Items(host, vm);
            var origin = menu.OfType<MenuItem>().Single(item => ((string)item.Header!).EndsWith("origin"));
            Assert.Contains(
                ((IEnumerable<object>)origin.ItemsSource!).OfType<MenuItem>(),
                item => (string)item.Header! == "✓  origin/shared/work"
            );
            Assert.Contains(menu.OfType<MenuItem>(), item => (string)item.Header! == "Stop tracking");
        }
        finally
        {
            host.Close();
        }

        await vm.StopTrackingAsync();
        Assert.Null(vm.Upstream);
        Assert.Equal("Not set", vm.UpstreamCaption);
    }

    [AvaloniaFact]
    public async Task DeletedFilesCountAsStagedAndCanBeCommitted()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        File.Delete(Path.Combine(fixture.RepositoryPath, "README.md"));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedWorkingFile = vm.WorkingFiles.First(file => file.Path == "README.md");
        await vm.StageSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.StagedFileCount);
        Assert.Equal("Deleted", vm.SelectedWorkingFile!.Status);
        Assert.True(vm.SelectedWorkingFile.IsStaged);
        vm.CommitMessage = "Remove outdated readme";
        Assert.True(vm.CommitCommand.CanExecute(null));
        await vm.CommitCommand.ExecuteAsync(null);
        using var repository = new Repository(fixture.RepositoryPath);
        Assert.Null(repository.Head.Tip["README.md"]);
        Assert.Contains(repository.RetrieveStatus(), entry => entry.FilePath == "src/Auth.cs");
    }

    [AvaloniaFact]
    public async Task FavoritingDoesNotClearCommitDraftOrSelection()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.CommitMessage = "Draft in progress";
        var selectedSha = vm.SelectedHistoryRow?.Sha;
        await vm.ToggleFavoriteCommand.ExecuteAsync(null);
        Assert.True(vm.SelectedRepository!.IsFavorite);
        Assert.Equal("Draft in progress", vm.CommitMessage);
        Assert.Equal(selectedSha, vm.SelectedHistoryRow?.Sha);
        Assert.Equal("Robert Fryer", vm.AuthorName);
    }

    [AvaloniaFact]
    public async Task EveryWorkflowTabRendersAndCredentialsSurviveRefreshWithoutBeingSaved()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            repo.Network.Remotes.Add("origin", "https://github.com/example/client.git");
        }

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.NetworkSecret = "session-only-password";
        vm.GithubToken = "session-only-github-token";
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("session-only-password", vm.NetworkSecret);
        Assert.Equal("example", vm.GithubOwner);
        await vm.ToggleFavoriteCommand.ExecuteAsync(null);
        var settings = File.ReadAllText(Path.Combine(fixture.SettingsPath, "settings.json"));
        Assert.DoesNotContain("session-only", settings);
        Assert.False((await new JsonSettingsStore(fixture.SettingsPath).LoadAsync()).AutoRefresh);
        vm.Status = "password=session-only-password";
        vm.RefreshDiagnosticsCommand.Execute(null);
        Assert.DoesNotContain("session-only-password", vm.DiagnosticsText);
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            foreach (
                var tab in new[]
                {
                    "Files",
                    "Branches",
                    "Tags",
                    "Stashes",
                    "Remotes",
                    "Repository",
                    "Worktrees",
                    "Reflog",
                    "Submodules",
                    "Pull Requests",
                    "Issues",
                    "Settings",
                }
            )
            {
                window.ShowDocument(tab);
                Dispatcher.UIThread.RunJobs();
                if (tab == "Repository")
                {
                    Assert.True(vm.IsRepositoryManagerSelected);
                }
                else if (tab == "Settings")
                {
                    Assert.True(vm.IsSettingsOpen);
                }
                else
                {
                    Assert.Equal(tab, vm.SelectedTab);
                }

                SavePreview(window, tab.Replace(' ', '-').ToLowerInvariant() + ".png");
            }

            Assert.NotEmpty(window.GetVisualDescendants().OfType<SettingsView>());
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task HostingCommandsHandleReviewMergeIssuesAndOfflineState()
    {
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath)) repo.Network.Remotes.Add("origin", "https://github.com/owner/repo.git");
        var provider = new FakeHostingProvider();
        var vm = await fixture.OpenAsync(provider);
        await WaitUntilAsync(() => vm.Remotes.Count == 1);
        vm.GithubOwner = "owner";
        vm.GithubRepository = "repo";
        vm.GithubToken = "session-token";
        await vm.RefreshPullRequestsCommand.ExecuteAsync(null);
        vm.SelectedPullRequest = Assert.Single(vm.PullRequests);
        Assert.Single(vm.HostingComments);
        vm.HostingComment = "Approved changes";
        await vm.ApprovePullRequestCommand.ExecuteAsync(null);
        Assert.Equal("APPROVE", provider.LastReview);
        vm.SelectedPullRequest = vm.PullRequests.First();
        await vm.MergePullRequestCommand.ExecuteAsync(null);
        Assert.Null(provider.MergedSha);
        vm.RequestConfirmation = _ => Task.FromResult(true);
        await vm.MergePullRequestCommand.ExecuteAsync(null);
        Assert.Equal("reviewed-sha", provider.MergedSha);
        await vm.RefreshIssuesCommand.ExecuteAsync(null);
        vm.SelectedIssue = Assert.Single(vm.Issues);
        await vm.ToggleIssueStateCommand.ExecuteAsync(null);
        Assert.Equal("closed", provider.IssueState);
        provider.Offline = true;
        await vm.RefreshIssuesCommand.ExecuteAsync(null);
        Assert.Single(vm.Issues);
        Assert.Contains("Could not reach GitHub", vm.HostingStatus);
    }

    [AvaloniaFact]
    public async Task AmendLoadsTheExistingMessageAndCanReuseItWhenBlank()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        string oldMessage, oldSha;
        int oldCount;
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            oldMessage = repo.Head.Tip.Message;
            oldSha = repo.Head.Tip.Sha;
            oldCount = repo.Commits.Count();
        }
        await vm.StageAllCommand.ExecuteAsync(null);
        Assert.True(vm.StagedFileCount > 0);
        Assert.False(vm.CommitCommand.CanExecute(null));
        vm.AmendStagedCommit = true;
        Assert.Equal(oldMessage, vm.CommitMessage);
        Assert.True(vm.CommitCommand.CanExecute(null));
        vm.CommitMessage = "";
        Assert.True(vm.CommitCommand.CanExecute(null));
        await vm.CommitCommand.ExecuteAsync(null);
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            Assert.NotEqual(oldSha, repo.Head.Tip.Sha);
            Assert.Equal(oldCount, repo.Commits.Count());
            Assert.Equal(oldMessage.Trim(), repo.Head.Tip.Message.Trim());
            Assert.Empty(repo.RetrieveStatus());
        }
        Assert.StartsWith("Amended commit", vm.Status);
        Assert.False(vm.AmendStagedCommit);
        Assert.Equal("", vm.CommitMessage);
    }

    [AvaloniaFact]
    public async Task SelectedLineCommandAndAutomaticRefreshReachRealGitState()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.SelectedWorkingFile = vm.WorkingFiles.First(file => file.Path == "src/Auth.cs");
        await WaitUntilAsync(() => vm.PatchLines.Any(line => line.IsChange));
        vm.SelectedPatchLine = vm.PatchLines.Last(line => line.Kind == "Added");
        await vm.ApplySelectedLineCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.StagedFileCount);
        vm.StartRepositoryMonitoring();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "watched.txt"), "external edit");
        await WaitUntilAsync(() => vm.WorkingFiles.Any(file => file.Path == "watched.txt"));
        vm.StopRepositoryMonitoring();
        await vm.StageAllCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.StagedFileCount);
        vm.CommitMessage = "Amended authentication";
        vm.AmendStagedCommit = true;
        int originalCount;
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            originalCount = repo.Commits.Count();
        }

        await vm.CommitCommand.ExecuteAsync(null);
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            Assert.Equal(originalCount, repo.Commits.Count());
        }

        Assert.False(vm.AmendStagedCommit);
    }

    [AvaloniaFact]
    public async Task BackgroundRefreshKeepsHoveredHistoryRowsAndScrollStable()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var selected = vm.HistoryRows[^2];
        vm.SelectedHistoryRow = selected;
        var view = new HistoryView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 330 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("HistoryList")!;
            list.ScrollIntoView(selected);
            Dispatcher.UIThread.RunJobs();
            var row = list.ContainerFromItem(selected)!;
            Assert.NotNull(row);
            var point = row.TranslatePoint(new Point(100, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
            Assert.True(row.IsPointerOver);
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            var offset = scroll.Offset;
            Assert.True(offset.Y > 0);
            var notifications = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            vm.HistoryRows.CollectionChanged += (_, e) => notifications.Add(e.Action);
            for (var i = 0; i < 3; i++)
            {
                await vm.RefreshCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(selected, vm.SelectedHistoryRow);
                Assert.Same(row, list.ContainerFromItem(selected));
                Assert.True(row.IsPointerOver);
                Assert.Equal(offset, scroll.Offset);
            }
            Assert.Empty(notifications);
            using (var repo = new Repository(fixture.RepositoryPath))
                repo.Tags.Add("hover-test", selected.Sha);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Contains("tag:hover-test", vm.HistoryRows.Single(item => item.Sha == selected.Sha).Labels);
            using (var repo = new Repository(fixture.RepositoryPath))
            {
                File.WriteAllText(Path.Combine(fixture.RepositoryPath, "external.txt"), "new commit");
                Commands.Stage(repo, "external.txt");
                var author = new Signature("Test", "test@example.invalid", DateTimeOffset.UtcNow);
                repo.Commit("External update", author, author);
            }
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Contains(vm.HistoryRows, item => item.Subject == "External update");
            Assert.Equal(selected.Sha, vm.SelectedHistoryRow!.Sha);
            Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, notifications);
            vm.HistorySearch = selected.Subject;
            Assert.All(vm.HistoryRows, item => Assert.Empty(item.Segments));
            SavePreview(window, "stable-history-hover.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HistoryClickDuringActivationRefreshKeepsTheClickedCommit()
    {
        var reader = new PausableOverviewReader();
        using var fixture = new WorkspaceFixture(reader);
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.SelectedHistoryRow = vm.HistoryRows[0];
        var target = vm.HistoryRows[3];
        var view = new HistoryView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 600 };
        var other = new Window { Content = new TextBox(), Width = 200, Height = 100 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("HistoryList")!;
            var row = list.ContainerFromItem(target)!;
            var point = row.TranslatePoint(new Point(100, row.Bounds.Height / 2), window)!.Value;
            other.Show();
            ((TextBox)other.Content!).Focus();
            reader.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refresh = vm.RefreshCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => reader.Paused);
            window.Activate();
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Assert.Equal(target.Sha, vm.SelectedHistoryRow!.Sha);
            reader.Release.SetResult();
            await refresh;
            Assert.Equal(target.Sha, vm.SelectedHistoryRow!.Sha);
            Assert.Equal(target.Subject, vm.HistorySubject);
            await WaitUntilAsync(() => vm.HistoryCommitDetails.Contains(target.Sha, StringComparison.Ordinal));
            Assert.Same(vm.SelectedHistoryRow, list.SelectedItem);
        }
        finally
        {
            reader.Release?.TrySetResult();
            other.Close();
            window.Close();
        }
    }

    private sealed class PausableOverviewReader : IRepositoryOverviewReader
    {
        public TaskCompletionSource? Release { get; set; }
        public bool Paused { get; private set; }
        public async Task<RepositoryOverview> ReadOverviewAsync(string path, CancellationToken cancellationToken = default)
        {
            if (Release is { } gate)
            {
                Paused = true;
                await gate.Task.WaitAsync(cancellationToken);
            }
            return await new LibGitOverviewReader().ReadOverviewAsync(path, cancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task RefreshUpdatesBranchAndHistoryFileSelectionLoadsThePatch()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        await WaitUntilAsync(() => vm.HistoryFileRows.Count > 0);
        vm.SelectedHistoryFileRow = vm.HistoryFileRows.First();
        await WaitUntilAsync(() => vm.HistoryFilePreview.Contains("diff --git", StringComparison.Ordinal));
        Assert.Equal(vm.SelectedHistoryFileRow.Path, vm.FileHistoryPath);
        Assert.Equal("No upstream", vm.UpstreamStatus);
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            repo.Branches.Rename(repo.Head, "main");
        }

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("main", vm.CurrentBranch);
        Assert.Contains("main", vm.BranchLines);
    }

    [AvaloniaFact]
    public async Task FloatingHistoryReturnsAndMissingDocumentHostRecovers()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.HistoryFiles.Count > 0);
            var control = window.FindControl<Dock.Avalonia.Controls.DockControl>("WorkspaceDock")!;
            var factory = control.Factory!;
            var documents = window.EnsureDocuments();
            var history = documents.VisibleDockables!.Single(d => d.Id == "history");
            factory.SplitToWindow(documents, history, 10, 10, 600, 400);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(documents.VisibleDockables!, d => d.Id == "history");
            window.ShowDocument("History");
            Assert.Contains(window.EnsureDocuments().VisibleDockables!, d => ReferenceEquals(d, history));
            Assert.Single(MainWindow.WalkDockables(control.Layout), d => d.Id == "history");
            documents = window.EnsureDocuments();
            factory.RemoveDockable(documents, true);
            window.ShowDocument("History");
            Assert.Equal("history", window.EnsureDocuments().ActiveDockable!.Id);
            window.ShowDocument("Changes");
            Assert.True(vm.IsWorkingTreeInspector);
            window.ShowDocument("History");
            Assert.False(vm.IsWorkingTreeInspector);
            var inspector = window.GetVisualDescendants().OfType<InspectorView>().First();
            Assert.Empty(inspector.GetVisualDescendants().OfType<TabControl>());
            Assert.DoesNotContain(MainWindow.WalkDockables(control.Layout), d => d.Id == "output");
            Assert.Equal(SystemDecorations.None, window.SystemDecorations);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task DoubleClickBranchChecksOutClickedRowAndPreservesLocalEdits()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.NewBranchName = "double-click-branch";
        await vm.CreateBranchCommand.ExecuteAsync(null);
        var path = Path.Combine(fixture.RepositoryPath, "src/Auth.cs");
        var edits = File.ReadAllText(path);
        var original = vm.CurrentBranch;
        var view = new NavigationView { DataContext = vm };
        var window = new Window
        {
            Content = view,
            Width = 360,
            Height = 850,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = view.GetVisualDescendants().OfType<ListBox>().First();
            var row = list.GetVisualDescendants()
                .OfType<ListBoxItem>()
                .Single(item => item.DataContext as string == "double-click-branch");
            var point = row.TranslatePoint(new Point(40, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Assert.Equal(original, vm.CurrentBranch);
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            await WaitUntilAsync(() => vm.Status == "Switched to double-click-branch");
            Assert.Equal("double-click-branch", vm.CurrentBranch);
            using var repo = new Repository(fixture.RepositoryPath);
            Assert.Equal("double-click-branch", repo.Head.FriendlyName);
            Assert.Equal(edits, File.ReadAllText(path));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SidebarContextActionsUseSelectedReferenceAndKeepWorkingFiles()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        var target = vm.HistoryRows.Last().Sha;
        var current = vm.CurrentBranch;
        vm.ContextBranchReference = target;
        vm.NewBranchName = "context-branch";
        await vm.CreateContextBranchCommand.ExecuteAsync(null);
        vm.ContextTagTarget = target;
        vm.NewTagName = "context-tag";
        await vm.CreateContextTagCommand.ExecuteAsync(null);
        using var repo = new Repository(fixture.RepositoryPath);
        Assert.Equal(target, repo.Branches["context-branch"].Tip.Sha);
        Assert.Equal(target, repo.Tags["context-tag"].Target.Sha);
        Assert.Equal(current, repo.Head.FriendlyName);
        var view = new NavigationView { DataContext = vm };
        var labels = ((IEnumerable<MenuItem>)view.BuildBranchMenu("context-branch").ItemsSource!)
            .Select(item => item.Header!.ToString())
            .ToArray();
        Assert.Contains("Checkout context-branch", labels);
        Assert.Contains("Rename branch…", labels);
        Assert.Contains("Delete merged branch…", labels);
        Assert.Contains("Show branch history", labels);
    }

    [AvaloniaFact]
    public async Task WorkingChangesStayInInspectorAndObsoleteLayoutDocumentsAreRemoved()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.HistoryFiles.Count > 0);
            Assert.Single(window.EnsureDocuments().VisibleDockables!);
            window.ShowDocument("Changes");
            Assert.True(vm.IsWorkingTreeInspector);
            Assert.Equal("history", window.EnsureDocuments().ActiveDockable!.Id);
            Assert.DoesNotContain("Changes", vm.AvailableTabs);
            vm.SelectedWorkingFile = vm.WorkingFiles.First();
            await WaitUntilAsync(() => vm.UnstagedDiffText.Contains("@@"));
            Dispatcher.UIThread.RunJobs();
            var inspector = window.GetVisualDescendants().OfType<InspectorView>().Single();
            Assert.Empty(inspector.GetVisualDescendants().OfType<GitLlama.Desktop.Controls.PatchView>());
            Assert.NotEmpty(window.GetVisualDescendants().OfType<FileReviewView>());
            Assert.Empty(inspector.GetVisualDescendants().OfType<TabControl>());
            SavePreview(window, "inspector-working-changes.png");
            var layout = (Dock.Model.Core.IDock)
                window.FindControl<Dock.Avalonia.Controls.DockControl>("WorkspaceDock")!.Layout!;
            var documents = window.EnsureDocuments();
            var obsolete = new Dock.Model.Mvvm.Controls.Document { Id = "changes", Title = "Changes" };
            documents.VisibleDockables!.Add(obsolete);
            documents.ActiveDockable = obsolete;
            MainWindow.RemoveChangesDocuments(layout);
            Assert.DoesNotContain(MainWindow.WalkDockables(layout), d => d.Id == "changes");
            Assert.Equal("history", documents.ActiveDockable!.Id);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task CenterSwitchesBetweenCommitDiffFileAndWorkingStagingWithoutDocuments()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.HistoryFileRows.Count > 0);
            vm.SelectedHistoryFileRow = vm.HistoryFileRows.First();
            await WaitUntilAsync(() => vm.CenterPatch.Contains("@@"));
            Assert.True(vm.IsFileReview);
            Assert.Equal("Commit", vm.ReviewSource);
            vm.ReviewFileCommand.Execute(null);
            await WaitUntilAsync(() => vm.CenterContent.Contains("// Add unit tests"));
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.IsFileReview);
            Assert.Equal("File", vm.FileReviewMode);
            await WaitUntilAsync(() => vm.CenterContent.Contains("// Add unit tests"));
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "center-historical-file.png");
            vm.CloseFileReviewCommand.Execute(null);
            Assert.False(vm.IsFileReview);
            vm.SelectedWorkingFile = vm.WorkingFiles.Single(file => file.Path == "src/Auth.cs");
            await WaitUntilAsync(() => vm.CenterPatch.Contains("+// Validate login input"));
            Assert.True(vm.IsWorkingFileReview);
            vm.ReviewFileCommand.Execute(null);
            await WaitUntilAsync(() => vm.CenterContent.Contains("// Validate login input"));
            Assert.False(vm.ApplySelectedLineCommand.CanExecute(null));
            Assert.False(vm.ApplySelectedHunkCommand.CanExecute(null));
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "center-working-file.png");
            vm.ReviewDiffCommand.Execute(null);
            vm.SelectedPatchLine = vm.PatchLines.First(line => line.Kind == "Hunk");
            Assert.False(vm.ApplySelectedLineCommand.CanExecute(null));
            Assert.True(vm.ApplySelectedHunkCommand.CanExecute(null));
            vm.SelectedPatchLine = GitPartialStageService
                .Lines(vm.CenterPatch)
                .Single(line => line.Text.Contains("+// Validate login input"));
            Assert.True(vm.ApplySelectedLineCommand.CanExecute(null));
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "accent-selected-file-actions.png");
            await vm.ApplySelectedLineCommand.ExecuteAsync(null);
            Assert.Equal(1, vm.StagedFileCount);
            Assert.True(vm.IsWorkingTreeInspector);
            Assert.True(vm.IsFileReview);
            Assert.Single(window.EnsureDocuments().VisibleDockables!);
            vm.ReviewStagedPatch = true;
            Assert.Contains("+// Validate login input", vm.CenterPatch);
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "center-staged-diff.png");
            window.ShowDocument("History");
            Assert.False(vm.IsFileReview);
            window.ShowDocument("Issues");
            Assert.Equal("Issues", vm.CenterSection);
            Assert.Single(window.EnsureDocuments().VisibleDockables!);
            vm.SelectedRepository = null;
            Assert.False(vm.IsFileReview);
            Assert.Equal("History", vm.CenterSection);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task RepositoryTabsSwitchAndClosingInactiveTabKeepsCurrentDraft()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var original = vm.SelectedRepository!;
        var otherPath = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "Other");
        Repository.Init(otherPath);
        vm.RepositoryPath = otherPath;
        await vm.AddRepositoryCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.SelectedRepository?.Path == otherPath && vm.CurrentBranch != "No repository");
        var other = vm.SelectedRepository!;
        vm.SelectedRepository = original;
        await WaitUntilAsync(() => vm.HistoryRows.Count > 0 && vm.AuthorName == "Robert Fryer");
        vm.CommitMessage = "Preserve this draft";
        await vm.CloseRepositoryTabCommand.ExecuteAsync(other);
        Assert.Equal(original, vm.SelectedRepository);
        Assert.Equal("Preserve this draft", vm.CommitMessage);
        Assert.Contains(other, vm.Repositories);
        Assert.DoesNotContain(vm.RepositoryWorkspaceTabs, tab => tab.Repository == other);
        Assert.True(Directory.Exists(otherPath));
    }

    [AvaloniaFact]
    public async Task GroupedFileCheckboxesStageAndUnstageRealIndexChanges()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.HistoryFileRows.Count > 0);
            vm.ShowWorkingTreeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var stage = window
                .GetVisualDescendants()
                .OfType<CheckBox>()
                .Single(box =>
                    box.Tag as string == "stage" && box.DataContext is WorkingFileRow { Path: "src/Auth.cs" }
                );
            stage.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() =>
                vm.StagedFileCount == 1 && vm.StagedFiles.Any(file => file.Path == "src/Auth.cs")
            );
            Assert.DoesNotContain(vm.UnstagedFiles, file => file.Path == "src/Auth.cs");
            await WaitUntilAsync(() => vm.ReviewStagedPatch && vm.CenterPatch.Contains("+// Validate login input"));
            vm.CommitSummaryText = "Improve authentication";
            vm.CommitDescriptionText = "Validate login input before authentication.";
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "modern-commit-workspace.png");
            var unstage = window
                .GetVisualDescendants()
                .OfType<CheckBox>()
                .Single(box =>
                    box.Tag as string == "unstage" && box.DataContext is WorkingFileRow { Path: "src/Auth.cs" }
                );
            unstage.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() =>
                vm.StagedFileCount == 0 && vm.UnstagedFiles.Any(file => file.Path == "src/Auth.cs")
            );
            using var repo = new Repository(fixture.RepositoryPath);
            Assert.True(repo.RetrieveStatus("src/Auth.cs").HasFlag(FileStatus.ModifiedInWorkdir));
            Assert.False(repo.RetrieveStatus("src/Auth.cs").HasFlag(FileStatus.ModifiedInIndex));
            Assert.Equal("Improve authentication", vm.CommitSummaryText);
            Assert.Equal("Validate login input before authentication.", vm.CommitDescriptionText);
            var addedPath = Path.Combine(fixture.RepositoryPath, "new-file.txt");
            File.WriteAllText(addedPath, "staged addition\n");
            await new GitWorkingTreeService().StageAsync(fixture.RepositoryPath, "new-file.txt");
            File.AppendAllText(addedPath, "unstaged modification\n");
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("Added", vm.StagedFiles.Single(file => file.Path == "new-file.txt").StagedStatus);
            Assert.Equal("Modified", vm.UnstagedFiles.Single(file => file.Path == "new-file.txt").UnstagedStatus);
            File.Delete(addedPath);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("Added", vm.StagedFiles.Single(file => file.Path == "new-file.txt").StagedStatus);
            Assert.Equal("Deleted", vm.UnstagedFiles.Single(file => file.Path == "new-file.txt").UnstagedStatus);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task RefreshNeverNavigatesAwayOrRecreatesContextViews()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        var transitions = new List<string>();
        System.ComponentModel.PropertyChangedEventHandler observer = (_, args) =>
        {
            if (
                args.PropertyName
                is nameof(vm.IsFileReview)
                    or nameof(vm.CenterSection)
                    or nameof(vm.ReviewSource)
                    or nameof(vm.FileReviewMode)
            )
            {
                transitions.Add(args.PropertyName!);
            }
        };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.HistoryFileRows.Count > 0);
            Dispatcher.UIThread.RunJobs();
            var center = window.GetVisualDescendants().OfType<ContextCenterView>().Single();
            var history = center.Content;
            vm.SelectedHistoryFileRow = vm.HistoryFileRows.First();
            vm.ReviewFileCommand.Execute(null);
            await WaitUntilAsync(() => vm.CenterContent.Contains("// Add unit tests"));
            Dispatcher.UIThread.RunJobs();
            var review = center.Content;
            vm.PropertyChanged += observer;
            for (var i = 0; i < 3; i++)
            {
                await vm.RefreshCommand.ExecuteAsync(null);
            }

            Dispatcher.UIThread.RunJobs();
            Assert.Empty(transitions);
            Assert.Same(review, center.Content);
            vm.PropertyChanged -= observer;
            window.ShowDocument("History");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(history, center.Content);
            window.ShowDocument("Files");
            Dispatcher.UIThread.RunJobs();
            var files = center.Content;
            vm.SelectedRepositoryFile = vm.RepositoryFiles.First(file => file.Path == "README.md");
            await WaitUntilAsync(() => vm.CenterContent.Contains("Initial commit"));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(review, center.Content);
            transitions.Clear();
            vm.PropertyChanged += observer;
            await vm.RefreshCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(transitions);
            Assert.Same(review, center.Content);
            vm.PropertyChanged -= observer;
            vm.CloseFileReviewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(files, center.Content);
            vm.SelectedWorkingFile = vm.WorkingFiles.First(file => file.Path == "src/Auth.cs");
            vm.ReviewFileCommand.Execute(null);
            await WaitUntilAsync(() => vm.CenterContent.Contains("Validate login input"));
            transitions.Clear();
            vm.PropertyChanged += observer;
            File.AppendAllText(Path.Combine(fixture.RepositoryPath, "src/Auth.cs"), "// external refresh edit\n");
            await vm.RefreshCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(transitions);
            Assert.Same(review, center.Content);
            Assert.Contains("external refresh edit", vm.CenterContent);
            SavePreview(window, "stable-charcoal-workspace.png");
        }
        finally
        {
            vm.PropertyChanged -= observer;
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task RepositoryPlusOpensManagerAndDiffUsesCompactSourceRows()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.HistoryFileRows.Count > 0);
            Dispatcher.UIThread.RunJobs();
            var plus = window.FindControl<Button>("ManageRepositoriesButton")!;
            Assert.True(plus.TranslatePoint(new Point(), window)!.Value.X < window.Width / 2);
            plus.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsRepositoryManagerSelected);
            var manager = window.GetVisualDescendants().OfType<RepositoryView>().Single();
            Assert.Contains(
                manager.GetVisualDescendants().OfType<Button>(),
                button => button.Content as string == "Browse for a repository…"
            );
            Assert.Contains(
                manager.GetVisualDescendants().OfType<Button>(),
                button => button.Content as string == "Initialize repository"
            );
            Assert.Contains(
                manager.GetVisualDescendants().OfType<Button>(),
                button => button.Content as string == "Clone repository"
            );
            SavePreview(window, "repository-manager.png");
            var open = manager.GetVisualDescendants().OfType<Button>().Single(button => button.Tag is RepositoryInfo);
            open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("History", vm.CenterSection);
            const string patch =
                "diff --git a/Test.cs b/Test.cs\nindex 000..111 100644\n--- a/Test.cs\n+++ b/Test.cs\n@@ -1 +1 @@\n-public string Name = \"old\";\n+public string Name = \"new\";\n";
            var visible = GitLlama.Desktop.Controls.PatchView.DisplayLines(patch);
            Assert.DoesNotContain(visible, line => line.Kind == "Metadata");
            Assert.Equal(
                GitPartialStageService.Lines(patch).Single(line => line.Kind == "Added").Index,
                visible.Single(line => line.Kind == "Added").Index
            );
            var viewer = new GitLlama.Desktop.Controls.PatchView
            {
                Patch = patch,
                FilePath = "Test.cs",
                Height = 160,
                Width = 700,
            };
            var previewWindow = new Window
            {
                Width = 740,
                Height = 210,
                Content = viewer,
            };
            try
            {
                previewWindow.Show();
                await WaitUntilAsync(() =>
                {
                    using var frame = previewWindow.CaptureRenderedFrame();
                    return frame is not null;
                });
                Dispatcher.UIThread.RunJobs();
                Assert.True(viewer.Editor.IsReadOnly);
                Assert.True(viewer.Editor.TextArea.TextView.VisualLinesValid);
                Assert.Contains(viewer.Editor.TextArea.TextView.LineTransformers,
                    transformer => transformer is GitLlama.Desktop.Controls.SourceLineColorizer);
                Assert.Contains(GitLlama.Desktop.Controls.PatchView.HighlightDiff(visible, "Test.cs").Values,
                    line => line.Spans.Any(span => line.Text.Substring(span.Start, span.Length) == "public"));
                SavePreview(previewWindow, "compact-code-diff.png");
            }
            finally
            {
                previewWindow.Close();
            }
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task ManagerTabFillsWorkspaceAndRestoresExistingReviewWithoutLayoutChanges()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.HistoryFileRows.Count > 0);
            vm.SelectedWorkingFile = vm.WorkingFiles.First(file => file.Path == "src/Auth.cs");
            vm.ReviewFileCommand.Execute(null);
            await WaitUntilAsync(() => vm.CenterContent.Contains("Validate login input"));
            Dispatcher.UIThread.RunJobs();
            var dock = window.FindControl<Dock.Avalonia.Controls.DockControl>("WorkspaceDock")!;
            var layout = dock.Layout;
            var review = window.GetVisualDescendants().OfType<ContextCenterView>().Single().Content;
            vm.CommitMessage = "Keep the current draft";
            window
                .FindControl<Button>("ManageRepositoriesButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsRepositoryManagerOpen);
            Assert.True(vm.IsRepositoryManagerSelected);
            Assert.False(dock.IsVisible);
            Assert.True(vm.SelectedRepositoryWorkspaceTab!.IsEmpty);
            var manager = window.FindControl<RepositoryView>("RepositoryManager")!;
            Assert.True(manager.IsVisible);
            Assert.True(manager.Bounds.Width > window.Bounds.Width * .9);
            SavePreview(window, "maximized-repository-manager.png");
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.IsRepositoryManagerSelected);
            window.SelectRepositoryTab(vm.SelectedRepository!);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsRepositoryManagerSelected);
            Assert.True(dock.IsVisible);
            Assert.True(vm.IsRepositoryManagerOpen);
            Assert.Same(layout, dock.Layout);
            Assert.Same(review, window.GetVisualDescendants().OfType<ContextCenterView>().Single().Content);
            Assert.Equal("File", vm.FileReviewMode);
            Assert.Equal("Keep the current draft", vm.CommitMessage);
            window.ShowDocument("Repository");
            vm.CloseWorkspaceTab(vm.SelectedRepositoryWorkspaceTab!);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsRepositoryManagerOpen);
            Assert.False(vm.IsRepositoryManagerSelected);
            Assert.True(dock.IsVisible);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task EmptyTabBecomesInitializedOrClonedRepositoryAndDialogsCollectDetails()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Thickness(1), window.FindControl<Border>("WindowFrame")!.BorderThickness);
            Assert.Equal("GitLlama", window.Title);
            Assert.NotNull(window.Icon);
            var handles = window.FindControl<Grid>("ResizeHandles")!;
            Assert.Equal(
                Enum.GetNames<WindowEdge>().Order(),
                handles.Children.Cast<Border>().Select(handle => (string)handle.Tag!).Order()
            );
            Assert.All(handles.Children.Cast<Border>(), handle => Assert.NotNull(handle.Cursor));
            Assert.True(window.CanResize);
            Assert.True(handles.IsVisible);
            window.WindowState = WindowState.Maximized;
            Assert.False(handles.IsVisible);
            window.WindowState = WindowState.Normal;
            Assert.True(handles.IsVisible);
            window.CanResize = false;
            Assert.False(handles.IsVisible);
            window.CanResize = true;
            Assert.Contains(window.GetVisualDescendants(), control => control is GitLlama.Desktop.Controls.LlamaIcon);
            Assert.Equal(new Thickness(0), window.FindControl<Border>("WindowFrame")!.Margin);
            Assert.Null(window.FindControl<TextBox>("GlobalCommitSearch"));
            Assert.IsType<Avalonia.Controls.Shapes.Path>(window.FindControl<Button>("WindowCloseButton")!.Content);
            Assert.Contains("repository-action", window.FindControl<Button>("FetchButton")!.Classes);
            Assert.Contains("repository-action", window.FindControl<Button>("RefreshButton")!.Classes);
            vm.NewRepositoryTab();
            var empty = vm.SelectedRepositoryWorkspaceTab!;
            var count = vm.RepositoryWorkspaceTabs.Count;
            var destination = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "Initialized");
            var dialog = new RepositoryCreationDialog(vm, false);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<StackPanel>("CloneFields")!.IsVisible);
            vm.NewRepositoryDestination = destination;
            SavePreview(dialog, "initialize-dialog.png");
            dialog
                .FindControl<Button>("Submit")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() =>
                !vm.IsFeatureBusy && vm.SelectedRepository?.Path == destination && !dialog.IsVisible
            );
            Assert.Same(empty, vm.SelectedRepositoryWorkspaceTab);
            Assert.Equal(count, vm.RepositoryWorkspaceTabs.Count);
            Assert.Equal("Initialized", empty.Title);
            Assert.False(vm.IsRepositoryManagerSelected);
            Assert.True(window.FindControl<Dock.Avalonia.Controls.DockControl>("WorkspaceDock")!.IsVisible);
            Assert.False(dialog.IsVisible);
            vm.NewRepositoryTab();
            empty = vm.SelectedRepositoryWorkspaceTab!;
            count = vm.RepositoryWorkspaceTabs.Count;
            var clone = new RepositoryCreationDialog(vm, true);
            clone.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(clone.FindControl<StackPanel>("CloneFields")!.IsVisible);
            vm.CloneUrl = fixture.RepositoryPath;
            vm.NewRepositoryDestination = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "Cloned");
            SavePreview(clone, "clone-dialog.png");
            clone
                .FindControl<Button>("Submit")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() =>
                !vm.IsNetworkBusy && vm.SelectedRepository?.Name == "Cloned" && !clone.IsVisible
            );
            Assert.Same(empty, vm.SelectedRepositoryWorkspaceTab);
            Assert.Equal(count, vm.RepositoryWorkspaceTabs.Count);
            Assert.Equal("Cloned", empty.Title);
            Assert.False(vm.IsRepositoryManagerSelected);
            Assert.False(clone.IsVisible);
            await WaitUntilAsync(() => vm.HistoryRows.Count > 0);
            SavePreview(window, "repository-tab-replaced.png");
            vm.NewRepositoryTab();
            empty = vm.SelectedRepositoryWorkspaceTab!;
            vm.OpenRepositoryTab(vm.Repositories.First(repo => repo.Path == fixture.RepositoryPath));
            Assert.DoesNotContain(empty, vm.RepositoryWorkspaceTabs);
            Assert.Single(vm.RepositoryWorkspaceTabs, tab => tab.Repository?.Path == fixture.RepositoryPath);
            vm.NewRepositoryTab();
            empty = vm.SelectedRepositoryWorkspaceTab!;
            var failed = new RepositoryCreationDialog(vm, false);
            failed.Show();
            Dispatcher.UIThread.RunJobs();
            vm.NewRepositoryDestination = fixture.RepositoryPath;
            void RefreshStatus(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(vm.IsFeatureBusy) && !vm.IsFeatureBusy)
                    vm.Status = "Repository refresh complete.";
            }
            vm.PropertyChanged += RefreshStatus;
            failed
                .FindControl<Button>("Submit")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() =>
                failed.FindControl<TextBlock>("Feedback")!.Text?.Contains("already a repository") == true,
                () => $"Feedback: {failed.FindControl<TextBlock>("Feedback")!.Text}; creation: {vm.RepositoryCreationFeedback}; status: {vm.Status}; destination: {vm.NewRepositoryDestination}; busy: {vm.IsFeatureBusy}"
            );
            vm.PropertyChanged -= RefreshStatus;
            Assert.True(empty.IsEmpty);
            Assert.True(vm.IsRepositoryManagerSelected);
            Assert.True(failed.IsVisible);
            failed.Close();
            await vm.ToggleFavoriteCommand.ExecuteAsync(null);
            using var sessions = new RepositorySessionManager(new LibGitRepositoryReader(), new LibGitOverviewReader());
            using var restored = new MainWindowViewModel(
                sessions,
                new JsonSettingsStore(fixture.SettingsPath),
                new GitWorkingTreeService()
            );
            await restored.Initialization;
            Assert.True(restored.IsRepositoryManagerSelected);
            Assert.Equal(
                vm.RepositoryWorkspaceTabs.Select(tab => tab.Title),
                restored.RepositoryWorkspaceTabs.Select(tab => tab.Title)
            );
            window.WindowState = WindowState.Maximized;
            Assert.Equal(new Thickness(0), window.FindControl<Border>("WindowFrame")!.Margin);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task ClickingSelectedBranchReturnsToGraphLoadsOlderTipAndScrollsToIt()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            repo.CreateBranch("archived", repo.Commits.Last());
            for (var i = 0; i < 220; i++)
            {
                var signature = new Signature(
                    "Robert Fryer",
                    "robert@example.com",
                    DateTimeOffset.UtcNow.AddSeconds(i)
                );
                repo.Commit("Recent commit " + i, signature, signature, new CommitOptions { AllowEmptyCommit = true });
            }
        }

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.HistoryRows, row => row.Labels.Contains("archived"));
        var original = vm.CurrentBranch;
        vm.SelectedBranchLine = "archived";
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            vm.HistorySearch = "no matching commit";
            vm.SelectedWorkingFile = vm.WorkingFiles.First();
            Assert.True(vm.IsFileReview);
            var navigation = window.GetVisualDescendants().OfType<NavigationView>().Single();
            var branches = navigation.GetVisualDescendants().OfType<ListBox>().First();
            var branch = branches
                .GetVisualDescendants()
                .OfType<ListBoxItem>()
                .Single(item => item.DataContext as string == "archived");
            var point = branch.TranslatePoint(new Point(40, branch.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            await WaitUntilAsync(() => vm.SelectedHistoryRow?.Labels.Contains("archived") == true);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Assert.False(vm.IsFileReview);
            Assert.False(vm.IsWorkingTreeInspector);
            Assert.Equal("History", vm.CenterSection);
            Assert.Equal("All branches", vm.SelectedHistoryScope);
            Assert.Empty(vm.HistorySearch);
            Assert.Equal(original, vm.CurrentBranch);
            var history = window
                .GetVisualDescendants()
                .OfType<HistoryView>()
                .Single()
                .FindControl<ListBox>("HistoryList")!;
            await WaitUntilAsync(() => history.ContainerFromItem(vm.SelectedHistoryRow!) is not null);
            var row = history.ContainerFromItem(vm.SelectedHistoryRow!)!;
            Assert.InRange(row.TranslatePoint(new Point(), history)!.Value.Y, 0, history.Bounds.Height);
            SavePreview(window, "branch-tip-revealed.png");
            window.ShowDocument("Files");
            await vm.RevealBranchHistoryCommand.ExecuteAsync("archived");
            Assert.Equal("History", vm.CenterSection);
            Assert.Equal(original, vm.CurrentBranch);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    private static async Task AssertClipboardTextAsync(Avalonia.Input.Platform.IClipboard clipboard, string expected)
    {
        string? copied = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            copied = (await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard))?.Replace("\r\n", "\n");
            if (copied == expected) break;
            await Task.Delay(10);
        }
        Assert.Equal(expected, copied);
    }

    [AvaloniaTheory]
    [InlineData(Avalonia.Input.KeyModifiers.Control)]
    [InlineData(Avalonia.Input.KeyModifiers.Meta)]
    public async Task ReadOnlyCopySupportsControlAndCommand(Avalonia.Input.KeyModifiers commandModifier)
    {
        var copyGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.C, commandModifier);
        var selectAllGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.A, commandModifier);
        var editor = GitLlama.Desktop.Controls.ReadOnlySourceEditor.Create([copyGesture], [selectAllGesture]);
        editor.Text = "first line\nsecond line";
        var window = new Window { Width = 600, Height = 250, Content = editor };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            editor.TextArea.Focus();
            var clipboard = TopLevel.GetTopLevel(editor)!.Clipboard!;
            await clipboard.ClearAsync();
            editor.Select(0, 10);
            PressShortcut(window, copyGesture, Avalonia.Input.PhysicalKey.C);
            await AssertClipboardTextAsync(clipboard, "first line");
            PressShortcut(window, selectAllGesture, Avalonia.Input.PhysicalKey.A);
            Assert.Equal(editor.Text, editor.SelectedText);
            Assert.True(editor.IsReadOnly);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceViewersSelectAndCopyAcrossLinesButRejectEdits(bool diff)
    {
        const string patch = "diff --git a/Test.cs b/Test.cs\n--- a/Test.cs\n+++ b/Test.cs\n@@ -1,2 +1,2 @@\n-old value\n+new value\n unchanged\n";
        var code = new GitLlama.Desktop.Controls.CodeView { FilePath = "Test.cs", Text = "old value\nnew value\nunchanged" };
        var changes = new GitLlama.Desktop.Controls.PatchView { FilePath = "Test.cs", Patch = patch };
        var editor = diff ? changes.Editor : code.Editor;
        var window = new Window { Width = 850, Height = 300, Content = diff ? changes : code };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            editor.TextArea.Focus();
            Assert.True(editor.IsReadOnly);
            var original = editor.Text;
            var first = editor.Document.GetLineByNumber(diff ? 2 : 1);
            var last = editor.Document.GetLineByNumber(diff ? 3 : 2);
            var view = editor.TextArea.TextView;
            var start = view.TranslatePoint(view.GetVisualPosition(
                new AvaloniaEdit.TextViewPosition(first.LineNumber, 1), AvaloniaEdit.Rendering.VisualYPosition.LineMiddle)
                - view.ScrollOffset, window)!.Value;
            var end = view.TranslatePoint(view.GetVisualPosition(
                new AvaloniaEdit.TextViewPosition(last.LineNumber, last.Length + 1), AvaloniaEdit.Rendering.VisualYPosition.LineMiddle)
                - view.ScrollOffset, window)!.Value;
            window.MouseDown(start, Avalonia.Input.MouseButton.Left);
            window.MouseMove(end, Avalonia.Input.RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, Avalonia.Input.MouseButton.Left);
            Assert.Equal("old value\nnew value", editor.SelectedText);
            var hotkeys = Avalonia.Application.Current!.PlatformSettings!.HotkeyConfiguration;
            var copyGesture = hotkeys.Copy.First(gesture => gesture.Key == Avalonia.Input.Key.C);
            PressShortcut(window, copyGesture, Avalonia.Input.PhysicalKey.C);
            Assert.Equal(hotkeys.Copy.First(), editor.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy")).InputGesture);
            var clipboard = TopLevel.GetTopLevel(editor)!.Clipboard!;
            await AssertClipboardTextAsync(clipboard, "old value\nnew value");
            window.KeyTextInput("replaced");
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Delete, Avalonia.Input.RawInputModifiers.None);
            window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Delete, Avalonia.Input.RawInputModifiers.None);
            PressShortcut(window, hotkeys.Paste.First(gesture => gesture.Key == Avalonia.Input.Key.V), Avalonia.Input.PhysicalKey.V);
            await Task.Yield();
            Assert.Equal(original, editor.Text);
            PressShortcut(window, hotkeys.SelectAll.First(gesture => gesture.Key == Avalonia.Input.Key.A), Avalonia.Input.PhysicalKey.A);
            Assert.Equal(original, editor.SelectedText);
            if (diff)
            {
                editor.TextArea.Caret.Line = 3;
                Assert.Equal("Added", changes.SelectedLine?.Kind);
                changes.SelectedLine = GitLlama.Desktop.Controls.PatchView.DisplayLines(patch).Single(line => line.Kind == "Deleted");
                Assert.Equal(2, editor.TextArea.Caret.Line);
            }
            SavePreview(window, diff ? "selectable-diff.png" : "selectable-file.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LibraryHighlightingHandlesMultilineLanguagesAndIndependentDiffSides()
    {
        var code =
            "/* comment begins\npublic FakeType() stays a comment\n*/\npublic sealed record Customer\n{\n    public required string Name { get; init; }\n    public string Format() => $\"Hello {Name}\";\n    public string Message = \"\"\"\nreturn is string content\n\"\"\";\n}";
        var lines = GitLlama.Desktop.Controls.SourceHighlighter.Highlight(code, "Customer.cs");
        Assert.All(lines[1].Spans, span => Assert.Equal("#8EAD70", span.Color));
        Assert.Contains(
            lines[3].Spans,
            span => lines[3].Text.Substring(span.Start, span.Length) == "record" && span.Color == "#80B8E0"
        );
        Assert.Contains(
            lines[3].Spans,
            span => lines[3].Text.Substring(span.Start, span.Length) == "Customer" && span.Color == "#4EC9B0"
        );
        Assert.Contains(
            lines[6].Spans,
            span => lines[6].Text.Substring(span.Start, span.Length) == "Format" && span.Color == "#DCDCAA"
        );
        Assert.All(lines[8].Spans, span => Assert.Equal("#D69D85", span.Color));
        foreach (
            var (path, source) in new[]
            {
                ("config.json", "{\"enabled\": true, \"count\": 42}"),
                ("View.axaml", "<TextBlock Text=\"Hello\"/>"),
                ("script.py", "def greet(name): # a comment\n    return \"hello\""),
                ("app.js", "function greet(name) { return 'hello'; }"),
            }
        )
        {
            Assert.Contains(
                GitLlama.Desktop.Controls.SourceHighlighter.Highlight(source, path),
                line => line.Spans.Count > 0
            );
        }

        var plain = GitLlama.Desktop.Controls.SourceHighlighter.Highlight("public is ordinary text", "notes.unknown");
        Assert.Empty(plain[0].Spans);
        const string patch =
            "diff --git a/Test.cs b/Test.cs\n--- a/Test.cs\n+++ b/Test.cs\n@@ -1,2 +1,2 @@\n-/* old comment\n-old continuation */\n+public record Customer;\n+return Customer;\n";
        var diff = GitLlama.Desktop.Controls.PatchView.DisplayLines(patch);
        var highlighted = GitLlama.Desktop.Controls.PatchView.HighlightDiff(diff, "Test.cs");
        var deleted = diff.Single(line => line.Text == "-old continuation */");
        Assert.All(highlighted[deleted.Index].Spans, span => Assert.Equal("#8EAD70", span.Color));
        var added = diff.Single(line => line.Text == "+public record Customer;");
        Assert.Contains(highlighted[added.Index].Spans, span => span.Color == "#4EC9B0");
        var window = new Window
        {
            Width = 1000,
            Height = 560,
            Content = new GitLlama.Desktop.Controls.CodeView { FilePath = "Customer.cs", Text = code },
        };
        try
        {
            window.Show();
            await WaitUntilAsync(() => window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>()
                .Any(editor => editor.TextArea.TextView.VisualLinesValid));
            SavePreview(window, "library-syntax-highlighting.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SettingsOverlayPreservesReviewAndPersistsOnlyAuthenticationPreferences()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.HistoryFileRows.Count > 0);
            vm.SelectedWorkingFile = vm.WorkingFiles.First();
            vm.CommitMessage = "Keep this draft";
            await WaitUntilAsync(() => vm.CenterPatch.Contains("@@"));
            Dispatcher.UIThread.RunJobs();
            var review = window.GetVisualDescendants().OfType<ContextCenterView>().Single().Content;
            window.ShowDocument("Settings");
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsSettingsOpen);
            Assert.True(vm.IsFileReview);
            Assert.Same(review, window.GetVisualDescendants().OfType<ContextCenterView>().Single().Content);
            foreach (var section in vm.SettingsSections)
            {
                vm.SelectedSettingsSection = section;
                Dispatcher.UIThread.RunJobs();
                SavePreview(window, "settings-" + section.Replace(' ', '-').ToLowerInvariant() + ".png");
            }

            vm.NetworkSecret = "session-test-secret";
            vm.GithubToken = "account-test-secret";
            vm.UseCredentialManager = false;
            vm.UseSshAgent = false;
            vm.SshPrivateKeyPath = "C:/keys/id_ed25519";
            await vm.ToggleFavoriteCommand.ExecuteAsync(null);
            var saved = File.ReadAllText(Path.Combine(fixture.SettingsPath, "settings.json"));
            Assert.DoesNotContain("session-test-secret", saved);
            Assert.DoesNotContain("account-test-secret", saved);
            var settings = await new JsonSettingsStore(fixture.SettingsPath).LoadAsync();
            Assert.False(settings.UseCredentialManager);
            Assert.False(settings.UseSshAgent);
            Assert.Equal(vm.SshPrivateKeyPath, settings.SshPrivateKeyPath);
            vm.CloseSettingsCommand.Execute(null);
            Assert.False(vm.IsSettingsOpen);
            Assert.Equal("Keep this draft", vm.CommitMessage);
            Assert.True(vm.IsFileReview);
            vm.ClearSessionCredentialsCommand.Execute(null);
            Assert.Empty(vm.NetworkSecret);
            Assert.Equal("account-test-secret", vm.GithubToken);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task InspectorReservesScrollbarSpaceAndKeepsUnchangedFileRowsDuringRefresh()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            for (var i = 0; i < 40; i++)
            {
                var path = "src/LongDirectoryName/ChangedFileWithALongName" + i + ".cs";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.RepositoryPath, path))!);
                File.WriteAllText(Path.Combine(fixture.RepositoryPath, path), "public class Changed {}\n");
                Commands.Stage(repo, path);
            }

            var author = new Signature("Robert Fryer", "robert@example.com", DateTimeOffset.UtcNow);
            repo.Commit("Many changed files", author, author);
        }

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedHistoryRow = vm.HistoryRows.First();
        await WaitUntilAsync(() => vm.HistoryFileRows.Count == 40);
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var original = vm.HistoryFileRows.ToArray();
            var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            vm.HistoryFileRows.CollectionChanged += (_, e) => events.Add(e.Action);
            await vm.RefreshCommand.ExecuteAsync(null);
            await Task.Delay(150);
            Assert.Empty(events);
            Assert.Equal(original.Length, vm.HistoryFileRows.Count);
            Assert.Same(original[0], vm.HistoryFileRows[0]);
            var inspector = window.GetVisualDescendants().OfType<InspectorView>().Single();
            var files = inspector
                .GetVisualDescendants()
                .OfType<ListBox>()
                .Single(list => ReferenceEquals(list.ItemsSource, vm.HistoryFileRows));
            Assert.False(ScrollViewer.GetAllowAutoHide(files));
            SavePreview(window, "changed-files-scrollbar.png");
            vm.ShowWorkingTreeCommand.Execute(null);
            var working = vm.WorkingFiles.ToArray();
            var unchanged = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            vm.UnstagedFiles.CollectionChanged += (_, e) => unchanged.Add(e.Action);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Empty(unchanged);
            Assert.Same(working[0], vm.WorkingFiles[0]);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task SettingsCogAndSshKeyFieldsLoadPublicKeyWithoutExposingPrivateMaterial()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var directory = Path.GetDirectoryName(fixture.RepositoryPath)!;
        var privatePath = Path.Combine(directory, "id_ed25519");
        File.WriteAllText(
            privatePath,
            "-----BEGIN OPENSSH PRIVATE KEY-----\nprivate-material\n-----END OPENSSH PRIVATE KEY-----"
        );
        var keyData = new byte[4 + 11 + 4 + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(keyData, 11);
        System.Text.Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(keyData, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(keyData.AsSpan(15), 32);
        var publicText = "ssh-ed25519 " + Convert.ToBase64String(keyData) + " gitllama-test";
        File.WriteAllText(privatePath + ".pub", publicText);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<Avalonia.Controls.Shapes.Path>(window.FindControl<Button>("SettingsButton")!.Content);
            Assert.Equal("Settings…", window.FindControl<MenuItem>("FileSettingsMenuItem")!.Header);
            vm.SshPrivateKeyPath = privatePath;
            await WaitUntilAsync(() => vm.HasLoadedPublicKey);
            Assert.Equal(privatePath + ".pub", vm.SshPublicKeyPath);
            Assert.Equal(publicText, vm.SshPublicKeyText);
            Assert.Contains("SHA256:", vm.SshKeyFeedback);
            window.ShowDocument("Settings");
            vm.SelectedSettingsSection = "SSH";
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Assert.True(vm.UseSshAgent);
            Assert.True(view.FindControl<TextBox>("SshPrivateKeyInput")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("VerifyPuttyHostButton")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("LoadPageantKeyButton")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<TextBox>("SshPublicKeyInput")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("CopyPublicKeyButton")!.IsEnabled);
            SavePreview(window, "ssh-key-loading.png");
            await vm.ToggleFavoriteCommand.ExecuteAsync(null);
            var saved = await new JsonSettingsStore(fixture.SettingsPath).LoadAsync();
            Assert.Equal(privatePath, saved.SshPrivateKeyPath);
            Assert.Equal(privatePath + ".pub", saved.SshPublicKeyPath);
            var puttyPublicPath = Path.Combine(directory, "putty-public.pub");
            File.WriteAllText(puttyPublicPath,
                "---- BEGIN SSH2 PUBLIC KEY ----\nComment: \"PuTTY public key\"\n"
                + Convert.ToBase64String(keyData) + "\n---- END SSH2 PUBLIC KEY ----");
            vm.SshPublicKeyPath = puttyPublicPath;
            await WaitUntilAsync(() => vm.HasLoadedPublicKey);
            Assert.Equal("ssh-ed25519 " + Convert.ToBase64String(keyData), vm.SshPublicKeyText);
            Assert.Contains("SHA256:", vm.SshKeyFeedback);
            vm.SshPublicKeyPath = privatePath;
            await WaitUntilAsync(() => vm.SshKeyFeedback.Contains("private key"));
            Assert.Empty(vm.SshPublicKeyText);
            Assert.False(vm.HasLoadedPublicKey);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ProfilesMigrateExistingConnectionPreferences()
    {
        using var fixture = new WorkspaceFixture();
        await new JsonSettingsStore(fixture.SettingsPath).SaveAsync(
            new GitLlamaSettings(
                [],
                null,
                UseCredentialManager: false,
                UseSshAgent: false,
                SshPrivateKeyPath: "existing-private",
                SshPublicKeyPath: "existing-public"
            )
        );
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        Assert.Single(vm.Profiles);
        Assert.Equal("Default", vm.ActiveProfileName);
        Assert.False(vm.UseCredentialManager);
        Assert.False(vm.UseSshAgent);
        Assert.Equal("existing-private", vm.SshPrivateKeyPath);
        Assert.Equal("existing-public", vm.SshPublicKeyPath);
        await vm.ToggleFavoriteCommand.ExecuteAsync(null);
        var saved = await new JsonSettingsStore(fixture.SettingsPath).LoadAsync();
        Assert.Single(saved.Profiles!);
        Assert.Equal(vm.ActiveProfile!.Id, saved.ActiveProfileId);
    }

    [AvaloniaFact]
    public async Task SettingsSeparateProfileNamesCommitIdentityAndAdvancedCredentials()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        Assert.Equal("Integrations", vm.SelectedSettingsSection);
        Assert.Contains("Commit identity", vm.SettingsSections);
        Assert.Contains("Git connections", vm.SettingsSections);
        Assert.DoesNotContain("Authentication", vm.SettingsSections);
        vm.ProfileName = "Unsaved rename";
        vm.ProfileAuthorName = "Profile Author";
        vm.ProfileAuthorEmail = "";
        await vm.SaveCommitIdentityCommand.ExecuteAsync(null);
        Assert.Equal("Robert Fryer", vm.AuthorName);
        vm.ProfileAuthorEmail = "profile@example.invalid";
        await vm.SaveCommitIdentityCommand.ExecuteAsync(null);
        Assert.Equal("Default", vm.ActiveProfileName);
        Assert.Equal("Profile Author <profile@example.invalid>", vm.EffectiveCommitIdentity);
        Assert.Contains("profile identity", vm.CommitIdentitySource);
        vm.ProfileName = "Personal";
        vm.ProfileAuthorName = "Unsaved author draft";
        await vm.SaveProfileCommand.ExecuteAsync(null);
        Assert.Equal("Personal", vm.ActiveProfileName);
        Assert.Equal("Profile Author", vm.AuthorName);
        using (var repo = new Repository(fixture.RepositoryPath))
            Assert.Equal("Robert Fryer", repo.Config.Get<string>("user.name").Value);
        vm.ProfileAuthorName = vm.ProfileAuthorEmail = "";
        await vm.SaveCommitIdentityCommand.ExecuteAsync(null);
        Assert.Equal("Robert Fryer", vm.AuthorName);
        Assert.Contains("Git configuration", vm.CommitIdentitySource);

        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        try
        {
            window.Show();
            vm.OpenSettingsSectionCommand.Execute("Profiles");
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<TextBox>("ProfileIdentityNameInput")!.IsEffectivelyVisible);
            vm.OpenSettingsSectionCommand.Execute("Commit identity");
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<TextBox>("ProfileIdentityNameInput")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Expander>("RepositoryIdentityExpander")!.IsExpanded);
            SavePreview(window, "consolidated-commit-identity.png");
            vm.OpenSettingsSectionCommand.Execute("Git connections");
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<Expander>("TemporaryTokenExpander")!.IsExpanded);
            Assert.Equal('●', view.FindControl<TextBox>("TemporaryTokenInput")!.PasswordChar);
            SavePreview(window, "advanced-git-connections.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfilesSwitchIdentityAndKeysPersistAndClearSessionSecrets()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.SshPrivateKeyPath = "personal-key";
        vm.UseSshAgent = false;
        await vm.ToggleFavoriteCommand.ExecuteAsync(null);
        var personalId = vm.ActiveProfile!.Id;
        await vm.CreateProfileCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.True(vm.UseSshAgent);
        Assert.Empty(vm.SshPrivateKeyPath);
        vm.ProfileName = "Work";
        vm.ProfileAuthorName = "Work Author";
        vm.ProfileAuthorEmail = "work@example.com";
        await vm.SaveProfileCommand.ExecuteAsync(null);
        await vm.SaveCommitIdentityCommand.ExecuteAsync(null);
        vm.SshPrivateKeyPath = "work-key";
        vm.UseCredentialManager = false;
        await vm.ToggleFavoriteCommand.ExecuteAsync(null);
        Assert.Equal("Work Author", vm.AuthorName);
        Assert.Equal("work@example.com", vm.AuthorEmail);
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            Assert.Equal("Robert Fryer", repo.Config.Get<string>("user.name").Value);
        }

        await vm.StageAllCommand.ExecuteAsync(null);
        vm.CommitMessage = "Commit with work profile";
        Assert.True(vm.CommitCommand.CanExecute(null));
        await vm.CommitCommand.ExecuteAsync(null);
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            Assert.Equal("Work Author", repo.Head.Tip.Author.Name);
            Assert.Equal("work@example.com", repo.Head.Tip.Author.Email);
            Assert.Equal("Robert Fryer", repo.Config.Get<string>("user.name").Value);
        }

        vm.ProfileName = "Default";
        await vm.SaveProfileCommand.ExecuteAsync(null);
        Assert.Equal("Work", vm.ActiveProfileName);
        Assert.Contains("already in use", vm.ProfileFeedback);
        vm.ProfileName = "Work";
        vm.NetworkSecret = "profile-secret";
        vm.GithubToken = "hosting-secret";
        vm.ProfileToSwitch = vm.Profiles.Single(p => p.Id == personalId);
        vm.IsAuthenticationBusy = true;
        await vm.SwitchProfileCommand.ExecuteAsync(null);
        Assert.Equal("Work", vm.ActiveProfileName);
        vm.IsAuthenticationBusy = false;
        await vm.SwitchProfileCommand.ExecuteAsync(null);
        Assert.Equal("Default", vm.ActiveProfileName);
        Assert.Equal("personal-key", vm.SshPrivateKeyPath);
        Assert.False(vm.UseSshAgent);
        Assert.True(vm.UseCredentialManager);
        Assert.Equal("Robert Fryer", vm.AuthorName);
        Assert.Empty(vm.NetworkSecret);
        Assert.Empty(vm.GithubToken);
        vm.ProfileToSwitch = vm.Profiles.Single(p => p.Name == "Work");
        await vm.SwitchProfileCommand.ExecuteAsync(null);
        Assert.Equal("work-key", vm.SshPrivateKeyPath);
        Assert.False(vm.UseCredentialManager);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            window.ShowDocument("Settings");
            window
                .GetVisualDescendants()
                .OfType<SettingsView>()
                .Single()
                .FindControl<Button>("CurrentProfileButton")!
                .Command!.Execute(null);
            Assert.True(vm.IsProfilesSettings);
            Dispatcher.UIThread.RunJobs();
            SavePreview(window, "settings-profiles.png");
        }
        finally
        {
            window.Close();
        }

        var json = File.ReadAllText(Path.Combine(fixture.SettingsPath, "settings.json"));
        Assert.DoesNotContain("profile-secret", json);
        Assert.DoesNotContain("hosting-secret", json);
        vm.Dispose();
        var reopened = await fixture.OpenAsync(expectedAuthor: "Work Author");
        reopened.AutoRefresh = false;
        Assert.Equal("Work", reopened.ActiveProfileName);
        Assert.Equal("work-key", reopened.SshPrivateKeyPath);
        await reopened.RemoveProfileCommand.ExecuteAsync(null);
        Assert.Single(reopened.Profiles);
        Assert.Equal("Default", reopened.ActiveProfileName);
        Assert.Equal("Robert Fryer", reopened.AuthorName);
        await reopened.RemoveProfileCommand.ExecuteAsync(null);
        Assert.Single(reopened.Profiles);
    }

    [AvaloniaFact]
    public async Task RemoteTreeGroupsRemotesAndBranchFoldersAndPreservesExpansionOnRefresh()
    {
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            repo.Network.Remotes.Add("origin", "https://github.com/example/StoneForge.git");
            repo.Network.Remotes.Add("upstream", "https://github.com/example/upstream.git");
            foreach (
                var name in new[]
                {
                    "origin/main",
                    "origin/feature/auth",
                    "origin/feature/ui",
                    "origin/team/fix/login",
                    "upstream/main",
                }
            )
            {
                repo.Refs.Add("refs/remotes/" + name, repo.Head.Tip.Id);
            }

            repo.Refs.Add("refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        }

        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        Assert.Equal(2, vm.RemoteTree.Count);
        Assert.DoesNotContain("origin/HEAD", vm.BranchLines);
        Assert.DoesNotContain(
            vm.HistoryRows.SelectMany(row => row.Labels),
            label => label == "origin/HEAD" || label == "HEAD"
        );
        var origin = vm.RemoteTree.Single(n => n.Name == "origin");
        var upstream = vm.RemoteTree.Single(n => n.Name == "upstream");
        Assert.Equal("4", origin.Count);
        Assert.Equal("1", upstream.Count);
        var feature = origin.Children.Single(n => n.Name == "feature");
        Assert.Null(feature.Branch);
        Assert.Equal(new[] { "origin/feature/auth", "origin/feature/ui" }, feature.Children.Select(n => n.Branch));
        var team = origin.Children.Single(n => n.Name == "team");
        Assert.Equal("origin/team/fix/login", team.Children.Single().Children.Single().Branch);
        feature.IsExpanded = false;
        upstream.IsExpanded = false;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            await WaitUntilAsync(() => window.GetVisualDescendants().OfType<RemoteTreeView>().Any());
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<RemoteTreeView>().Single();
            Assert.Equal(
                new[]
                {
                    "Fetch origin",
                    "Push " + vm.CurrentBranch + " to origin",
                    "Create remote branch…",
                    "Edit remote URL…",
                    "Remove origin…",
                    "Copy remote URL",
                },
                view.BuildMenu(origin).Items.OfType<MenuItem>().Select(i => i.Header)
            );
            Assert.Contains(
                view.BuildMenu(feature.Children[0]).Items.OfType<MenuItem>(),
                item => Equals(item.Header, "Rename remote branch…")
            );
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Same(origin, vm.RemoteTree.Single(n => n.Name == "origin"));
            Assert.Same(feature, origin.Children.Single(n => n.Name == "feature"));
            Assert.False(feature.IsExpanded);
            Assert.False(upstream.IsExpanded);
            var leaf = origin.Children.Single(n => n.Branch == "origin/main");
            view.FindControl<TreeView>("RemoteTreeControl")!.SelectedItem = leaf;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("origin/main", vm.SelectedBranchLine);
            Assert.Equal("origin", vm.SelectedRemote!.Name);
            Assert.Equal("main", vm.UpstreamBranchName);
            SavePreview(window, "remote-tree.png");
            window.ShowDocument("Remotes");
            await WaitUntilAsync(() =>
                window
                    .GetVisualDescendants()
                    .OfType<RemotesView>()
                    .Any(remotes => remotes.GetVisualDescendants().OfType<RemoteTreeView>().Any())
            );
            Assert.NotEmpty(
                window
                    .GetVisualDescendants()
                    .OfType<RemotesView>()
                    .Single()
                    .GetVisualDescendants()
                    .OfType<RemoteTreeView>()
            );
            SavePreview(window, "remotes-panel.png");
            vm.BranchSearch = "login";
            Assert.Single(vm.RemoteTree);
            Assert.Equal("1", vm.RemoteTree[0].Count);
            Assert.Equal(
                "origin/team/fix/login",
                vm.RemoteTree[0].Children.Single().Children.Single().Children.Single().Branch
            );
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task IntegrationsOAuthRenewsAutomaticallyRestoresProfilesAndHidesSignIn()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        Assert.Contains("Integrations", vm.SettingsSections);
        Assert.DoesNotContain("GitHub", vm.SettingsSections);
        var handler = new OAuthHandler();
        using var client = new HttpClient(handler);
        vm.GithubOAuthFactory = () =>
            new GitLlama.Providers.GitHubDeviceOAuth(
                client,
                delay: (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }
            );
        vm.GithubOAuthVerify = (token, _) =>
        {
            Assert.Equal("oauth-session-secret", token);
            return Task.FromResult("LlamaUser");
        };
        Uri? opened = null;
        vm.OpenIntegrationBrowser = uri =>
        {
            opened = uri;
            return Task.FromResult(true);
        };
        await vm.SignInGithubOAuthCommand.ExecuteAsync(null);
        Assert.Equal("https://github.com/login/device", opened!.AbsoluteUri);
        Assert.Equal("Connected as LlamaUser", vm.GithubAccountStatus);
        Assert.NotNull(vm.GithubSessionExpiresAt);
        Assert.Empty(vm.GithubOAuthUserCode);
        Assert.False(vm.IsAuthenticationBusy);
        vm.NetworkCredentialHost = "github.com";
        Assert.Equal("oauth-session-secret", vm.NetworkOptions(0).Secret);
        vm.NetworkCredentialHost = "github.com.attacker.example";
        Assert.Empty(vm.NetworkOptions(0).Secret);
        vm.NetworkCredentialHost = "github.com";
        vm.NetworkSecret = "separate-host-token";
        Assert.Equal("separate-host-token", vm.NetworkOptions(0).Secret);
        vm.NetworkUsername = "temporary-user";
        vm.ClearSessionCredentialsCommand.Execute(null);
        Assert.Empty(vm.NetworkSecret);
        Assert.Empty(vm.NetworkUsername);
        Assert.True(vm.IsGithubConnected);
        Assert.Equal("oauth-session-secret", vm.GithubToken);
        Assert.Equal("oauth-session-secret", vm.NetworkOptions(0).Secret);
        await vm.ToggleFavoriteCommand.ExecuteAsync(null);
        var json = File.ReadAllText(Path.Combine(fixture.SettingsPath, "settings.json"));
        Assert.DoesNotContain("oauth-session-secret", json);
        Assert.DoesNotContain("refresh-secret", json);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            window.ShowDocument("Settings");
            vm.SelectedSettingsSection = "Integrations";
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsIntegrationsSettings);
            Assert.NotNull(
                window
                    .GetVisualDescendants()
                    .OfType<SettingsView>()
                    .Single()
                    .FindControl<Button>("GithubOAuthSignInButton")
            );
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Assert.False(view.FindControl<Button>("GithubOAuthSignInButton")!.IsVisible);
            Assert.True(view.FindControl<Button>("GithubDisconnectButton")!.IsVisible);
            SavePreview(window, "integrations.png");
            await WaitUntilAsync(() => vm.GithubToken == "rotated-access-secret" && !vm.IsAuthenticationBusy);
            Assert.Equal(1, handler.RefreshRequests);
            Assert.True(vm.IsGithubConnected);
            Assert.Equal("Connected as LlamaUser", vm.GithubAccountStatus);
            Assert.Contains("renews automatically", vm.GithubSessionExpiryText);
            var store = new ProtectedIntegrationStore(fixture.SettingsPath);
            var profileId = vm.ActiveProfile!.Id;
            if (store.IsSupported)
            {
                Assert.Equal("rotated-refresh-secret", (await store.LoadAsync(profileId))!.RefreshToken);
            }

            await vm.CreateProfileCommand.ExecuteAsync(null);
            Assert.False(vm.IsGithubConnected);
            Assert.Empty(vm.GithubToken);
            vm.ProfileToSwitch = vm.Profiles.Single(p => p.Id == profileId);
            await vm.SwitchProfileCommand.ExecuteAsync(null);
            if (store.IsSupported)
            {
                Assert.True(vm.IsGithubConnected);
                Assert.Equal("rotated-access-secret", vm.GithubToken);
            }

            await vm.DisconnectGithubCommand.ExecuteAsync(null);
            Assert.Null(await store.LoadAsync(profileId));
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Button>("GithubOAuthSignInButton")!.IsVisible);
            Assert.False(view.FindControl<Button>("GithubDisconnectButton")!.IsVisible);
            vm.OpenIntegrationBrowser = _ =>
            {
                vm.CancelAuthenticationCommand.Execute(null);
                return Task.FromResult(true);
            };
            await vm.SignInGithubOAuthCommand.ExecuteAsync(null);
            Assert.Empty(vm.GithubToken);
            Assert.Empty(vm.GithubOAuthUserCode);
            Assert.False(vm.IsAuthenticationBusy);
            Assert.Contains("canceled", vm.GithubAccountStatus);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SavedGithubAuthorizationRenewsOnRestartRetriesOfflineAndClearsRevokedCredentials()
    {
        using var fixture = new WorkspaceFixture();
        if (!new ProtectedIntegrationStore(fixture.SettingsPath).IsSupported)
        {
            return;
        }

        var handler = new OAuthHandler { InitialExpiry = 3600 };
        using var client = new HttpClient(handler);
        Func<GitLlama.Providers.GitHubDeviceOAuth> factory = () =>
            new(
                client,
                delay: (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }
            );
        var vm = await fixture.OpenAsync(oauthFactory: factory);
        vm.AutoRefresh = false;
        vm.GithubOAuthVerify = (_, _) => Task.FromResult("LlamaUser");
        await vm.SignInGithubOAuthCommand.ExecuteAsync(null);
        var profileId = vm.ActiveProfile!.Id;
        var store = new ProtectedIntegrationStore(fixture.SettingsPath);
        var connection = (await store.LoadAsync(profileId))!;
        vm.Dispose();
        await store.SaveAsync(
            profileId,
            new GithubStoredConnection
            {
                ClientId = connection.ClientId,
                Login = connection.Login,
                AccessToken = connection.AccessToken,
                RefreshToken = connection.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                RefreshExpiresAt = connection.RefreshExpiresAt,
            }
        );
        var reopened = await fixture.OpenAsync(oauthFactory: factory);
        reopened.AutoRefresh = false;
        Assert.True(reopened.IsGithubConnected);
        Assert.Equal("rotated-access-secret", reopened.GithubToken);
        Assert.Equal(1, handler.RefreshRequests);
        handler.Offline = true;
        Assert.False(await reopened.RenewGithubConnectionAsync());
        Assert.True(reopened.IsGithubConnected);
        Assert.Equal("rotated-refresh-secret", (await store.LoadAsync(profileId))!.RefreshToken);
        Assert.DoesNotContain("refresh-secret", reopened.GithubStorageFeedback);
        handler.Offline = false;
        handler.RejectRefresh = true;
        Assert.False(await reopened.RenewGithubConnectionAsync());
        Assert.False(reopened.IsGithubConnected);
        Assert.Empty(reopened.GithubToken);
        Assert.Null(await store.LoadAsync(profileId));
        Assert.Contains("revoked", reopened.GithubAccountStatus);
    }

    private sealed class OAuthHandler : HttpMessageHandler
    {
        public int RefreshRequests { get; private set; }
        public bool RejectRefresh { get; set; }
        public bool Offline { get; set; }
        public int InitialExpiry { get; set; } = 3;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            string body;
            if (form.Contains("grant_type=refresh_token"))
            {
                RefreshRequests++;
                if (Offline)
                {
                    throw new HttpRequestException("refresh-secret");
                }

                body = RejectRefresh
                    ? "{\"error\":\"bad_refresh_token\",\"error_description\":\"refresh-secret\"}"
                    : "{\"access_token\":\"rotated-access-secret\",\"token_type\":\"bearer\",\"expires_in\":3600,\"refresh_token\":\"rotated-refresh-secret\",\"refresh_token_expires_in\":15000000}";
            }
            else
            {
                body = request.RequestUri!.AbsolutePath.EndsWith("/code")
                    ? "{\"device_code\":\"device-secret\",\"user_code\":\"ABCD-EFGH\",\"expires_in\":900,\"interval\":5}"
                    : "{\"access_token\":\"oauth-session-secret\",\"token_type\":\"bearer\",\"expires_in\":"
                        + InitialExpiry
                        + ",\"refresh_token\":\"refresh-secret\",\"refresh_token_expires_in\":15000000}";
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    [AvaloniaFact]
    public async Task PushToNewEmptyRemotePublishesBranchAndShowsPersistentSuccessOrFailure()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var remotePath = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "GitLlama.git");
        Repository.Init(remotePath, true);
        vm.NewRemoteName = "GitLlama";
        vm.NewRemoteUrl = remotePath;
        await vm.AddRemoteCommand.ExecuteAsync(null);
        Assert.Equal("GitLlama", vm.SelectedRemote!.Name);
        var branchName = vm.CurrentBranch;
        await vm.PushRemoteCommand.ExecuteAsync(null);
        Assert.Contains("Pushed " + branchName + " and its tags to GitLlama/" + branchName, vm.RemoteOperationFeedback);
        using (var remote = new Repository(remotePath))
        using (var local = new Repository(fixture.RepositoryPath))
        {
            Assert.Equal(local.Head.Tip.Sha, remote.Branches[branchName].Tip.Sha);
            Assert.Equal("GitLlama", local.Head.RemoteName);
        }

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("Pushed", vm.RemoteOperationFeedback);
        vm.NewRemoteName = "Missing";
        vm.NewRemoteUrl = Path.Combine(Path.GetDirectoryName(remotePath)!, "missing.git");
        await vm.AddRemoteCommand.ExecuteAsync(null);
        await vm.PushRemoteCommand.ExecuteAsync(null);
        Assert.Contains("Push to Missing failed:", vm.RemoteOperationFeedback);
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Border>("RemoteFeedbackBanner")!.IsVisible);
            SavePreview(window, "push-failed.png");
            vm.SelectedRemote = null;
            await vm.PushRemoteCommand.ExecuteAsync(null);
            Assert.Contains("choose a remote", vm.RemoteOperationFeedback);
            vm.DismissRemoteFeedbackCommand.Execute(null);
            Assert.False(vm.HasRemoteOperationFeedback);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    [AvaloniaFact]
    public async Task AzurePatConnectionsAreVerifiedSavedScopedAndRemoved()
    {
        using var fixture = new WorkspaceFixture();
        Func<string, string, CancellationToken, Task<IReadOnlyList<GitLlama.Providers.AzureRepository>>> loader = (
            org,
            token,
            ct
        ) =>
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal("pat-" + org, token);
            return Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureRepository>>([
                new(org, "client", "Project", $"https://dev.azure.com/{org}/Project/_git/client", ""),
            ]);
        };
        var vm = await fixture.OpenAsync(azureRepositoryLoader: loader);
        vm.AutoRefresh = false;
        foreach (var org in new[] { "team", "other" })
        {
            vm.AzureOrganizationInput = "https://dev.azure.com/" + org;
            vm.AzurePatInput = "pat-" + org;
            await vm.ConnectAzurePatCommand.ExecuteAsync(null);
            Assert.Empty(vm.AzurePatInput);
            Assert.True(vm.IsAzureConnected);
        }

        Assert.Equal(2, vm.AzureOrganizations.Count);
        foreach (var org in new[] { "team", "other" })
        {
            Assert.Equal(
                "pat-" + org,
                vm.NetworkOptions(
                    0,
                    remoteUrl: $"https://dev.azure.com/{org}/Project/_git/client"
                ).AzurePersonalAccessToken
            );
        }

        var unknown = "https://dev.azure.com/unknown/Project/_git/client";
        Assert.Empty(vm.NetworkOptions(0, remoteUrl: unknown).AzurePersonalAccessToken);
        Assert.Throws<InvalidOperationException>(() =>
            vm.NetworkOptions(0, remoteUrl: unknown).EnsureAzureAccess(unknown)
        );
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            window.ShowDocument("Settings");
            vm.SelectedSettingsSection = "Integrations";
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Assert.NotNull(view.FindControl<TextBox>("AzurePatInput"));
            Assert.NotNull(view.FindControl<Button>("AzureSignInButton"));
            view.FindControl<ScrollViewer>("SettingsContentScroll")!.Offset = new Vector(0, 480);
            SavePreview(window, "azure-pat-connections.png");
        }
        finally
        {
            window.Close();
        }

        Assert.DoesNotContain("pat-team", File.ReadAllText(Path.Combine(fixture.SettingsPath, "settings.json")));
        var profile = vm.ActiveProfile!.Id;
        vm.Dispose();
        var legacyStore = new ProtectedIntegrationStore(fixture.SettingsPath);
        if (legacyStore.IsSupported)
            await legacyStore.SavePayloadAsync(profile, "azure", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version = 3,
                Connections = new[] { new { Organization = "team", Token = "pat-team" }, new { Organization = "other", Token = "pat-other" } }
            }));
        var restored = await fixture.OpenAsync(azureRepositoryLoader: loader);
        restored.AutoRefresh = false;
        var store = new ProtectedIntegrationStore(fixture.SettingsPath);
        if (store.IsSupported)
        {
            Assert.Equal(2, restored.AzureOrganizations.Count);
            await restored.BrowseAzureOrganizationCommand.ExecuteAsync(
                restored.AzureOrganizations.Single(org => org.Name == "team")
            );
            Assert.Single(restored.AzureRepositories);
            await restored.RemoveAzureOrganizationCommand.ExecuteAsync(
                restored.AzureOrganizations.Single(org => org.Name == "team")
            );
            Assert.Empty(
                restored
                    .NetworkOptions(0, remoteUrl: "https://dev.azure.com/team/Project/_git/client")
                    .AzurePersonalAccessToken
            );
            Assert.Single(restored.AzureOrganizations);
        }

        await restored.DisconnectAzureCommand.ExecuteAsync(null);
        Assert.False(restored.IsAzureConnected);
        Assert.Null(await store.LoadPayloadAsync(profile, "azure"));
    }

    [AvaloniaFact]
    public async Task GithubPushDeniedOffersAuthorizationWithoutAttemptingThePush()
    {
        using var fixture = new WorkspaceFixture();
        using var handler = new OAuthHandler();
        using var client = new HttpClient(handler);
        var vm = await fixture.OpenAsync(oauthFactory: () =>
            new GitLlama.Providers.GitHubDeviceOAuth(
                client,
                delay: (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }
            )
        );
        vm.AutoRefresh = false;
        vm.GithubOAuthVerify = (_, _) => Task.FromResult("LlamaUser");
        await vm.SignInGithubOAuthCommand.ExecuteAsync(null);
        vm.NewRemoteName = "GitHub";
        vm.NewRemoteUrl = "https://github.com/example/denied.git";
        await vm.AddRemoteCommand.ExecuteAsync(null);
        vm.GithubPushAccessCheck = (repo, token, _) =>
        {
            Assert.Equal("example", repo.Owner);
            Assert.Equal("denied", repo.Name);
            Assert.Equal(vm.GithubToken, token);
            return Task.FromResult(GitLlama.Providers.GitHubPushAccess.ApprovalNeeded);
        };
        await vm.PushRemoteCommand.ExecuteAsync(null);
        Assert.Contains("GitHub denied GitLlama access", vm.RemoteOperationFeedback);
        Assert.True(vm.HasGithubPushAccessLink);
        Assert.False(vm.IsNetworkBusy);
        Uri? opened = null;
        vm.OpenIntegrationBrowser = uri =>
        {
            opened = uri;
            return Task.FromResult(true);
        };
        await vm.ManageGithubPushAccessCommand.ExecuteAsync(null);
        Assert.Equal("https://github.com/settings/connections/applications/Ov23liO3PME31PJH8PfM", opened!.AbsoluteUri);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("GithubGrantAccessButton")!.IsEffectivelyVisible);
            var launcher = vm.OpenIntegrationBrowser;
            window.ShowDocument("Settings");
            await WaitUntilAsync(() => window.GetVisualDescendants().OfType<SettingsView>().Any());
            vm.CloseSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(launcher, vm.OpenIntegrationBrowser);
            Assert.True(IntegrationBrowserPolicy.Allows(new Uri(vm.GithubPushAccessUrl)));
            opened = null;
            await vm.ManageGithubPushAccessCommand.ExecuteAsync(null);
            Assert.NotNull(opened);
            vm.OpenIntegrationBrowser = _ => Task.FromResult(false);
            await vm.ManageGithubPushAccessCommand.ExecuteAsync(null);
            Assert.Contains("browser could not be opened", vm.RemoteOperationFeedback);
            vm.OpenIntegrationBrowser = launcher;
            SavePreview(window, "github-push-access.png");
        }
        finally
        {
            window.Close();
        }

        vm.DismissRemoteFeedbackCommand.Execute(null);
        Assert.False(vm.HasGithubPushAccessLink);
        vm.GithubPushAccessCheck = (_, _, _) => Task.FromResult(GitLlama.Providers.GitHubPushAccess.NoWritePermission);
        Assert.False(
            await vm.CheckLinkedGithubPushAccessAsync(vm.SelectedRemote!.Url, vm.GithubToken, CancellationToken.None)
        );
        Assert.Contains("write access", vm.RemoteOperationFeedback);
        Assert.False(vm.HasGithubPushAccessLink);
        vm.DismissRemoteFeedbackCommand.Execute(null);
        vm.GithubPushAccessCheck = (_, _, _) => throw new HttpRequestException("secret-provider-error");
        Assert.True(
            await vm.CheckLinkedGithubPushAccessAsync(vm.SelectedRemote.Url, vm.GithubToken, CancellationToken.None)
        );
        Assert.False(vm.HasRemoteOperationFeedback);
        vm.GithubPushAccessCheck = (_, _, _) => Task.FromResult(GitLlama.Providers.GitHubPushAccess.PrivateScopeNeeded);
        Assert.False(
            await vm.CheckLinkedGithubPushAccessAsync(vm.SelectedRemote.Url, vm.GithubToken, CancellationToken.None)
        );
        Assert.True(vm.NeedsGithubPrivateAccess);
        await vm.EnableGithubPrivateAccessCommand.ExecuteAsync(null);
        Assert.True(vm.GithubIncludePrivateRepositories);
        Assert.True(vm.IsGithubConnected);
        Assert.True(vm.IsGithubIntegrationExpanded);
        Assert.True(vm.IsSettingsOpen);
        vm.DismissRemoteFeedbackCommand.Execute(null);
        Assert.False(vm.NeedsGithubPrivateAccess);
        vm.OfferGithubAccessAfterPushFailure(
            vm.SelectedRemote.Url,
            vm.GithubToken,
            "request failed with status code: 403"
        );
        Assert.True(vm.HasGithubPushAccessLink);
        vm.DismissRemoteFeedbackCommand.Execute(null);
        vm.OfferGithubAccessAfterPushFailure(vm.SelectedRemote.Url, vm.GithubToken, "non-fast-forward update rejected");
        Assert.False(vm.HasGithubPushAccessLink);
        vm.OfferGithubAccessAfterPushFailure("https://other.example/owner/repo", vm.GithubToken, "403 access denied");
        Assert.False(vm.HasGithubPushAccessLink);
        vm.OfferGithubAccessAfterPushFailure(
            vm.SelectedRemote.Url,
            vm.GithubToken,
            "refusing to allow an OAuth App to create or update workflow `.github/workflows/build.yml` without `workflow` scope"
        );
        Assert.True(vm.NeedsGithubWorkflowAccess);
        Assert.Contains("authorize the workflow permission", vm.RemoteOperationFeedback);
        await vm.ReconnectGithubPermissionsCommand.ExecuteAsync(null);
        Assert.True(vm.IsGithubConnected);
        Assert.False(vm.NeedsGithubWorkflowAccess);
        Assert.True(vm.GithubIncludePrivateRepositories);
        Assert.Contains("Retry the push", vm.RemoteOperationFeedback);
    }

    [AvaloniaFact]
    public async Task RemoteUrlPopupUpdatesConnectionAndRemovalRequiresConfirmation()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.NewRemoteName = "origin";
        vm.NewRemoteUrl = "https://github.com/example/old.git";
        await vm.AddRemoteCommand.ExecuteAsync(null);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(vm.OpenIntegrationBrowser);
            var edit = RemoteEditDialog.ConnectionAsync(window, vm, vm.SelectedRemote);
            await WaitUntilAsync(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            var boxes = dialog.GetVisualDescendants().OfType<TextBox>().ToDictionary(input => input.Name!);
            Assert.True(boxes["RemoteNameInput"].IsReadOnly);
            boxes["RemoteUrlInput"].Text = "https://github.com/example/new.git";
            dialog
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(button => Equals(button.Content, "Save remote"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !dialog.IsVisible);
            await edit;
            Assert.Equal("https://github.com/example/new.git", vm.SelectedRemote!.Url);
            Assert.Equal(vm.SelectedRemote.Url, vm.SelectedRemote.PushUrl);
            var remove = RemoteEditDialog.ConnectionAsync(window, vm, vm.SelectedRemote, remove: true);
            await WaitUntilAsync(() => window.OwnedWindows.Any());
            dialog = window.OwnedWindows.Single();
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBox>(), input => input.IsVisible);
            dialog.GetVisualDescendants().OfType<Button>()
                .Single(item => Equals(item.Content, "Cancel"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await remove;
            Assert.Single(vm.Remotes);
            remove = RemoteEditDialog.ConnectionAsync(window, vm, vm.SelectedRemote, remove: true);
            await WaitUntilAsync(() => window.OwnedWindows.Any());
            dialog = window.OwnedWindows.Single();
            var button = dialog.GetVisualDescendants().OfType<Button>()
                .Single(item => Equals(item.Content, "Remove remote"));
            Assert.False(button.IsDefault);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !dialog.IsVisible);
            await remove;
            Assert.Empty(vm.Remotes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TagDeletionWarningCancelsOnDismissAndDeletesOnlyOnApproval()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.SelectedTag = vm.Tags.Single(tag => tag.Name == "v0.3.0");
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        window.Show();
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var deletion = vm.DeleteSelectedTagCommand.ExecuteAsync(null);
                await WaitUntilAsync(() => window.OwnedWindows.Any());
                var dialog = window.OwnedWindows.Single();
                Assert.Empty(dialog.GetVisualDescendants().OfType<TextBox>());
                Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text?.Contains("v0.3.0") == true);
                var approve = dialog.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, "Delete tag"));
                Assert.False(approve.IsDefault);
                if (attempt == 0) SavePreview(dialog, "delete-tag-warning.png");
                if (attempt == 1)
                    dialog.Close();
                else
                    (attempt == 2 ? approve : dialog.GetVisualDescendants().OfType<Button>()
                        .Single(button => Equals(button.Content, "Cancel")))
                        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await deletion;
                using var repo = new Repository(fixture.RepositoryPath);
                Assert.Equal(attempt < 2, repo.Tags["v0.3.0"] is not null);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WarningApprovalIsBoundToItsTargetAndResetCancellationPreservesHistory()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.SelectedTag = vm.Tags.Single();
        // An absent dialog host fails closed.
        await vm.DeleteSelectedTagCommand.ExecuteAsync(null);
        using (var repo = new Repository(fixture.RepositoryPath)) Assert.NotNull(repo.Tags["v0.3.0"]);
        var reply = new TaskCompletionSource<bool>();
        vm.RequestConfirmation = request =>
        {
            Assert.Contains("v0.3.0", request.Message);
            return reply.Task;
        };
        var deletion = vm.DeleteSelectedTagCommand.ExecuteAsync(null);
        vm.SelectedTag = null;
        reply.SetResult(true);
        await deletion;
        using (var repo = new Repository(fixture.RepositoryPath)) Assert.NotNull(repo.Tags["v0.3.0"]);

        vm.SelectedHistoryRow = vm.HistoryRows.Last();
        string original;
        using (var repo = new Repository(fixture.RepositoryPath)) original = repo.Head.Tip.Sha;
        var warnings = new List<ActionConfirmation>();
        vm.RequestConfirmation = request => { warnings.Add(request); return Task.FromResult(false); };
        await vm.SoftResetToSelectedCommitCommand.ExecuteAsync(null);
        await vm.MixedResetToSelectedCommitCommand.ExecuteAsync(null);
        await vm.HardResetToSelectedCommitCommand.ExecuteAsync(null);
        vm.RebaseTargetBranch = "feature/authentication";
        await vm.RebaseCurrentOntoTargetCommand.ExecuteAsync(null);
        await vm.ExecuteEditableRebasePlanCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Soft reset", "Mixed reset", "Hard reset", "Rebase", "Execute rebase" },
            warnings.Select(warning => warning.Action));
        using (var repo = new Repository(fixture.RepositoryPath)) Assert.Equal(original, repo.Head.Tip.Sha);
        Assert.Contains("Validate login input", File.ReadAllText(Path.Combine(fixture.RepositoryPath, "src/Auth.cs")));
    }

    [Theory]
    [InlineData("https://github.com/settings/connections/applications/Ov23liO3PME31PJH8PfM", true)]
    [InlineData("https://github.com/login/device", true)]
    [InlineData("https://microsoft.com/devicelogin", true)]
    [InlineData("https://www.microsoft.com/devicelogin", true)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/deviceauth", true)]
    [InlineData("https://microsoft.com.evil.example/devicelogin", false)]
    [InlineData("https://microsoft.com/devicelogin?redirect=evil", false)]
    [InlineData("https://microsoft.com/anything-else", false)]
    [InlineData("https://user:pass@microsoft.com/devicelogin", false)]
    [InlineData("https://github.com.evil.example/settings/connections/applications/id", false)]
    [InlineData("https://github.com/settings/connections/applications/id?redirect=evil", false)]
    public void IntegrationBrowserChecksExactAllowedLinks(string url, bool allowed) =>
        Assert.Equal(allowed, IntegrationBrowserPolicy.Allows(new Uri(url)));

    [AvaloniaFact]
    public async Task AzurePatValidationAndFailuresDoNotReplaceWorkingConnections()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.AzureRepositoryLoader = (_, _, _) => Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureRepository>>([]);
        vm.AzureOrganizationInput = "team";
        vm.AzurePatInput = "original-token";
        await vm.ConnectAzurePatCommand.ExecuteAsync(null);
        Assert.True(vm.IsAzureConnected);
        Assert.Empty(vm.AzureRepositories);
        vm.AzureRepositoryLoader = (_, _, _) => throw new HttpRequestException("provider echoed replacement-token");
        vm.AzurePatInput = "replacement-token";
        await vm.ConnectAzurePatCommand.ExecuteAsync(null);
        Assert.Empty(vm.AzurePatInput);
        Assert.DoesNotContain("replacement-token", vm.AzureOrganizationFeedback);
        Assert.Equal(
            "original-token",
            vm.NetworkOptions(0, remoteUrl: "https://dev.azure.com/team/Project/_git/client").AzurePersonalAccessToken
        );
        vm.AzureOrganizationInput = "https://attacker.example/team";
        vm.AzurePatInput = "token";
        await vm.ConnectAzurePatCommand.ExecuteAsync(null);
        Assert.Single(vm.AzureOrganizations);
        vm.AzureOrganizationInput = "other";
        vm.AzurePatInput = "bad\r\ntoken";
        await vm.ConnectAzurePatCommand.ExecuteAsync(null);
        Assert.Single(vm.AzureOrganizations);
        vm.AzureRepositoryLoader = (_, _, ct) =>
        {
            vm.CancelAuthenticationCommand.Execute(null);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureRepository>>([]);
        };
        vm.AzurePatInput = "token";
        await vm.ConnectAzurePatCommand.ExecuteAsync(null);
        Assert.Single(vm.AzureOrganizations);
        Assert.Contains("canceled", vm.AzureOrganizationFeedback);
    }

    [AvaloniaFact]
    public async Task RepositoryManagerCombinesAccountsAndPrefillsCloneDialog()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.AzureRepositoryLoader = (org, _, _) =>
            Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureRepository>>([
                new(org, "client", "Project", $"https://dev.azure.com/{org}/Project/_git/client", ""),
            ]);
        foreach (var org in new[] { "team", "other" })
        {
            vm.AzureOrganizationInput = org;
            vm.AzurePatInput = "test-pat";
            await vm.ConnectAzurePatCommand.ExecuteAsync(null);
        }

        vm.GithubToken = "github-test-token";
        vm.IsGithubConnected = true;
        vm.GithubCloneRepositoryLoader = (token, _) =>
        {
            Assert.Equal("github-test-token", token);
            return Task.FromResult<IReadOnlyList<GitLlama.Providers.CloneableRepository>>([
                new("GitHub", "owner", "shared", "https://github.com/owner/shared.git"),
            ]);
        };
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            window.ShowDocument("Settings");
            vm.SelectedSettingsSection = "Integrations";
            Dispatcher.UIThread.RunJobs();
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Assert.Null(settings.FindControl<ListBox>("AzureRepositoriesList"));
            await vm.ShowCloneableRepositoriesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsSettingsOpen);
            Assert.True(vm.IsRepositoryManagerSelected);
            Assert.Equal(3, vm.CloneableRepositories.Count);
            vm.CloneableRepositorySearch = "other";
            Assert.Single(vm.FilteredCloneableRepositories);
            var repo = vm.FilteredCloneableRepositories.Single();
            var emptyTab = vm.SelectedRepositoryWorkspaceTab;
            var dialog = new RepositoryCreationDialog(vm, true, repo.CloneUrl);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(repo.CloneUrl, vm.CloneUrl);
            Assert.Equal(repo.CloneUrl, dialog.FindControl<TextBox>("RepositoryUrl")!.Text);
            Assert.Same(emptyTab, vm.SelectedRepositoryWorkspaceTab);
            SavePreview(dialog, "azure-clone-prefilled.png");
            dialog.Close();
            var manual = new RepositoryCreationDialog(vm, true);
            Assert.Empty(vm.CloneUrl);
            manual.Close();
            vm.CloneableRepositorySearch = "";
            SavePreview(window, "repository-clone-catalog.png");
            await vm.RemoveAzureOrganizationCommand.ExecuteAsync(
                vm.AzureOrganizations.Single(org => org.Name == "other")
            );
            await vm.RefreshCloneableRepositoriesCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.CloneableRepositories.Count);
            Assert.DoesNotContain(vm.CloneableRepositories, item => item.Group.StartsWith("other"));
            vm.AzureRepositoryLoader = (_, _, _) => throw new HttpRequestException("secret error");
            await vm.RefreshCloneableRepositoriesCommand.ExecuteAsync(null);
            Assert.Single(vm.CloneableRepositories);
            Assert.Contains("Some repositories", vm.CloneableRepositoryFeedback);
            Assert.DoesNotContain("secret error", vm.CloneableRepositoryFeedback);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RepositoryCatalogDoesNotPublishResultsFromRemovedConnections()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        vm.AzureRepositoryLoader = (_, _, _) => Task.FromResult<IReadOnlyList<GitLlama.Providers.AzureRepository>>([]);
        vm.AzureOrganizationInput = "team";
        vm.AzurePatInput = "pat";
        await vm.ConnectAzurePatCommand.ExecuteAsync(null);
        var completion = new TaskCompletionSource<IReadOnlyList<GitLlama.Providers.AzureRepository>>();
        vm.AzureRepositoryLoader = (_, _, _) => completion.Task;
        var loading = vm.RefreshCloneableRepositoriesCommand.ExecuteAsync(null);
        await vm.RemoveAzureOrganizationCommand.ExecuteAsync(vm.AzureOrganizations.Single());
        completion.SetResult([new("1", "stale", "Project", "https://dev.azure.com/team/Project/_git/stale", "")]);
        await loading;
        Assert.Empty(vm.CloneableRepositories);
        Assert.False(vm.IsCloneableRepositoriesBusy);
    }

    [AvaloniaFact]
    public async Task AzureLegacyOAuthCachesRequireFreshSignIn()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        vm.AutoRefresh = false;
        var store = new ProtectedIntegrationStore(fixture.SettingsPath);
        if (!store.IsSupported)
        {
            return;
        }

        var profile = vm.ActiveProfile!.Id;
        await store.SavePayloadAsync(
            profile,
            "azure",
            System.Text.Encoding.UTF8.GetBytes("{\"Version\":2,\"Cache\":\"old-oauth-cache\"}")
        );
        vm.Dispose();
        var restored = await fixture.OpenAsync();
        restored.AutoRefresh = false;
        Assert.False(restored.IsAzureConnected);
        Assert.Empty(restored.AzureOrganizations);
        Assert.Contains("older version", restored.AzureOrganizationFeedback);
        Assert.Null(await store.LoadPayloadAsync(profile, "azure"));
    }

    private static void SavePreview(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("GITLLAMA_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name));
    }

    [AvaloniaFact]
    public async Task LfsTrackedFilesAreStagedAsPointers()
    {
        using var fixture = new WorkspaceFixture();
        var vm = await fixture.OpenAsync();
        Assert.False(vm.UsesLfs);

        vm.LfsPatternInput = "*.bin";
        await vm.TrackLfsPatternCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.LfsPatterns.Any(row => row.Pattern == "*.bin"));
        Assert.True(vm.UsesLfs);

        var content = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(fixture.RepositoryPath, "Hero.bin"), content);
        await vm.RefreshCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.WorkingFiles.Any(file => file.Path == "Hero.bin"));
        vm.SelectedWorkingFile = vm.WorkingFiles.First(file => file.Path == "Hero.bin");
        await vm.StageSelectedCommand.ExecuteAsync(null);

        using (var repo = new Repository(fixture.RepositoryPath))
        {
            var staged = repo.Lookup<Blob>(repo.Index["Hero.bin"].Id);
            Assert.StartsWith("version https://git-lfs.github.com/spec/v1", staged.GetContentText());
        }

        // The inspector marks LFS files, in working changes and in a commit's files.
        await WaitUntilAsync(() => vm.WorkingFiles.Any(file => file is { Path: "Hero.bin", IsLfs: true }));
        Assert.False(vm.WorkingFiles.Single(file => file.Path == "README.md").IsLfs);
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            var signature = new Signature("Robert Fryer", "robert@example.com", DateTimeOffset.UtcNow);
            repo.Commit("Add hero model", signature, signature);
        }

        await vm.RefreshCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.HistoryRows.Any(row => row.Subject == "Add hero model"));
        vm.SelectedHistoryRow = vm.HistoryRows.First(row => row.Subject == "Add hero model");
        await WaitUntilAsync(() => vm.HistoryFileRows.Any(file => file is { Path: "Hero.bin", IsLfs: true }));
        Assert.False(vm.HistoryFileRows.Single(file => file.Path == "Hero.bin").ShowLineCounts);

        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var patterns = window
                .GetVisualDescendants()
                .OfType<ListBox>()
                .Single(list => list.Name == "LfsPatternList");
            Assert.Equal(1, patterns.ItemCount);
            SavePreview(window, "lfs-sidebar.png");
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }

        await vm.UntrackLfsPatternCommand.ExecuteAsync("*.bin");
        await WaitUntilAsync(() => vm.LfsPatterns.Count == 0);
    }

    [AvaloniaFact]
    public async Task LfsLocksAppearInTheSidebarAndOnChangedFiles()
    {
        using var server = new GitLlama.Tests.Shared.FakeLfsLockServer();
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            repo.Config.Set("lfs.url", server.Url);
        }

        var vm = await fixture.OpenAsync();
        vm.LfsPatternInput = "*.psd";
        await vm.TrackLfsPatternCommand.ExecuteAsync(null);
        await vm.RequireLfsLockingCommand.ExecuteAsync("*.psd");
        await WaitUntilAsync(() => vm.LfsPatterns.Any(row => row is { Pattern: "*.psd", Lockable: true }));

        Directory.CreateDirectory(Path.Combine(fixture.RepositoryPath, "Art"));
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "Art", "Hero.psd"), "art");
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            Commands.Stage(repo, [".gitattributes", "Art/Hero.psd"]);
            var signature = new Signature("Robert Fryer", "robert@example.com", DateTimeOffset.UtcNow);
            repo.Commit("Add hero art", signature, signature);
        }

        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "Art", "Map.psd"), "map");
        server.Seed("Art/Map.psd", "Alex");

        await vm.LockLfsFileCommand.ExecuteAsync("Art/Hero.psd");
        await WaitUntilAsync(() => vm.LfsLocks.Count == 2);
        Assert.Contains(vm.LfsLocks, row => row is { Path: "Art/Hero.psd", Owner: "you" });
        Assert.Contains(vm.LfsLocks, row => row is { Path: "Art/Map.psd", Owner: "Alex" });

        await vm.RefreshCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.WorkingFiles.Any(file => file.Path == "Art/Map.psd" && file.HasLock));
        var map = vm.WorkingFiles.Single(file => file.Path == "Art/Map.psd");
        Assert.Contains("Alex", map.LockToolTip);
        Assert.False(map.Lock!.IsOurs);
        // Locking made the committed lockable file writable.
        Assert.False(new FileInfo(Path.Combine(fixture.RepositoryPath, "Art", "Hero.psd")).IsReadOnly);

        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var locks = window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "LfsLockList");
            Assert.Equal(2, locks.ItemCount);
            SavePreview(window, "lfs-locks.png");
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }

        await vm.UnlockLfsFileCommand.ExecuteAsync("Art/Hero.psd");
        await WaitUntilAsync(() => vm.LfsLocks.Count == 1);
        Assert.True(new FileInfo(Path.Combine(fixture.RepositoryPath, "Art", "Hero.psd")).IsReadOnly);

        // Someone else's lock can't be released without forcing it.
        await vm.UnlockLfsFileCommand.ExecuteAsync("Art/Map.psd");
        Assert.Contains("Force unlock", vm.Status);
        await vm.ForceUnlockLfsFileCommand.ExecuteAsync("Art/Map.psd");
        await WaitUntilAsync(() => vm.LfsLocks.Count == 0);
        Assert.False(vm.WorkingFiles.Single(file => file.Path == "Art/Map.psd").HasLock);
    }

    [AvaloniaFact]
    public async Task SubmodulesAreListedAndCanBeEnteredAndLeftInTheSameTab()
    {
        using var fixture = new WorkspaceFixture();
        // Build a submodule by hand: a nested repository, its .gitmodules entry and a gitlink in the parent.
        var childPath = Path.Combine(fixture.RepositoryPath, "libs", "UnrealSharp");
        Directory.CreateDirectory(childPath);
        Repository.Init(childPath);
        using (var child = new Repository(childPath))
        {
            File.WriteAllText(Path.Combine(childPath, "Plugin.cs"), "// plugin\n");
            Commands.Stage(child, "Plugin.cs");
            var signature = new Signature("Robert Fryer", "robert@example.com", DateTimeOffset.UtcNow);
            child.Commit("Initial plugin", signature, signature);
        }

        var siblingPath = Path.Combine(fixture.RepositoryPath, "libs", "Tools");
        Repository.Clone(childPath, siblingPath);
        using (var sibling = new Repository(siblingPath))
        {
            File.WriteAllText(Path.Combine(siblingPath, "Tools.cs"), "// tools\n");
            Commands.Stage(sibling, "Tools.cs");
            var signature = new Signature("Robert Fryer", "robert@example.com", DateTimeOffset.UtcNow);
            sibling.Commit("Initial tools", signature, signature);
        }

        using (var parent = new Repository(fixture.RepositoryPath))
        {
            File.WriteAllText(
                Path.Combine(fixture.RepositoryPath, ".gitmodules"),
                "[submodule \"UnrealSharp\"]\n\tpath = libs/UnrealSharp\n\turl = https://example.com/UnrealSharp.git\n"
                + "[submodule \"Tools\"]\n\tpath = libs/Tools\n\turl = https://example.com/Tools.git\n"
            );
            Commands.Stage(parent, ".gitmodules");
            using var child = new Repository(childPath);
            using var sibling = new Repository(siblingPath);
            var definition = TreeDefinition
                .From(parent.ObjectDatabase.CreateTree(parent.Index))
                .AddGitLink("libs/UnrealSharp", child.Head.Tip.Id)
                .AddGitLink("libs/Tools", sibling.Head.Tip.Id);
            var tree = parent.ObjectDatabase.CreateTree(definition);
            var signature = new Signature("Robert Fryer", "robert@example.com", DateTimeOffset.UtcNow);
            var commit = parent.ObjectDatabase.CreateCommit(
                signature,
                signature,
                "Add UnrealSharp submodule",
                tree,
                [parent.Head.Tip],
                prettifyMessage: true
            );
            parent.Refs.UpdateTarget(parent.Head.Reference.ResolveToDirectReference(), commit.Id);
            parent.Index.Replace(tree);
            parent.Index.Write();
        }

        var vm = await fixture.OpenAsync();
        var root = vm.SelectedRepository!;
        await WaitUntilAsync(() => vm.Submodules.Count == 2);
        var module = vm.Submodules.Single(m => m.DisplayName == "UnrealSharp");
        Assert.Equal("UnrealSharp", module.DisplayName);
        Assert.True(module.IsCheckedOut);
        Assert.Equal("Up to date", module.Summary);
        Assert.True(vm.HasSubmodules);
        var tabs = vm.RepositoryWorkspaceTabs.Count;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(
                window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "AddSubmoduleButton")
            );
            SavePreview(window, "submodules-sidebar.png");

            await vm.OpenSubmoduleCommand.ExecuteAsync(module);
            await WaitUntilAsync(() => vm.HistoryRows.Any(row => row.Subject == "Initial plugin"));
            Assert.Equal(Path.GetFullPath(childPath), vm.SelectedRepository!.Path);
            Assert.Equal(root, vm.SelectedRepository.Parent);
            Assert.True(vm.IsInSubmodule);
            Assert.Equal(["repository", "submodule"], vm.RepositoryBreadcrumbs.Select(crumb => crumb.Caption));
            Assert.Equal(["StoneForge", "UnrealSharp"], vm.RepositoryBreadcrumbs.Select(crumb => crumb.Name));
            Assert.Equal(tabs, vm.RepositoryWorkspaceTabs.Count);
            Assert.Equal("StoneForge", vm.SelectedRepositoryWorkspaceTab!.Title);
            Assert.DoesNotContain(vm.Repositories, repository => repository.Path == vm.SelectedRepository.Path);
            Dispatcher.UIThread.RunJobs();
            var bar = window
                .GetVisualDescendants()
                .OfType<ItemsControl>()
                .Single(c => c.Name == "RepositoryBreadcrumbBar");
            Assert.Equal(2, bar.ItemCount);
            SavePreview(window, "submodule-entered.png");

            File.AppendAllText(Path.Combine(childPath, "Plugin.cs"), "// preserve this edit\n");
            var picker = bar.GetVisualDescendants().OfType<Button>().Single(button =>
                button.DataContext is RepositoryCrumb { IsSubmodule: true } && button.Content is StackPanel);
            picker.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => picker.ContextMenu?.IsOpen == true);
            var choices = picker.ContextMenu!.ItemsSource!.Cast<MenuItem>().ToArray();
            Assert.Equal(2, choices.Length);
            Assert.False(choices.Single(item => item.Header!.ToString()!.Contains("UnrealSharp")).IsEnabled);
            var tools = choices.Single(item => item.Header!.ToString()!.Contains("libs/Tools"));
            SavePreview(window, "submodule-switcher.png");
            tools.Command!.Execute(tools.CommandParameter);
            picker.ContextMenu.Close();
            await WaitUntilAsync(() => vm.HistoryRows.Any(row => row.Subject == "Initial tools"));
            Assert.Equal(Path.GetFullPath(siblingPath), vm.SelectedRepository!.Path);
            Assert.Equal(root, vm.SelectedRepository.Parent);
            Assert.Equal(tabs, vm.RepositoryWorkspaceTabs.Count);
            Assert.Contains("preserve this edit", File.ReadAllText(Path.Combine(childPath, "Plugin.cs")));
            Dispatcher.UIThread.RunJobs();
            var close = bar.GetVisualDescendants().OfType<Button>().Single(button =>
                button.DataContext is RepositoryCrumb { IsSubmodule: true } && Equals(button.Content, "×"));
            close.Command!.Execute(close.CommandParameter);
            await WaitUntilAsync(() => vm.HistoryRows.Any(row => row.Subject == "Add UnrealSharp submodule"));
            Assert.Equal(root, vm.SelectedRepository);
            Assert.False(vm.IsInSubmodule);
            Assert.Single(vm.RepositoryBreadcrumbs);
            Assert.Equal(tabs, vm.RepositoryWorkspaceTabs.Count);
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? diagnostic = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "Repository UI did not reach the expected state. " + diagnostic?.Invoke());
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "GitLlama-UI-" + Guid.NewGuid().ToString("N"));
        private readonly RepositorySessionManager _sessions;
        private readonly List<MainWindowViewModel> _viewModels = [];
        public string RepositoryPath => Path.Combine(_root, "StoneForge");
        public string SettingsPath => Path.Combine(_root, "settings");

        public WorkspaceFixture(IRepositoryOverviewReader? overview = null)
        {
            _sessions = new(new LibGitRepositoryReader(), overview ?? new LibGitOverviewReader());
            Directory.CreateDirectory(RepositoryPath);
            Repository.Init(RepositoryPath);
            using var repo = new Repository(RepositoryPath);
            repo.Config.Set("user.name", "Robert Fryer");
            repo.Config.Set("user.email", "robert@example.com");
            var subjects = new[]
            {
                "Initial commit",
                "Initial mod loader implementation",
                "Add mod metadata validation",
                "Fix issue with mod loading",
                "Improve settings UI",
                "Release v0.2.0",
                "Update dependencies",
                "Add async support to login",
            };
            var sequence = 0;
            void Commit(string subject, string file)
            {
                var absolute = Path.Combine(RepositoryPath, file);
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                File.AppendAllText(absolute, $"// {subject}\n");
                Commands.Stage(repo, file);
                var signature = new Signature(
                    "Robert Fryer",
                    "robert@example.com",
                    DateTimeOffset.UtcNow.AddDays(-10).AddHours(sequence++ * 12)
                );
                repo.Commit(subject, signature, signature);
            }

            foreach (var subject in subjects)
            {
                Commit(subject, "README.md");
            }

            var main = repo.Head;
            var feature = repo.CreateBranch("feature/authentication");
            Commands.Checkout(repo, feature);
            Commit("Refactor authentication service", "src/Auth.cs");
            Commit("Update authentication validation", "src/Auth.cs");
            Commands.Checkout(repo, main);
            Commit("Improve repository navigation", "src/Navigation.cs");
            var mergeSignature = new Signature("Robert Fryer", "robert@example.com", DateTimeOffset.UtcNow.AddDays(-2));
            repo.Merge(
                feature,
                mergeSignature,
                new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward }
            );
            Commit("Add unit tests and improve authentication", "tests/AuthTests.cs");
            repo.ApplyTag("v0.3.0");
            File.AppendAllText(Path.Combine(RepositoryPath, "src/Auth.cs"), "// Validate login input\n");
            File.AppendAllText(Path.Combine(RepositoryPath, "README.md"), "Documentation updates\n");
        }

        public async Task<MainWindowViewModel> OpenAsync(
            IHostingProvider? provider = null,
            string expectedAuthor = "Robert Fryer",
            Func<GitLlama.Providers.GitHubDeviceOAuth>? oauthFactory = null,
            Func<
                string,
                string,
                CancellationToken,
                Task<IReadOnlyList<GitLlama.Providers.AzureRepository>>
            >? azureRepositoryLoader = null
        )
        {
            var vm = new MainWindowViewModel(
                _sessions,
                new JsonSettingsStore(SettingsPath),
                new GitWorkingTreeService(),
                provider,
                oauthFactory,
                azureRepositoryLoader
            );
            _viewModels.Add(vm);
            await vm.Initialization;
            vm.RepositoryPath = RepositoryPath;
            await vm.AddRepositoryCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => vm.AuthorName == expectedAuthor && vm.HistoryRows.Count > 0);
            await vm.RefreshCommand.ExecuteAsync(null);
            return vm;
        }

        public void Dispose()
        {
            foreach (var vm in _viewModels)
            {
                vm.Dispose();
            }

            _sessions.Dispose();
            GitLlama.Git.GitLfsFilter.Shutdown();
            // Only remove this fixture's unique temporary directory. A background refresh can still be running a
            // short git process in it, so retry briefly.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(_root, true);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < 20)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task PullRequestTreeGroupsAssignmentsSearchesAndOffersLocalMerge()
    {
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath)) repo.Network.Remotes.Add("origin", "https://github.com/owner/repo.git");
        HostingItem Pull(int number, string title, string author) => new(number, title, "Description", "open", author,
            $"https://github.com/owner/repo/pull/{number}", true, new string('a', 40), "feature/change", "main");
        var provider = new FakeHostingProvider
        {
            Login = "me",
            Items = [Pull(41, "My change", "ME"), Pull(42, "Authentication update", "contributor") with
            { Assignees = ["Me"], RequestedReviewers = ["me"], IsDraft = true }, Pull(43, "Other work", "another")]
        };
        var vm = await fixture.OpenAsync(provider);
        vm.AutoRefresh = false;
        await WaitUntilAsync(() => vm.Remotes.Count == 1);
        vm.GithubOwner = "owner";
        vm.GithubRepository = "repo";
        vm.GithubToken = "fixture-token";
        await vm.RefreshPullRequestsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "1", "1", "1", "3" }, vm.PullRequestTree.Select(node => node.Count));
        var assigned = vm.PullRequestTree.Single(node => node.Key == "assigned");
        assigned.IsExpanded = false;
        await vm.RefreshPullRequestsCommand.ExecuteAsync(null);
        Assert.False(assigned.IsExpanded);
        Assert.Equal(42, Assert.Single(Assert.Single(assigned.Children).Children).Item!.Number);
        vm.PullRequestSearch = "authentication";
        Assert.Equal(new[] { "0", "1", "1", "1" }, vm.PullRequestTree.Select(node => node.Count));
        vm.PullRequestSearch = "#43";
        Assert.Equal("1", vm.PullRequestTree.Single(node => node.Key == "all").Count);
        vm.PullRequestSearch = "";
        assigned.IsExpanded = true;
        var template = new DockContentTemplate(vm);
        Avalonia.Application.Current!.DataTemplates.Add(template);
        var window = new MainWindow(fixture.SettingsPath) { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var nav = window.GetVisualDescendants().OfType<NavigationView>().First();
            nav.FindControl<Expander>("PullRequestCategory")!.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(nav.FindControl<TreeView>("PullRequestTree"));
            var menu = nav.BuildPullRequestMenu(provider.Items[1]);
            Assert.Contains(menu.ItemsSource!.Cast<MenuItem>(), item => item.Header!.ToString()!.StartsWith("Merge #42 into"));
            Assert.True(menu.ItemsSource!.Cast<MenuItem>().Single(item => item.Header!.ToString()!.StartsWith("Merge #42 into")).IsEnabled);
            var category = nav.FindControl<Expander>("PullRequestCategory")!;
            var searchBox = nav.FindControl<TextBox>("PullRequestSearchBox")!;
            var searchPosition = searchBox.TranslatePoint(new Avalonia.Point(0, 0), category);
            var leaf = vm.PullRequestTree.Single(node => node.Key == "review").Children.Single().Children.Single();
            vm.SelectedPullRequestNode = leaf;
            Assert.Equal(42, vm.SelectedPullRequest!.Number);
            Assert.Null(provider.MergedSha);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(searchPosition, searchBox.TranslatePoint(new Avalonia.Point(0, 0), category));
            foreach (var expander in nav.GetVisualDescendants().OfType<Expander>().Where(expander => expander.Classes.Contains("navigation")))
                expander.IsExpanded = expander.Name == "PullRequestCategory";
            if (nav.FindControl<Expander>("PullRequestCategory")!.Content is ResizableCategoryPanel panel)
                panel.Children[0].MaxHeight = 480;
            SavePreview(window, "pull-request-tree.png");
        }
        finally
        {
            window.Close();
            Avalonia.Application.Current.DataTemplates.Remove(template);
        }
        vm.GithubToken = "";
        Assert.All(vm.PullRequestTree, group => Assert.Empty(group.Children));
    }

    [AvaloniaFact]
    public async Task PullRequestsCombineOnlyCurrentRemotesAndLoadEveryPageWithoutEmptyRepositoryNodes()
    {
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            repo.Network.Remotes.Add("origin", "https://github.com/fork/repo.git");
            repo.Network.Remotes.Add("upstream", "git@github.com:team/repo.git");
            repo.Network.Remotes.Add("alias", "https://github.com/TEAM/repo.git");
            repo.Network.Remotes.Add("empty", "https://github.com/empty/repo.git");
            repo.Network.Remotes.Add("azure", "https://dev.azure.com/team/project/_git/repo");
        }
        var provider = new FakeHostingProvider { Login = "me" };
        provider.PageLoader = (repository, _, _, page, _) =>
        {
            if (repository.Owner.Equals("empty", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HostingPage([], false));
            var item = new HostingItem(page == 1 ? 42 : 43, repository.Owner + " change", "", "open", "me",
                $"https://github.com/{repository.Owner}/repo/pull/42", true, new string('a', 40), "feature", "main")
                { Assignees = ["me"], RequestedReviewers = ["me"] };
            return Task.FromResult(new HostingPage([item], repository.Owner == "fork" && page == 1));
        };
        var vm = await fixture.OpenAsync(provider);
        vm.AutoRefresh = false;
        await WaitUntilAsync(() => vm.Remotes.Count == 5);
        vm.GithubToken = "fixture-token";
        await vm.RefreshPullRequestsCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.PullRequests.Count);
        Assert.False(vm.HasMorePullRequests);
        Assert.Equal(4, provider.ListRequests.Count);
        Assert.Single(provider.ListRequests, request => request.Repository.Owner.Equals("team", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, vm.PullRequests.Count(item => item.Number == 42));
        var all = vm.PullRequestTree.Single(node => node.Key == "all");
        Assert.Equal(new[] { "fork/repo", "team/repo" }, all.Children.Select(node => node.Name.ToLowerInvariant()));
        Assert.All(vm.PullRequestTree, group => Assert.DoesNotContain(group.Children, node => node.Name.StartsWith("empty/")));
        Assert.Equal(3, all.Children.SelectMany(node => node.Children).Select(node => node.Key).Distinct().Count());
        vm.SelectedRemote = vm.Remotes.Single(remote => remote.Name == "upstream");
        Assert.Equal(3, vm.PullRequests.Count);
        var fromFork = vm.PullRequests.First(item => item.Repository!.Owner == "fork");
        vm.SelectedPullRequest = fromFork;
        Assert.Equal(fromFork.Repository, provider.CommentRepository);
        vm.HostingComment = "Reviewed fork PR";
        await vm.ApprovePullRequestCommand.ExecuteAsync(null);
        Assert.Equal(fromFork.Repository, provider.ReviewRepository);
        vm.PullRequestSearch = "team/repo";
        Assert.Single(all.Children);
        Assert.Equal("team/repo", all.Children[0].Name.ToLowerInvariant());
        vm.PullRequestSearch = "";
        Assert.Equal(2, all.Children.Count);

        var requests = provider.ListRequests.Count;
        var otherPath = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "OtherRepository");
        Repository.Init(otherPath);
        using (var other = new Repository(otherPath))
        {
            File.WriteAllText(Path.Combine(otherPath, "README.md"), "other\n");
            Commands.Stage(other, "README.md");
            var author = new Signature("Test", "test@example.invalid", DateTimeOffset.UtcNow);
            other.Commit("Other repository", author, author);
        }
        vm.SelectedRepository = new RepositoryInfo(otherPath, "Other repository");
        await WaitUntilAsync(() => vm.HistoryRows.Any(row => row.Subject == "Other repository"));
        await vm.RefreshPullRequestsCommand.ExecuteAsync(null);
        Assert.Empty(vm.Remotes);
        Assert.Empty(vm.PullRequests);
        Assert.All(vm.PullRequestTree, group => Assert.Empty(group.Children));
        Assert.Equal(requests, provider.ListRequests.Count);
        // A manually entered or account-owned repository must not expand the current repository's remote scope.
        vm.GithubOwner = "somewhere-else";
        vm.GithubRepository = "account-repository";
        await vm.RefreshPullRequestsCommand.ExecuteAsync(null);
        Assert.Equal(requests, provider.ListRequests.Count);
    }

    [AvaloniaFact]
    public async Task PullRequestRefreshIgnoresResponsesFromRemovedRemotes()
    {
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath))
            repo.Network.Remotes.Add("origin", "https://github.com/old/repo.git");
        var response = new TaskCompletionSource<HostingPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeHostingProvider { PageLoader = (_, _, _, _, _) => response.Task };
        var vm = await fixture.OpenAsync(provider);
        vm.AutoRefresh = false;
        await WaitUntilAsync(() => vm.Remotes.Count == 1);
        var loading = vm.RefreshPullRequestsCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => provider.ListRequests.Count == 1);
        using (var repo = new Repository(fixture.RepositoryPath))
            repo.Network.Remotes.Remove("origin");
        await vm.RefreshCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.Remotes.Count == 0);
        response.SetResult(new HostingPage([
            new HostingItem(42, "Stale PR", "", "open", "me", "https://github.com/old/repo/pull/42", true, "", "feature", "main")
        ], false));
        await loading;
        Assert.Empty(vm.PullRequests);
        Assert.All(vm.PullRequestTree, group => Assert.Empty(group.Children));
    }

    [AvaloniaFact]
    public async Task PullRequestWorkspaceAutomaticallyShowsEveryRemoteWithoutRepositorySwitching()
    {
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath))
        {
            repo.Network.Remotes.Add("origin", "https://github.com/fork/repo.git");
            repo.Network.Remotes.Add("upstream", "git@github.com:team/repo.git");
        }
        var provider = new FakeHostingProvider
        {
            PageLoader = (repository, _, state, page, _) => Task.FromResult(new HostingPage(
                Enumerable.Range((page - 1) * 50 + 1, page == 1 ? 50 : 5).Select(number =>
                    new HostingItem(number, $"{repository.Owner} {state} change {number}", "", state, "me",
                        $"https://github.com/{repository.Owner}/repo/pull/{number}", true,
                        new string('a', 40), "feature", "main")).ToArray(), page == 1))
        };
        var vm = await fixture.OpenAsync(provider);
        vm.AutoRefresh = false;
        await WaitUntilAsync(() => vm.Remotes.Count == 2);
        var view = new HostingView(true) { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            await WaitUntilAsync(() => vm.PullRequests.Count == 110 && !vm.IsHostingBusy);
            var list = view.FindControl<ListBox>("HostingItems")!;
            Assert.Equal(110, list.Items.Count);
            Assert.Equal(55, vm.PullRequests.Count(pr => pr.Repository!.Owner == "fork"));
            Assert.Equal(55, vm.PullRequests.Count(pr => pr.Repository!.Owner == "team"));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBox>(),
                input => input.IsEffectivelyVisible && input.Watermark is "GitHub owner" or "Repository");
            vm.SelectedPullRequest = vm.PullRequests.First(pr => pr.Repository!.Owner == "fork");
            await WaitUntilAsync(() => vm.HostingComments.Count > 0);
            vm.SelectedRemote = vm.Remotes.Single(remote => remote.Name == "upstream");
            Assert.Contains("fork/repo", vm.HostingDetails);
            Assert.NotEmpty(vm.HostingComments);
            Assert.Equal(110, list.Items.Count);
            vm.HostingState = "all";
            await WaitUntilAsync(() => vm.PullRequests.Count == 110 && !vm.IsHostingBusy
                && vm.PullRequests.All(pr => pr.State == "all"));
            SavePreview(window, "combined-pull-request-workspace.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PullRequestRightClickOpensMenuWithoutNavigatingOrCrashing()
    {
        using var fixture = new WorkspaceFixture();
        using (var repo = new Repository(fixture.RepositoryPath))
            repo.Network.Remotes.Add("origin", "https://github.com/owner/repo.git");
        var vm = await fixture.OpenAsync(new FakeHostingProvider());
        vm.AutoRefresh = false;
        await WaitUntilAsync(() => vm.Remotes.Count == 1);
        await vm.RefreshPullRequestsCommand.ExecuteAsync(null);
        var navigations = new List<string>();
        vm.DockDocument = navigations.Add;
        var nav = new NavigationView { DataContext = vm };
        var window = new Window { Content = nav, Width = 400, Height = 900 };
        try
        {
            window.Show();
            foreach (var expander in nav.GetVisualDescendants().OfType<Expander>().Where(e => e.Classes.Contains("navigation")))
                expander.IsExpanded = expander.Name == "PullRequestCategory";
            Dispatcher.UIThread.RunJobs();
            var tree = nav.FindControl<TreeView>("PullRequestTree")!;
            var row = tree.GetVisualDescendants().OfType<TreeViewItem>().First(r => r.DataContext is PullRequestTreeNode { Item: not null });
            var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Right);
            window.MouseUp(point, Avalonia.Input.MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var menu = row.ContextMenu ?? tree.ContextMenu;
            Assert.NotNull(menu);
            Assert.True(menu.IsOpen);
            Assert.Empty(navigations);
            Assert.Contains(menu.ItemsSource!.Cast<MenuItem>(), item => item.Header!.ToString()!.StartsWith("Merge #42 into"));
            menu.Close();
            window.MouseDown(point, Avalonia.Input.MouseButton.Right);
            window.MouseUp(point, Avalonia.Input.MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.True(tree.ContextMenu!.IsOpen);
            Assert.Empty(navigations);
            tree.ContextMenu.Close();
            tree.SelectedItem = null;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Assert.Equal("Pull Requests", Assert.Single(navigations));
        }
        finally { window.Close(); }
    }

    private sealed class FakeHostingProvider : IHostingProvider
    {
        public Func<HostingRepository, bool, string, int, CancellationToken, Task<HostingPage>>? PageLoader { get; set; }
        public List<(HostingRepository Repository, int Page)> ListRequests { get; } = [];
        public HostingRepository? CommentRepository { get; private set; }
        public HostingRepository? ReviewRepository { get; private set; }
        public string Login { get; set; } = "Contributor";
        public IReadOnlyList<HostingItem>? Items { get; set; }
        public Task<string> AuthenticatedUserAsync(string token, CancellationToken ct = default) => Task.FromResult(Login);
        public bool Offline { get; set; }
        public string? LastReview { get; private set; }
        public string? MergedSha { get; private set; }
        public string IssueState { get; private set; } = "open";

        public Task<HostingPage> ListAsync(
            HostingRepository repository,
            bool pullRequests,
            string state,
            int page,
            string token,
            CancellationToken ct = default
        )
        {
            ListRequests.Add((repository, page));
            if (Offline)
            {
                throw new HttpRequestException("Offline test");
            }

            if (PageLoader is not null) return PageLoader(repository, pullRequests, state, page, ct);
            if (Items is not null) return Task.FromResult(new HostingPage(Items, false));

            return Task.FromResult(
                new HostingPage(
                    [
                        new(
                            42,
                            "Improve authentication",
                            "Test description",
                            pullRequests ? "open" : IssueState,
                            "Contributor",
                            "https://github.com/owner/repo/pull/42",
                            pullRequests,
                            "reviewed-sha",
                            "feature/auth",
                            "main"
                        ),
                    ],
                    false
                )
            );
        }

        public Task<IReadOnlyList<HostingComment>> CommentsAsync(
            HostingRepository repository,
            int number,
            string token,
            CancellationToken ct = default
        )
        {
            CommentRepository = repository;
            return Task.FromResult<IReadOnlyList<HostingComment>>([new("Reviewer", "Looks good", DateTimeOffset.UtcNow)]);
        }

        public Task<HostingItem> CreateAsync(
            HostingRepository repository,
            string title,
            string body,
            string? head,
            string? @base,
            string token,
            CancellationToken ct = default
        ) =>
            Task.FromResult(
                new HostingItem(
                    43,
                    title,
                    body,
                    "open",
                    "Author",
                    "https://github.com/owner/repo/issues/43",
                    head is not null,
                    "new-sha",
                    head ?? "",
                    @base ?? ""
                )
            );

        public Task CommentAsync(
            HostingRepository repository,
            int number,
            string body,
            string token,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task ReviewAsync(
            HostingRepository repository,
            int number,
            string commitSha,
            string action,
            string body,
            string token,
            CancellationToken ct = default
        )
        {
            LastReview = action;
            ReviewRepository = repository;
            return Task.CompletedTask;
        }

        public Task MergeAsync(
            HostingRepository repository,
            int number,
            string expectedSha,
            string token,
            CancellationToken ct = default
        )
        {
            MergedSha = expectedSha;
            return Task.CompletedTask;
        }

        public Task SetIssueStateAsync(
            HostingRepository repository,
            int number,
            string state,
            string token,
            CancellationToken ct = default
        )
        {
            IssueState = state;
            return Task.CompletedTask;
        }
    }
}
