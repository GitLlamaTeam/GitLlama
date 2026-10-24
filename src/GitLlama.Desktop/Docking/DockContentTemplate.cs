using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using GitLlama.Desktop.ViewModels;
using GitLlama.Desktop.Views;

namespace GitLlama.Desktop.Docking;

/// <summary>Maps stable dockable IDs to actual repository-aware content.</summary>
public sealed class DockContentTemplate(MainWindowViewModel workspace) : IDataTemplate
{
    public bool Match(object? data) => data is Tool or Document;

    public Control Build(object? data)
    {
        var id = data switch
        {
            Tool tool => tool.Id,
            Document document => document.Id,
            _ => null,
        };
        Control? polished = id switch
        {
            "repositories" => new NavigationView(),
            "history" => new ContextCenterView(),
            "inspector" => new InspectorView(),
            "output" => CreateOutput(),
            "repository" => new RepositoryView(),
            "worktrees" => new WorktreesView(),
            "reflog" => new ReflogView(),
            "files" => new FilesView(),
            "pull requests" => new HostingView(true),
            "issues" => new HostingView(false),
            "remotes" => new RemotesView(),
            "settings" => new SettingsView(),
            "branches" => new BranchesView(),
            "tags" => new TagsView(),
            "stashes" => new StashesView(),
            "submodules" => new SubmodulesView(),
            _ => null,
        };
        if (polished is not null)
        {
            polished.DataContext = workspace;
            return polished;
        }

        var root = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            DataContext = workspace,
        };
        var heading = new TextBlock { Text = id?.ToUpperInvariant(), FontWeight = FontWeight.SemiBold };
        root.Children.Add(heading);
        switch (id)
        {
            case "repositories":
                var repositories = new ListBox { MinHeight = 100 };
                repositories.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(workspace.Repositories)));
                repositories.Bind(
                    Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
                    new Binding(nameof(workspace.SelectedRepository)) { Mode = BindingMode.TwoWay }
                );
                repositories.ItemTemplate = new FuncDataTemplate<GitLlama.Core.RepositoryInfo>(
                    (repo, _) =>
                        new TextBlock { Text = repo is null ? "" : (repo.IsFavorite ? "★  " : "    ") + repo.Name }
                );
                root.Children.Add(repositories);
                break;
            case "advanced history":
                root.Children.Add(
                    new TextBlock { Text = "History inspection and advanced operations", FontWeight = FontWeight.Bold }
                );
                var historySearch = new TextBox { Watermark = "Filter commit graph by SHA, subject or ref" };
                historySearch.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.HistorySearch)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(historySearch);
                var historyGraph = new ListBox { MinHeight = 190 };
                historyGraph.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(workspace.HistoryGraphLines)));
                historyGraph.Bind(
                    Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
                    new Binding(nameof(workspace.SelectedHistoryGraphLine)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(historyGraph);
                root.Children.Add(new TextBlock { Text = "Selected commit", FontWeight = FontWeight.SemiBold });
                var historyDetails = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 120,
                    TextWrapping = TextWrapping.Wrap,
                };
                historyDetails.Bind(TextBox.TextProperty, new Binding(nameof(workspace.HistoryCommitDetails)));
                root.Children.Add(historyDetails);
                root.Children.Add(
                    new TextBlock { Text = "Changed files · select to preview", FontWeight = FontWeight.SemiBold }
                );
                var historyFiles = new ListBox { MinHeight = 110 };
                historyFiles.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(workspace.HistoryFiles)));
                historyFiles.Bind(
                    Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
                    new Binding(nameof(workspace.SelectedHistoryFile)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(historyFiles);
                var historyContentToggle = new CheckBox { Content = "Show historical file content instead of patch" };
                historyContentToggle.Bind(
                    Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,
                    new Binding(nameof(workspace.HistoryShowContent)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(historyContentToggle);
                var historyPreview = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 220,
                    TextWrapping = TextWrapping.NoWrap,
                };
                historyPreview.Bind(TextBox.TextProperty, new Binding(nameof(workspace.HistoryFilePreview)));
                root.Children.Add(historyPreview);
                root.Children.Add(
                    new TextBlock { Text = "Advanced history operations", FontWeight = FontWeight.SemiBold }
                );
                AddList(root, nameof(workspace.CommitLines), nameof(workspace.SelectedCommitLine));
                root.Children.Add(new TextBlock { Text = "Historical file content", FontWeight = FontWeight.SemiBold });
                var fileContentCommit = new TextBox { Watermark = "Commit SHA" };
                fileContentCommit.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.FileContentCommitSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(fileContentCommit);
                var fileContentPath = new TextBox { Watermark = "Repository-relative file path" };
                fileContentPath.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.FileContentPath)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(fileContentPath);
                var fileContentAction = new Button { Content = "Preview historical file" };
                fileContentAction.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.InspectFileContentCommand))
                );
                root.Children.Add(fileContentAction);
                var fileContentResult = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 160,
                    TextWrapping = TextWrapping.NoWrap,
                };
                fileContentResult.Bind(TextBox.TextProperty, new Binding(nameof(workspace.FileContentSummary)));
                root.Children.Add(fileContentResult);

                root.Children.Add(new TextBlock { Text = "Commit file patch", FontWeight = FontWeight.SemiBold });
                var filePatchCommit = new TextBox { Watermark = "Commit SHA" };
                filePatchCommit.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.FilePatchCommitSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(filePatchCommit);
                var filePatchPath = new TextBox { Watermark = "Repository-relative file path" };
                filePatchPath.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.FilePatchPath)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(filePatchPath);
                var filePatchAction = new Button { Content = "Inspect file patch" };
                filePatchAction.Bind(Button.CommandProperty, new Binding(nameof(workspace.InspectFilePatchCommand)));
                root.Children.Add(filePatchAction);
                var filePatchResult = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 160,
                    TextWrapping = TextWrapping.NoWrap,
                };
                filePatchResult.Bind(TextBox.TextProperty, new Binding(nameof(workspace.FilePatchSummary)));
                root.Children.Add(filePatchResult);

                root.Children.Add(
                    new TextBlock { Text = "Per-file commit statistics", FontWeight = FontWeight.SemiBold }
                );
                var fileStatisticsInput = new TextBox { Watermark = "Commit SHA" };
                fileStatisticsInput.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.FileStatisticsCommitSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(fileStatisticsInput);
                var fileStatisticsAction = new Button { Content = "Inspect file statistics" };
                fileStatisticsAction.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.InspectFileStatisticsCommand))
                );
                root.Children.Add(fileStatisticsAction);
                var fileStatisticsResult = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                fileStatisticsResult.Bind(TextBox.TextProperty, new Binding(nameof(workspace.FileStatisticsSummary)));
                root.Children.Add(fileStatisticsResult);

                root.Children.Add(new TextBlock { Text = "Commit changed files", FontWeight = FontWeight.SemiBold });
                var changedFilesInput = new TextBox { Watermark = "Commit SHA" };
                changedFilesInput.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.ChangedFilesCommitSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(changedFilesInput);
                var changedFilesAction = new Button { Content = "Inspect changed files" };
                changedFilesAction.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.InspectChangedFilesCommand))
                );
                root.Children.Add(changedFilesAction);
                var changedFilesResult = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                changedFilesResult.Bind(TextBox.TextProperty, new Binding(nameof(workspace.ChangedFilesSummary)));
                root.Children.Add(changedFilesResult);

                root.Children.Add(new TextBlock { Text = "Commit diff statistics", FontWeight = FontWeight.SemiBold });
                var statisticsInput = new TextBox { Watermark = "Commit SHA" };
                statisticsInput.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.DiffStatisticsCommitSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(statisticsInput);
                var statisticsAction = new Button { Content = "Inspect change statistics" };
                statisticsAction.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.InspectDiffStatisticsCommand))
                );
                root.Children.Add(statisticsAction);
                var statisticsResult = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                statisticsResult.Bind(TextBox.TextProperty, new Binding(nameof(workspace.DiffStatisticsSummary)));
                root.Children.Add(statisticsResult);

                root.Children.Add(
                    new TextBlock { Text = "Commit relationship inspection", FontWeight = FontWeight.SemiBold }
                );
                var relationshipFirst = new TextBox { Watermark = "First commit SHA" };
                relationshipFirst.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.RelationshipFirstSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(relationshipFirst);
                var relationshipSecond = new TextBox { Watermark = "Second commit SHA" };
                relationshipSecond.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.RelationshipSecondSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(relationshipSecond);
                var relationshipAction = new Button { Content = "Compare commits" };
                relationshipAction.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.InspectRelationshipCommand))
                );
                root.Children.Add(relationshipAction);
                var relationshipResult = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                relationshipResult.Bind(TextBox.TextProperty, new Binding(nameof(workspace.RelationshipSummary)));
                root.Children.Add(relationshipResult);

                root.Children.Add(new TextBlock { Text = "Merge base inspection", FontWeight = FontWeight.SemiBold });
                var mergeBaseFirst = new TextBox { Watermark = "First commit SHA" };
                mergeBaseFirst.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.MergeBaseFirstSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(mergeBaseFirst);
                var mergeBaseSecond = new TextBox { Watermark = "Second commit SHA" };
                mergeBaseSecond.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.MergeBaseSecondSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(mergeBaseSecond);
                var mergeBaseAction = new Button { Content = "Find common ancestor" };
                mergeBaseAction.Bind(Button.CommandProperty, new Binding(nameof(workspace.InspectMergeBaseCommand)));
                root.Children.Add(mergeBaseAction);
                var mergeBaseResult = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                mergeBaseResult.Bind(TextBox.TextProperty, new Binding(nameof(workspace.MergeBaseSummary)));
                root.Children.Add(mergeBaseResult);

                root.Children.Add(
                    new TextBlock { Text = "Commit descendants inspection", FontWeight = FontWeight.SemiBold }
                );
                var descendantsInput = new TextBox { Watermark = "Ancestor commit SHA" };
                descendantsInput.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.DescendantCommitSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(descendantsInput);
                var descendantsAction = new Button { Content = "Inspect descendants" };
                descendantsAction.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.InspectDescendantsCommand))
                );
                root.Children.Add(descendantsAction);
                var descendantsResult = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                descendantsResult.Bind(TextBox.TextProperty, new Binding(nameof(workspace.DescendantSummary)));
                root.Children.Add(descendantsResult);

                root.Children.Add(
                    new TextBlock { Text = "Commit ancestry inspection", FontWeight = FontWeight.SemiBold }
                );
                var ancestryInput = new TextBox { Watermark = "Commit SHA" };
                ancestryInput.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.AncestryCommitSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(ancestryInput);
                var ancestryAction = new Button { Content = "Inspect ancestry" };
                ancestryAction.Bind(Button.CommandProperty, new Binding(nameof(workspace.InspectAncestryCommand)));
                root.Children.Add(ancestryAction);
                var ancestryResult = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                ancestryResult.Bind(TextBox.TextProperty, new Binding(nameof(workspace.AncestrySummary)));
                root.Children.Add(ancestryResult);

                root.Children.Add(new TextBlock { Text = "Inspection", FontWeight = FontWeight.SemiBold });
                var action41 = new Button { Content = "Inspect Tags" };
                action41.Bind(Button.CommandProperty, new Binding(nameof(workspace.InspectTagsCommand)));
                root.Children.Add(action41);
                var result41 = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                result41.Bind(TextBox.TextProperty, new Binding(nameof(workspace.TagInspectionSummary)));
                root.Children.Add(result41);

                root.Children.Add(new TextBlock { Text = "Inspection", FontWeight = FontWeight.SemiBold });
                var input40 = new TextBox { Watermark = "Repository-relative file path" };
                input40.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.FileHistoryPath)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(input40);
                var action40 = new Button { Content = "Load File History" };
                action40.Bind(Button.CommandProperty, new Binding(nameof(workspace.LoadFileHistoryCommand)));
                root.Children.Add(action40);
                var result40 = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                result40.Bind(TextBox.TextProperty, new Binding(nameof(workspace.FileHistorySummary)));
                root.Children.Add(result40);

                root.Children.Add(new TextBlock { Text = "Inspection", FontWeight = FontWeight.SemiBold });
                var input39 = new TextBox { Watermark = "Repository-relative file path" };
                input39.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.BlamePath)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(input39);
                var action39 = new Button { Content = "Inspect Blame" };
                action39.Bind(Button.CommandProperty, new Binding(nameof(workspace.InspectBlameCommand)));
                root.Children.Add(action39);
                var result39 = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                result39.Bind(TextBox.TextProperty, new Binding(nameof(workspace.BlameSummary)));
                root.Children.Add(result39);

                root.Children.Add(new TextBlock { Text = "Inspection", FontWeight = FontWeight.SemiBold });
                var action37 = new Button { Content = "Load Graph Lanes" };
                action37.Bind(Button.CommandProperty, new Binding(nameof(workspace.LoadGraphLanesCommand)));
                root.Children.Add(action37);
                var result37 = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                result37.Bind(TextBox.TextProperty, new Binding(nameof(workspace.GraphLaneSummary)));
                root.Children.Add(result37);

                root.Children.Add(new TextBlock { Text = "Inspection", FontWeight = FontWeight.SemiBold });
                var input38 = new TextBox { Watermark = "Search message, author or SHA" };
                input38.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.CommitSearchQuery)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(input38);
                var action38 = new Button { Content = "Search Commits" };
                action38.Bind(Button.CommandProperty, new Binding(nameof(workspace.SearchCommitsCommand)));
                root.Children.Add(action38);
                var result38 = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                result38.Bind(TextBox.TextProperty, new Binding(nameof(workspace.CommitSearchSummary)));
                root.Children.Add(result38);

                root.Children.Add(
                    new TextBlock { Text = "Commit graph (parents and refs)", FontWeight = FontWeight.SemiBold }
                );
                var loadGraph = new Button { Content = "Load commit graph" };
                loadGraph.Bind(Button.CommandProperty, new Binding(nameof(workspace.LoadCommitGraphCommand)));
                root.Children.Add(loadGraph);
                var graphDetails = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 130,
                    TextWrapping = TextWrapping.Wrap,
                };
                graphDetails.Bind(TextBox.TextProperty, new Binding(nameof(workspace.CommitGraphSummary)));
                root.Children.Add(graphDetails);
                root.Children.Add(
                    new TextBlock { Text = "Rebase conflicts (read-only)", FontWeight = FontWeight.SemiBold }
                );
                var inspectRebase = new Button { Content = "Inspect rebase conflicts" };
                inspectRebase.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.InspectRebaseConflictsCommand))
                );
                root.Children.Add(inspectRebase);
                var rebaseConflicts = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 90,
                    TextWrapping = TextWrapping.Wrap,
                };
                rebaseConflicts.Bind(TextBox.TextProperty, new Binding(nameof(workspace.RebaseConflictSummary)));
                root.Children.Add(rebaseConflicts);
                root.Children.Add(
                    new TextBlock
                    {
                        Text = "Interactive rebase plan preview (read-only)",
                        FontWeight = FontWeight.SemiBold,
                    }
                );
                var rebaseUpstream = new TextBox { Watermark = "Upstream ancestor commit SHA" };
                rebaseUpstream.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.RebaseUpstreamSha)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(rebaseUpstream);
                var previewRebase = new Button { Content = "Preview rebase steps" };
                previewRebase.Bind(Button.CommandProperty, new Binding(nameof(workspace.PreviewRebasePlanCommand)));
                root.Children.Add(previewRebase);
                var rebasePreview = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                };
                rebasePreview.Bind(TextBox.TextProperty, new Binding(nameof(workspace.RebasePlanPreview)));
                root.Children.Add(rebasePreview);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Create a local branch pointing to the selected historical commit, without checking it out or changing files.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var branchFromCommitName = new TextBox { Watermark = "New branch name (e.g. recovery/old-work)" };
                branchFromCommitName.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.BranchFromCommitName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(branchFromCommitName);
                var createBranchFromCommit = new Button { Content = "Create branch from selected commit" };
                createBranchFromCommit.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.CreateBranchFromSelectedCommitCommand))
                );
                root.Children.Add(createBranchFromCommit);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Cherry-pick the selected non-merge commit onto the current branch. Requires a clean working tree; conflicts must be resolved manually.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var cherryPick = new Button { Content = "Cherry-pick selected commit" };
                cherryPick.Bind(Button.CommandProperty, new Binding(nameof(workspace.CherryPickSelectedCommitCommand)));
                root.Children.Add(cherryPick);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Revert the selected non-merge commit by creating a new inverse commit on the current branch. Requires a clean working tree; conflicts must be resolved manually.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var revertCommit = new Button { Content = "Revert selected commit" };
                revertCommit.Bind(Button.CommandProperty, new Binding(nameof(workspace.RevertSelectedCommitCommand)));
                root.Children.Add(revertCommit);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Soft reset to a selected ancestor: moves local HEAD, preserves staged changes and working files. Published HEAD commits are blocked.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var softReset = new Button { Content = "Soft reset to selected ancestor" };
                softReset.Bind(Button.CommandProperty, new Binding(nameof(workspace.SoftResetToSelectedCommitCommand)));
                root.Children.Add(softReset);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Mixed reset to a selected ancestor: moves local HEAD and unstages changes, but preserves working files. Published HEAD commits are blocked.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var mixedReset = new Button { Content = "Mixed reset to selected ancestor" };
                mixedReset.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.MixedResetToSelectedCommitCommand))
                );
                root.Children.Add(mixedReset);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "DANGER — hard reset moves HEAD and replaces tracked working files and index with the selected ancestor. Later local commits become unreachable from this branch. Requires a clean tree; published HEAD is blocked.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var hardReset = new Button { Content = "Hard reset to selected ancestor (destructive)" };
                hardReset.Bind(Button.CommandProperty, new Binding(nameof(workspace.HardResetToSelectedCommitCommand)));
                root.Children.Add(hardReset);
                root.Children.Add(
                    new TextBlock { Text = "Lightweight tags (current HEAD)", FontWeight = FontWeight.SemiBold }
                );
                var tagName = new TextBox { Watermark = "Tag name (e.g. v1.0.0)" };
                tagName.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.NewTagName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(tagName);
                var createTag = new Button { Content = "Create lightweight tag" };
                createTag.Bind(Button.CommandProperty, new Binding(nameof(workspace.CreateLightweightTagCommand)));
                root.Children.Add(createTag);
                root.Children.Add(
                    new TextBlock { Text = "Annotated tag (current HEAD)", FontWeight = FontWeight.SemiBold }
                );
                var annotatedName = new TextBox { Watermark = "Annotated tag name (e.g. v2.0.0)" };
                annotatedName.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.AnnotatedTagName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(annotatedName);
                var annotatedMessage = new TextBox
                {
                    Watermark = "Tag annotation message",
                    AcceptsReturn = true,
                    MinHeight = 55,
                };
                annotatedMessage.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.AnnotatedTagMessage)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(annotatedMessage);
                var annotatedAuthor = new TextBox { Watermark = "Tagger name" };
                annotatedAuthor.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.AnnotatedTagAuthorName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(annotatedAuthor);
                var annotatedEmail = new TextBox { Watermark = "Tagger email" };
                annotatedEmail.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.AnnotatedTagAuthorEmail)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(annotatedEmail);
                var createAnnotatedTag = new Button { Content = "Create annotated tag" };
                createAnnotatedTag.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.CreateAnnotatedTagCommand))
                );
                root.Children.Add(createAnnotatedTag);
                var tagList = new ListBox { MinHeight = 80 };
                tagList.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(workspace.Tags)));
                tagList.Bind(
                    Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
                    new Binding(nameof(workspace.SelectedTag)) { Mode = BindingMode.TwoWay }
                );
                tagList.ItemTemplate = new FuncDataTemplate<GitLlama.Git.GitTagInfo>(
                    (tag, _) =>
                        new TextBlock
                        {
                            Text = tag is null
                                ? ""
                                : tag.Name + "  " + tag.TargetSha[..7] + (tag.IsAnnotated ? " (annotated)" : ""),
                        }
                );
                root.Children.Add(tagList);
                root.Children.Add(
                    new TextBlock
                    {
                        Text = "Remote tags: select a local tag above and a remote in the Remotes panel.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var pushTag = new Button { Content = "Push selected tag to selected remote (no force)" };
                pushTag.Bind(Button.CommandProperty, new Binding(nameof(workspace.PushSelectedTagCommand)));
                root.Children.Add(pushTag);
                root.Children.Add(
                    new TextBlock
                    {
                        Text = "Delete the selected tag from the selected remote after confirmation. The local tag is preserved.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var deleteRemoteTag = new Button { Content = "Delete selected tag from remote" };
                deleteRemoteTag.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.DeleteSelectedRemoteTagCommand))
                );
                root.Children.Add(deleteRemoteTag);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Delete local tag: select a tag. Remote tags are unaffected.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var deleteTag = new Button { Content = "Delete selected local tag" };
                deleteTag.Bind(Button.CommandProperty, new Binding(nameof(workspace.DeleteSelectedTagCommand)));
                root.Children.Add(deleteTag);
                break;
            case "working tree tools":
                AddList(root, nameof(workspace.ChangeLines), nameof(workspace.SelectedChangeLine));
                root.Children.Add(
                    new TextBlock
                    {
                        Text = "Stash staged changes (unstaged and untracked files stay in place)",
                        FontWeight = FontWeight.SemiBold,
                    }
                );
                var stashMessage = new TextBox { Watermark = "Optional stash message" };
                stashMessage.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.StashMessage)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(stashMessage);
                var saveStash = new Button { Content = "Stash staged changes" };
                saveStash.Bind(Button.CommandProperty, new Binding(nameof(workspace.SaveStagedStashCommand)));
                root.Children.Add(saveStash);
                var stashList = new ListBox { MinHeight = 70 };
                stashList.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(workspace.Stashes)));
                stashList.Bind(
                    Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
                    new Binding(nameof(workspace.SelectedStash)) { Mode = BindingMode.TwoWay }
                );
                stashList.ItemTemplate = new FuncDataTemplate<GitLlama.Git.GitStashInfo>(
                    (stash, _) =>
                        new TextBlock { Text = stash is null ? "" : $"stash@{{{stash.Index}}}: {stash.Message}" }
                );
                root.Children.Add(stashList);
                root.Children.Add(
                    new TextBlock
                    {
                        Text = "Apply selected stash to a clean working tree (stash remains saved).",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var applyStash = new Button { Content = "Apply selected stash (keep entry)" };
                applyStash.Bind(Button.CommandProperty, new Binding(nameof(workspace.ApplySelectedStashCommand)));
                root.Children.Add(applyStash);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Pop applies the selected stash to a clean working tree and removes its entry only if application succeeds.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var popStash = new Button { Content = "Pop selected stash (apply and remove)" };
                popStash.Bind(Button.CommandProperty, new Binding(nameof(workspace.PopSelectedStashCommand)));
                root.Children.Add(popStash);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Permanently drop a stash without applying it. Select an entry.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var dropStash = new Button { Content = "Permanently drop selected stash" };
                dropStash.Bind(Button.CommandProperty, new Binding(nameof(workspace.DropSelectedStashCommand)));
                root.Children.Add(dropStash);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var stage = new Button { Content = "Stage selected" };
                stage.Bind(Button.CommandProperty, new Binding(nameof(workspace.StageSelectedCommand)));
                var unstage = new Button { Content = "Unstage selected" };
                unstage.Bind(Button.CommandProperty, new Binding(nameof(workspace.UnstageSelectedCommand)));
                actions.Children.Add(stage);
                actions.Children.Add(unstage);
                root.Children.Add(actions);
                var stagedSummary = new TextBlock();
                stagedSummary.Bind(TextBlock.TextProperty, new Binding(nameof(workspace.StagingSummary)));
                root.Children.Add(stagedSummary);
                root.Children.Add(new TextBlock { Text = "Staged diff", FontWeight = FontWeight.SemiBold });
                var stagedPatch = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    MinHeight = 110,
                    FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
                };
                stagedPatch.Bind(TextBox.TextProperty, new Binding(nameof(workspace.StagedDiffText)));
                root.Children.Add(stagedPatch);
                root.Children.Add(new TextBlock { Text = "Unstaged diff", FontWeight = FontWeight.SemiBold });
                var unstagedPatch = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    MinHeight = 110,
                    FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
                };
                unstagedPatch.Bind(TextBox.TextProperty, new Binding(nameof(workspace.UnstagedDiffText)));
                root.Children.Add(unstagedPatch);
                root.Children.Add(new TextBlock { Text = "Unstaged hunk overview", FontWeight = FontWeight.SemiBold });
                var hunkSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
                hunkSummary.Bind(TextBlock.TextProperty, new Binding(nameof(workspace.WorkingTreeReviewSummary)));
                root.Children.Add(hunkSummary);
                var hunks = new ListBox { MinHeight = 70 };
                hunks.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(workspace.UnstagedHunks)));
                root.Children.Add(hunks);
                var sideBySide = new CheckBox { Content = "Show side-by-side diff" };
                sideBySide.Bind(
                    Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,
                    new Binding(nameof(workspace.SideBySideDiff)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(sideBySide);
                var compare = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
                var before = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 180,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
                };
                before.Bind(TextBox.TextProperty, new Binding(nameof(workspace.DiffBeforeText)));
                var after = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 180,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
                };
                after.Bind(TextBox.TextProperty, new Binding(nameof(workspace.DiffAfterText)));
                Grid.SetColumn(after, 1);
                compare.Children.Add(before);
                compare.Children.Add(after);
                compare.Bind(Control.IsVisibleProperty, new Binding(nameof(workspace.SideBySideDiff)));
                root.Children.Add(compare);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Discard unstaged tracked changes for the selected file. Staged changes are preserved. This cannot be undone.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var discard = new Button { Content = "Discard unstaged changes in selected file" };
                discard.Bind(Button.CommandProperty, new Binding(nameof(workspace.DiscardSelectedUnstagedCommand)));
                root.Children.Add(discard);
                root.Children.Add(new TextBlock { Text = "Commit staged changes", FontWeight = FontWeight.SemiBold });
                var message = new TextBox
                {
                    Watermark = "Commit message",
                    AcceptsReturn = true,
                    MinHeight = 60,
                };
                message.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.CommitMessage)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(message);
                var name = new TextBox { Watermark = "Author name" };
                name.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.AuthorName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(name);
                var email = new TextBox { Watermark = "Author email" };
                email.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.AuthorEmail)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(email);
                var commit = new Button { Content = "Commit staged" };
                commit.Bind(Button.CommandProperty, new Binding(nameof(workspace.CommitCommand)));
                root.Children.Add(commit);
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Amend latest unpublished commit message (rewrites HEAD SHA; requires clean working tree and refuses commits in remote-tracking history)",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var amendedMessage = new TextBox
                {
                    Watermark = "Replacement message for latest commit",
                    AcceptsReturn = true,
                    MinHeight = 60,
                };
                amendedMessage.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.AmendedCommitMessage)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(amendedMessage);
                var amendCommit = new Button { Content = "Amend latest unpublished commit message" };
                amendCommit.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.AmendLatestCommitMessageCommand))
                );
                root.Children.Add(amendCommit);
                break;
            case "branch tools":
                AddList(root, nameof(workspace.BranchLines), nameof(workspace.SelectedBranchLine));
                var branchName = new TextBox { Watermark = "New local branch name" };
                branchName.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.NewBranchName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(branchName);
                var createBranch = new Button { Content = "Create branch" };
                createBranch.Bind(Button.CommandProperty, new Binding(nameof(workspace.CreateBranchCommand)));
                root.Children.Add(createBranch);
                var checkoutBranch = new Button { Content = "Switch to selected branch" };
                checkoutBranch.Bind(Button.CommandProperty, new Binding(nameof(workspace.CheckoutBranchCommand)));
                root.Children.Add(checkoutBranch);
                var deleteBranch = new Button { Content = "Delete selected local branch" };
                deleteBranch.Bind(Button.CommandProperty, new Binding(nameof(workspace.DeleteBranchCommand)));
                root.Children.Add(deleteBranch);
                var renameBranchName = new TextBox { Watermark = "New name for selected local branch" };
                renameBranchName.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.RenameBranchName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(renameBranchName);
                var renameBranch = new Button { Content = "Rename selected local branch" };
                renameBranch.Bind(Button.CommandProperty, new Binding(nameof(workspace.RenameBranchCommand)));
                root.Children.Add(renameBranch);
                root.Children.Add(
                    new TextBlock { Text = "Merge and conflict resolution", FontWeight = FontWeight.SemiBold }
                );
                var merge = new Button { Content = "Merge selected branch into current branch" };
                merge.Bind(Button.CommandProperty, new Binding(nameof(workspace.MergeSelectedBranchCommand)));
                root.Children.Add(merge);
                var inspectConflicts = new Button { Content = "Refresh unresolved conflicts" };
                inspectConflicts.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.InspectMergeConflictsCommand))
                );
                root.Children.Add(inspectConflicts);
                var conflictSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
                conflictSummary.Bind(TextBlock.TextProperty, new Binding(nameof(workspace.ConflictSummary)));
                root.Children.Add(conflictSummary);
                var conflictList = new ListBox { MinHeight = 80 };
                conflictList.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(workspace.MergeConflicts)));
                conflictList.Bind(
                    Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
                    new Binding(nameof(workspace.ConflictPath)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(conflictList);
                var conflictPath = new TextBox { Watermark = "Repository-relative resolved file path" };
                conflictPath.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.ConflictPath)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(conflictPath);
                root.Children.Add(
                    new TextBlock { Text = "Three-way conflict stages (read-only)", FontWeight = FontWeight.SemiBold }
                );
                var conflictStages = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 8 };
                var conflictBase = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 160,
                    TextWrapping = TextWrapping.NoWrap,
                };
                conflictBase.Bind(TextBox.TextProperty, new Binding(nameof(workspace.ConflictBaseText)));
                var conflictOurs = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 160,
                    TextWrapping = TextWrapping.NoWrap,
                };
                conflictOurs.Bind(TextBox.TextProperty, new Binding(nameof(workspace.ConflictOursText)));
                var conflictTheirs = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    MinHeight = 160,
                    TextWrapping = TextWrapping.NoWrap,
                };
                conflictTheirs.Bind(TextBox.TextProperty, new Binding(nameof(workspace.ConflictTheirsText)));
                Grid.SetColumn(conflictOurs, 1);
                Grid.SetColumn(conflictTheirs, 2);
                conflictStages.Children.Add(conflictBase);
                conflictStages.Children.Add(conflictOurs);
                conflictStages.Children.Add(conflictTheirs);
                root.Children.Add(new TextBlock { Text = "Base                 Ours                 Theirs" });
                root.Children.Add(conflictStages);
                var chooseSides = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var acceptOurs = new Button { Content = "Accept ours" };
                acceptOurs.Bind(Button.CommandProperty, new Binding(nameof(workspace.AcceptOursCommand)));
                var acceptTheirs = new Button { Content = "Accept theirs" };
                acceptTheirs.Bind(Button.CommandProperty, new Binding(nameof(workspace.AcceptTheirsCommand)));
                chooseSides.Children.Add(acceptOurs);
                chooseSides.Children.Add(acceptTheirs);
                root.Children.Add(chooseSides);

                var resolve = new Button { Content = "Mark edited working-tree file resolved" };
                resolve.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.ResolveConflictUsingWorkingTreeCommand))
                );
                root.Children.Add(resolve);
                var mergeMessage = new TextBox { Watermark = "Merge commit message (blank uses Git's merge message)" };
                mergeMessage.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.MergeCommitMessage)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(mergeMessage);
                var continueMerge = new Button { Content = "Complete merge after resolving conflicts" };
                continueMerge.Bind(Button.CommandProperty, new Binding(nameof(workspace.ContinueMergeCommand)));
                root.Children.Add(continueMerge);
                var abortMerge = new Button { Content = "Abort merge (discards merge working-tree changes)" };
                abortMerge.Bind(Button.CommandProperty, new Binding(nameof(workspace.AbortMergeCommand)));
                root.Children.Add(abortMerge);
                root.Children.Add(
                    new TextBlock { Text = "Linear rebase (rewrites local history)", FontWeight = FontWeight.SemiBold }
                );
                var rebaseTarget = new TextBox { Watermark = "Local target branch name" };
                rebaseTarget.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.RebaseTargetBranch)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(rebaseTarget);
                var rebaseAction = new Button { Content = "Rebase current branch onto target" };
                rebaseAction.Bind(
                    Button.CommandProperty,
                    new Binding(nameof(workspace.RebaseCurrentOntoTargetCommand))
                );
                root.Children.Add(rebaseAction);
                root.Children.Add(
                    new TextBlock { Text = "Cherry-pick / revert recovery", FontWeight = FontWeight.SemiBold }
                );
                var recoveryMessage = new TextBox { Watermark = "Commit message after resolving conflicts" };
                recoveryMessage.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.SequencerCommitMessage)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(recoveryMessage);
                var continueOperation = new Button { Content = "Continue cherry-pick / revert" };
                continueOperation.Bind(Button.CommandProperty, new Binding(nameof(workspace.ContinueSequencerCommand)));
                root.Children.Add(continueOperation);
                var abortOperation = new Button { Content = "Abort cherry-pick / revert (discards operation changes)" };
                abortOperation.Bind(Button.CommandProperty, new Binding(nameof(workspace.AbortSequencerCommand)));
                root.Children.Add(abortOperation);
                break;
            case "remote tools":
                root.Children.Add(
                    new GitLlama.Desktop.Views.RemoteTreeView
                    {
                        DataContext = workspace,
                        MinHeight = 150,
                        MaxHeight = 360,
                    }
                );
                var remoteName = new TextBox { Watermark = "Remote name (e.g. origin)" };
                remoteName.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.NewRemoteName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(remoteName);
                var remoteUrl = new TextBox { Watermark = "Remote URL" };
                remoteUrl.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.NewRemoteUrl)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(remoteUrl);
                var addRemote = new Button { Content = "Add remote" };
                addRemote.Bind(Button.CommandProperty, new Binding(nameof(workspace.AddRemoteCommand)));
                root.Children.Add(addRemote);
                var removeRemote = new Button { Content = "Remove selected remote" };
                removeRemote.Bind(Button.CommandProperty, new Binding(nameof(workspace.RemoveRemoteCommand)));
                root.Children.Add(removeRemote);
                var fetchRemote = new Button { Content = "Fetch selected remote" };
                fetchRemote.Bind(Button.CommandProperty, new Binding(nameof(workspace.FetchRemoteCommand)));
                root.Children.Add(fetchRemote);
                var pushRemote = new Button { Content = "Push current branch (no force)" };
                pushRemote.Bind(Button.CommandProperty, new Binding(nameof(workspace.PushRemoteCommand)));
                root.Children.Add(pushRemote);
                var pullRemote = new Button { Content = "Pull tracked branch (fast-forward only)" };
                pullRemote.Bind(Button.CommandProperty, new Binding(nameof(workspace.PullFastForwardCommand)));
                root.Children.Add(pullRemote);
                root.Children.Add(
                    new TextBlock
                    {
                        Text = "Double-click a branch in the remote tree to check it out as a local tracking branch.",
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
                var upstreamBranch = new TextBox { Watermark = "Remote branch to track (e.g. main)" };
                upstreamBranch.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.UpstreamBranchName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(upstreamBranch);
                var setUpstream = new Button { Content = "Track selected remote branch" };
                setUpstream.Bind(Button.CommandProperty, new Binding(nameof(workspace.SetUpstreamCommand)));
                root.Children.Add(setUpstream);
                var clearUpstream = new Button { Content = "Clear current branch upstream" };
                clearUpstream.Bind(Button.CommandProperty, new Binding(nameof(workspace.ClearUpstreamCommand)));
                root.Children.Add(clearUpstream);
                break;
            case "identity":
                var current = new TextBlock();
                current.TextWrapping = TextWrapping.Wrap;
                current.Bind(TextBlock.TextProperty, new Binding(nameof(workspace.InspectorText)));
                root.Children.Add(current);
                root.Children.Add(new TextBlock { Text = "Effective Git identity", FontWeight = FontWeight.SemiBold });
                var identity = new TextBlock { TextWrapping = TextWrapping.Wrap };
                identity.Bind(TextBlock.TextProperty, new Binding(nameof(workspace.ConfiguredIdentity)));
                root.Children.Add(identity);
                root.Children.Add(
                    new TextBlock { Text = "Repository-local identity (does not modify global Git settings)" }
                );
                var localName = new TextBox { Watermark = "Git author name" };
                localName.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.LocalIdentityName)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(localName);
                var localEmail = new TextBox { Watermark = "Git author email" };
                localEmail.Bind(
                    TextBox.TextProperty,
                    new Binding(nameof(workspace.LocalIdentityEmail)) { Mode = BindingMode.TwoWay }
                );
                root.Children.Add(localEmail);
                var saveLocalIdentity = new Button { Content = "Save local Git identity" };
                saveLocalIdentity.Bind(Button.CommandProperty, new Binding(nameof(workspace.SaveLocalIdentityCommand)));
                root.Children.Add(saveLocalIdentity);
                break;
            case "output":
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
                status.Bind(TextBlock.TextProperty, new Binding(nameof(workspace.Status)));
                root.Children.Add(status);
                break;
            default:
                root.Children.Add(new TextBlock { Text = "No content available." });
                break;
        }

        foreach (var button in root.GetLogicalDescendants().OfType<Button>())
        {
            button.Classes.Add("workspace");
        }

        return new ScrollViewer { Content = root };
    }

    private Control CreateOutput()
    {
        var log = new ListBox
        {
            Margin = new Thickness(4),
            FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
            FontSize = 11,
        };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(workspace.OperationLog)));
        return log;
    }

    private static void AddList(StackPanel root, string property, string selectionProperty)
    {
        var list = new ListBox { MinHeight = 150 };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(property));
        list.Bind(
            Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
            new Binding(selectionProperty) { Mode = BindingMode.TwoWay }
        );
        root.Children.Add(list);
    }
}
