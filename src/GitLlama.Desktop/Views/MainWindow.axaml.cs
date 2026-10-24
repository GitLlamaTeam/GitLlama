using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Dock.Serializer;
using GitLlama.Desktop.Docking;
using GitLlama.Desktop.ViewModels;
using GitLlama.Infrastructure;

namespace GitLlama.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly GitLlamaDockFactory _factory = new();
    private readonly DockSerializer _serializer = new();
    private readonly string _layoutPath;
    private bool _hasActivated;

    public MainWindow()
        : this(null) { }

    public MainWindow(string? layoutDirectory)
    {
        _layoutPath = Path.Combine(layoutDirectory ?? AppDataPaths.SettingsDirectory(), "workspace.json");
        InitializeComponent();
        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty || args.Property == CanResizeProperty)
            {
                ResizeHandles.IsVisible = CanResize && WindowState == WindowState.Normal;
            }

            if (args.Property != WindowStateProperty)
            {
                return;
            }

            WindowMaximizeIcon.Data = Avalonia.Media.StreamGeometry.Parse(
                WindowState == WindowState.Maximized
                    ? "M 3,1 L 11,1 L 11,9 M 1,3 L 9,3 L 9,11 L 1,11 Z"
                    : "M 1,1 L 11,1 L 11,11 L 1,11 Z"
            );
            UpdateNativeShadow();
        };
        WorkspaceDock.Factory = _factory;
        WorkspaceDock.Layout = layout;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            vm.IsOpeningWindow = true;
            vm.DockDocument = ShowDocument;
            vm.ActivateTool = ActivateTool;
            vm.RequestConfirmation = async request => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                ConfirmDialog.ShowAsync(this, request.Title, request.Message, request.Action, destructive: true));
            vm.OpenIntegrationBrowser ??= LaunchIntegrationBrowserAsync;
            vm.SshSession.RequestPassphrase = async (request, ct) =>
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    SshPromptDialog.UnlockAsync(this, request, ct)
                );
            vm.SshSession.RequestHostTrust = async (request, ct) =>
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    SshPromptDialog.TrustHostAsync(this, request, ct)
                );
        };
        Opened += OnOpened;
        _factory.ActiveDockableChanged += (_, args) =>
        {
            if (DataContext is MainWindowViewModel vm && args.Dockable is Document document)
            {
                vm.SelectedTab = document.Title ?? "History";
                if (document.Id == "changes")
                {
                    vm.IsWorkingTreeInspector = true;
                }
                else if (document.Id == "history")
                {
                    vm.IsWorkingTreeInspector = false;
                }
            }
        };
        Closing += (_, _) =>
        {
            SaveLayout();
            (DataContext as MainWindowViewModel)?.StopRepositoryMonitoring();
        };
        Closed += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.CancelNetworkCommand.Execute(null);
                vm.RequestConfirmation = null;
                vm.SshSession.Dispose();
            }
        };
        Activated += async (_, _) =>
        {
            // Startup already opens and reconciles the repository before revealing the workspace.
            if (!_hasActivated)
            {
                _hasActivated = true;
                return;
            }

            if (DataContext is MainWindowViewModel { IsLoading: false } vm)
            {
                await vm.RefreshFromWatcherAsync();
            }
        };
        KeyDown += (_, e) =>
        {
            if (DataContext is MainWindowViewModel { IsSettingsOpen: true } settings)
            {
                if (e.Key == Key.Escape)
                {
                    settings.CloseSettingsCommand.Execute(null);
                    e.Handled = true;
                }

                return;
            }

            if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                OpenRepositoryFolder();
                e.Handled = true;
            }

            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ShowDocument("History");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var search = this.GetVisualDescendants()
                        .OfType<HistoryView>()
                        .FirstOrDefault()
                        ?.FindControl<TextBox>("HistoryCommitSearch");
                    search?.Focus();
                    search?.SelectAll();
                });
                e.Handled = true;
            }

            if (e.Key == Key.Q && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ShowCommandPalette();
                e.Handled = true;
            }
        };
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        UpdateNativeShadow();
        var vm = DataContext as MainWindowViewModel;
        try
        {
            if (vm is not null)
                await vm.Initialization;

            RestoreLayout();
            SynchronizeDocumentTabs();
            if (vm is not null)
                await vm.StartRepositoryMonitoringAsync();
        }
        finally
        {
            if (vm is not null)
                vm.IsOpeningWindow = false;
        }
    }

    private async Task<bool> LaunchIntegrationBrowserAsync(Uri uri) =>
        IntegrationBrowserPolicy.Allows(uri) && await Launcher.LaunchUriAsync(uri);

    private DocumentDock? FindDocuments()
    {
        if (WorkspaceDock.Layout is not { } root)
        {
            return null;
        }

        return FindDocumentsRecursive(root);
    }

    private static DocumentDock? FindDocumentsRecursive(IDockable dockable)
    {
        if (dockable is DocumentDock documents)
        {
            return documents;
        }

        if (dockable is IDock dock && dock.VisibleDockables is { } children)
        {
            foreach (var child in children)
            {
                var found = FindDocumentsRecursive(child);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private void SynchronizeDocumentTabs()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var documents = FindDocuments();
        if (documents?.VisibleDockables is not { } visible)
        {
            return;
        }

        var names = visible
            .OfType<Document>()
            .Select(d => d.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .ToArray();
        vm.Tabs.Clear();
        foreach (var name in names)
        {
            vm.Tabs.Add(name);
        }

        vm.SelectedTab = documents.ActiveDockable?.Title ?? names.FirstOrDefault() ?? "History";
    }

    private void OpenTab_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        ShowDocument(vm.NewTab);
    }

    internal DocumentDock EnsureDocuments()
    {
        if (FindDocuments() is { } existing)
        {
            return existing;
        }

        if (WorkspaceDock.Layout is not IDock root)
        {
            throw new InvalidOperationException("Workspace is unavailable.");
        }

        var main = root.VisibleDockables?.OfType<ProportionalDock>().FirstOrDefault();
        if (main is null)
        {
            main = new ProportionalDock
            {
                Id = "main",
                Orientation = Dock.Model.Core.Orientation.Horizontal,
                VisibleDockables = _factory.CreateList<IDockable>(),
            };
            _factory.AddDockable(root, main);
            root.ActiveDockable = main;
        }

        var documents = new DocumentDock
        {
            Id = "documents",
            Title = "Documents",
            Proportion = .53,
            CanCloseLastDockable = false,
            VisibleDockables = _factory.CreateList<IDockable>(),
        };
        if (
            WalkDockables(main).OfType<ProportionalDock>().FirstOrDefault(d => d.Id == "document-workspace") is
            { } workspace
        )
        {
            _factory.AddDockable(workspace, documents);
            return documents;
        }

        _factory.AddDockable(main, documents);
        var children = main.VisibleDockables!;
        children.Remove(documents);
        var right = children.FirstOrDefault(d => d.Id == "right-tools");
        children.Insert(right is null ? children.Count : Math.Max(0, children.IndexOf(right) - 1), documents);
        return documents;
    }

    internal void ShowDocument(string title)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (title == "Settings")
        {
            vm.IsSettingsOpen = true;
            if (string.IsNullOrWhiteSpace(vm.AuthenticationTestUrl))
            {
                vm.AuthenticationTestUrl = vm.SelectedRemote?.Url ?? "";
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                this.GetVisualDescendants()
                    .OfType<SettingsView>()
                    .LastOrDefault()
                    ?.FindControl<Button>("CloseSettingsButton")
                    ?.Focus()
            );
            return;
        }

        if (title == "Repository")
        {
            var empty = vm.RepositoryWorkspaceTabs.FirstOrDefault(tab => tab.IsEmpty);
            if (empty is null)
            {
                vm.NewRepositoryTab();
            }
            else
            {
                vm.SelectedRepositoryWorkspaceTab = empty;
            }

            return;
        }

        if (vm.SelectedRepository is { } current)
        {
            SelectRepositoryTab(current);
        }

        if (title == "Changes")
        {
            vm.ShowWorkingTreeCommand.Execute(null);
            return;
        }

        if (!vm.AvailableTabs.Contains(title))
        {
            return;
        }

        vm.CenterSection = title;
        vm.IsFileReview = false;
        vm.SelectedTab = title;
        var requestedTitle = title;
        title = "History";
        var documents = EnsureDocuments();
        var id = title.ToLowerInvariant();
        var existing = documents.VisibleDockables!.OfType<Document>().FirstOrDefault(d => d.Id == id);
        if (existing is null)
        {
            // A floating document must return to the main workspace, rather than opening a duplicate.
            existing = (WorkspaceDock.Layout as IRootDock)
                ?.Windows?.SelectMany(w => WalkDockables(w.Layout))
                .OfType<Document>()
                .FirstOrDefault(d => d.Id == id);
            if (existing is not null)
            {
                _factory.RemoveDockable(existing, false);
            }

            existing ??= new Document { Id = id, Title = title };
            _factory.AddDockable(documents, existing);
        }

        _factory.SetActiveDockable(existing);
        documents.ActiveDockable = existing;
        if (!vm.Tabs.Contains(title))
        {
            vm.Tabs.Add(title);
        }

        vm.SelectedTab = requestedTitle;
        if (requestedTitle == "History")
        {
            vm.IsWorkingTreeInspector = false;
            ActivateTool("repositories");
        }

        SaveLayout();
    }

    private void ActivateTool(string id)
    {
        var tool = WalkDockables(WorkspaceDock.Layout).FirstOrDefault(d => d.Id == id);
        if (tool is null)
        {
            return;
        }

        // Select the tool and each containing group, preserving the user's dock arrangement.
        for (var dockable = tool; dockable.Owner is IDock owner; dockable = owner)
        {
            _factory.SetActiveDockable(dockable);
            owner.ActiveDockable = dockable;
        }
    }

    internal static IEnumerable<IDockable> WalkDockables(IDockable? dockable)
    {
        if (dockable is null)
        {
            yield break;
        }

        yield return dockable;
        if (dockable is IDock dock && dock.VisibleDockables is { } children)
        {
            foreach (var child in children.ToArray())
            {
                foreach (var descendant in WalkDockables(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    internal void DockAllDocuments()
    {
        var floating =
            (WorkspaceDock.Layout as IRootDock)
                ?.Windows?.SelectMany(w => WalkDockables(w.Layout))
                .OfType<Document>()
                .Select(d => d.Title)
                .ToArray()
            ?? [];
        foreach (var title in floating)
        {
            if (title is not null)
            {
                ShowDocument(title);
            }
        }

        ShowDocument("History");
    }

    private void DockDocuments_Click(object? sender, RoutedEventArgs e) => DockAllDocuments();

    private void ManageRepositories_Click(object? sender, RoutedEventArgs e) =>
        (DataContext as MainWindowViewModel)?.NewRepositoryTab();

    private void RepositoryTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (
            DataContext is MainWindowViewModel vm
            && e.AddedItems.OfType<RepositoryWorkspaceTab>().FirstOrDefault() is { } tab
        )
        {
            vm.SelectedRepositoryWorkspaceTab = tab;
        }
    }

    internal void SelectRepositoryTab(GitLlama.Core.RepositoryInfo repository)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.Repositories.Contains(repository))
        {
            return;
        }

        var tab = vm.RepositoryWorkspaceTabs.FirstOrDefault(item => item.Repository?.Path == repository.Path);
        if (tab is not null)
        {
            vm.SelectedRepositoryWorkspaceTab = tab;
        }
        else
        {
            vm.OpenRepositoryTab(repository);
        }
    }

    private void RepositoryTab_Tapped(object? sender, TappedEventArgs e)
    {
        if (
            DataContext is MainWindowViewModel vm
            && (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext
                is RepositoryWorkspaceTab tab
        )
        {
            vm.SelectedRepositoryWorkspaceTab = tab;
        }
    }

    private void CloseRepositoryManager_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedRepositoryWorkspaceTab is { } tab)
        {
            vm.CloseWorkspaceTab(tab);
        }

        e.Handled = true;
    }

    private void CloseRepositoryTab_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Button { Tag: RepositoryWorkspaceTab tab })
        {
            vm.CloseWorkspaceTab(tab);
        }

        e.Handled = true;
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object? sender, RoutedEventArgs e) => Close();

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.GetPosition(this).Y <= WindowFrame.Margin.Top + 5
        )
        {
            return;
        }

        if (
            e.Source is Control source
            && source.GetSelfAndVisualAncestors().Any(c => c is Button or TextBox or MenuItem or Menu or ListBoxItem)
        )
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, new RoutedEventArgs());
        }
        else
        {
            BeginMoveDrag(e);
        }

        e.Handled = true;
    }

    private void WindowEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanResize || WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is Control { Tag: string direction } && Enum.TryParse<WindowEdge>(direction, out var edge))
        {
            e.Handled = true;
            BeginResizeDrag(edge, e);
        }
    }

    private void ShowDocument_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string title })
        {
            ShowDocument(title);
        }
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => ShowDocument("Settings");

    private void ViewChanges_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsWorkingTreeInspector = true;
        }

        ShowDocument("Changes");
    }

    private async void RepositoryCrumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RepositoryCrumb crumb } button || DataContext is not MainWindowViewModel vm)
            return;
        if (crumb.Repository.Parent is not { } parent)
        {
            vm.NavigateToRepositoryCommand.Execute(crumb.Repository);
            return;
        }

        var current = vm.SelectedRepository;
        try
        {
            var modules = await new GitLlama.Git.GitSubmoduleService().ListAsync(parent.Path);
            if (vm.SelectedRepository != current) return;
            var items = modules.OrderBy(module => module.Path, StringComparer.OrdinalIgnoreCase).Select(module =>
            {
                var selected = string.Equals(Path.GetFullPath(Path.Combine(parent.Path, module.Path)), crumb.Repository.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                return new MenuItem
                {
                    Header = (selected ? "✓  " : "") + module.Path + (module.IsCheckedOut ? "" : " (not initialized)"),
                    IsEnabled = !selected,
                    Command = vm.SwitchSubmoduleCommand,
                    CommandParameter = new SubmoduleNavigation(parent, module)
                };
            }).ToList();
            if (items.Count == 0) items.Add(new MenuItem { Header = "No submodules", IsEnabled = false });
            button.ContextMenu = new ContextMenu
            {
                ItemsSource = items,
                Placement = PlacementMode.Bottom
            };
            button.ContextMenu.Open(button);
        }
        catch (Exception error) { vm.Status = vm.PresentError(error); }
    }

    private void BranchPicker_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var items = vm
            .BranchLines.Where(branch =>
                !vm.Remotes.Any(remote => branch.StartsWith(remote.Name + "/", StringComparison.Ordinal))
            )
            .Select(branch =>
            {
                var item = new MenuItem
                {
                    Header = (branch == vm.CurrentBranch ? "✓  " : "⑂  ") + branch,
                    IsEnabled = branch != vm.CurrentBranch,
                };
                item.Click += async (_, _) =>
                {
                    vm.SelectedBranchLine = branch;
                    await vm.CheckoutBranchCommand.ExecuteAsync(null);
                };
                return item;
            })
            .ToList();
        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = "No local branches", IsEnabled = false });
        }

        button.ContextMenu = new ContextMenu { ItemsSource = items };
        button.ContextMenu.Open(button);
    }

    private void UpstreamPicker_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        button.ContextMenu = new ContextMenu { ItemsSource = UpstreamMenu.Items(this, vm) };
        button.ContextMenu.Open(button);
    }

    private async void OpenPath_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var path = new TextBox { Watermark = "Local repository path", MinWidth = 400 };
        var dialog = new Window
        {
            Title = "Open repository",
            Width = 500,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var open = new Button { Content = "Open repository", HorizontalAlignment = HorizontalAlignment.Right };
        open.Classes.Add("workspace");
        open.Click += async (_, _) =>
        {
            vm.RepositoryPath = path.Text ?? "";
            await vm.AddRepositoryCommand.ExecuteAsync(null);
            if (string.IsNullOrWhiteSpace(vm.RepositoryPath))
            {
                dialog.Close();
            }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children = { path, open },
        };
        await dialog.ShowDialog(this);
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var documents = FindDocuments();
        if (documents?.VisibleDockables is not { } tabs || tabs.Count <= 1)
        {
            return;
        }

        var selected = documents.ActiveDockable as Document;
        if (selected is null)
        {
            return;
        }

        tabs.Remove(selected);
        documents.ActiveDockable = tabs.OfType<Document>().FirstOrDefault();
        vm.Tabs.Remove(selected.Title ?? "");
        vm.SelectedTab = documents.ActiveDockable?.Title ?? "History";
        SaveLayout();
    }

    private string NamedLayoutPath()
    {
        var name = (DataContext as MainWindowViewModel)?.SelectedLayout ?? "Standard";
        return Path.Combine(Path.GetDirectoryName(_layoutPath)!, $"layout-{name.Replace(' ', '-')}.json");
    }

    private void SaveLayout_Click(object? sender, RoutedEventArgs e) => SaveLayout(NamedLayoutPath());

    private void LoadLayout_Click(object? sender, RoutedEventArgs e) => RestoreLayout(NamedLayoutPath());

    private void ResetLayout_Click(object? sender, RoutedEventArgs e)
    {
        CloseFloatingWindows();
        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        WorkspaceDock.Layout = layout;
        SynchronizeDocumentTabs();
        SaveLayout();
    }

    private void CloseFloatingWindows()
    {
        if (WorkspaceDock.Layout is IRootDock root && root.Windows is { } windows)
        {
            foreach (var floating in windows.ToArray())
            {
                _factory.CloseWindow(floating);
            }
        }
    }

    private void RestoreLayout() => RestoreLayout(_layoutPath);

    private void RestoreLayout(string path)
    {
        if (!File.Exists(path))
        {
            if (path != _layoutPath)
            {
                var preset = _factory.CreatePreset((DataContext as MainWindowViewModel)?.SelectedLayout ?? "Standard");
                _factory.InitLayout(preset);
                WorkspaceDock.Layout = preset;
                SynchronizeDocumentTabs();
                if (DataContext is MainWindowViewModel vm && vm.SelectedLayout == "Merge Resolution")
                {
                    ShowDocument("Branches");
                }
            }

            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var layout = _serializer.Load<IDock?>(stream);
            if (layout is null)
            {
                return;
            }

            if (layout.Id is "root" or "root-v1" or "root-v2")
            {
                if (!File.Exists(path + ".previous"))
                {
                    File.Copy(path, path + ".previous");
                }

                layout = _factory.CreateLayout();
            }

            if (layout.Id != "root-v3")
            {
                throw new InvalidDataException("Unsupported workspace layout schema.");
            }

            RemoveChangesDocuments(layout);
            _factory.InitLayout(layout);
            WorkspaceDock.Layout = layout;
            SynchronizeDocumentTabs();
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Restore dock layout");
            // Corrupt or incompatible layouts must never prevent the app from opening.
            // The default workspace remains available for this session.
        }
    }

    internal static void RemoveChangesDocuments(IDock layout)
    {
        var roots = new List<IDockable> { layout };
        var obsoleteFloating = new HashSet<IDockWindow>();
        if (layout is IRootDock root && root.Windows is { } windows)
        {
            foreach (var window in windows.Where(w => WalkDockables(w.Layout).Any(d => d.Id == "changes")))
            {
                obsoleteFloating.Add(window);
            }

            roots.AddRange(windows.Where(w => w.Layout is not null).Select(w => (IDockable)w.Layout!));
        }

        foreach (var dock in roots.SelectMany(WalkDockables).OfType<IDock>())
        {
            if (dock.VisibleDockables is not { } items)
            {
                continue;
            }

            foreach (var obsolete in items.Where(item => item.Id == "changes").ToArray())
            {
                items.Remove(obsolete);
                if (dock.ActiveDockable == obsolete)
                {
                    dock.ActiveDockable = items.FirstOrDefault();
                }

                if (dock.DefaultDockable == obsolete)
                {
                    dock.DefaultDockable = items.FirstOrDefault();
                }
            }
        }

        if (
            !roots.SelectMany(WalkDockables).OfType<Document>().Any()
            && WalkDockables(layout).OfType<DocumentDock>().FirstOrDefault() is { } documents
        )
        {
            var history = new Document { Id = "history", Title = "History" };
            documents.VisibleDockables!.Add(history);
            documents.ActiveDockable = history;
        }

        if (layout is IRootDock workspace && workspace.Windows is { } floatingWindows)
        {
            foreach (
                var floating in floatingWindows
                    .Where(w => obsoleteFloating.Contains(w) && !WalkDockables(w.Layout).OfType<Document>().Any())
                    .ToArray()
            )
            {
                floatingWindows.Remove(floating);
            }
        }
    }

    private void SaveLayout() => SaveLayout(_layoutPath);

    private void SaveLayout(string path)
    {
        if (WorkspaceDock.Layout is not { } layout)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                _serializer.Save(stream, layout);
            }

            File.Move(temporary, path, true);
        }
        catch (IOException e)
        {
            FileLog.Error(e, "Save dock layout");
        }
        catch (UnauthorizedAccessException e)
        {
            FileLog.Error(e, "Save dock layout");
        }
        catch (Exception e)
        {
            FileLog.Error(e, "Serialize dock layout");
        }
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e) => OpenRepositoryFolder();

    internal async void OpenRepositoryFolder()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Open Git repository", AllowMultiple = false }
            );
            if (folders.Count == 0)
            {
                return;
            }

            var path = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                vm.Status = "The selected folder does not have a local filesystem path.";
                return;
            }

            vm.RepositoryPath = path;
            await vm.AddRepositoryCommand.ExecuteAsync(null);
        }
        catch (Exception exception)
        {
            FileLog.Error(exception, "Open repository folder picker");
            vm.Status = "Could not open repository folder: " + exception.Message;
        }
    }
}
