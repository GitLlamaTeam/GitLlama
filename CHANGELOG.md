# Changelog

## Unreleased

- Use platform shortcuts in read-only editor and multi-selection tests so macOS uses Command instead of Control. Show the matching Copy and Select all shortcuts in the editor menu.
- Switch Discard all changes to Discard selected changes when multiple working files are selected. Confirm the affected paths, restore only those files' staged and unstaged content, and preserve unselected changes, ignored files and nested repositories.
- Add Shift-click and Ctrl-click file selection in the working-change and commit inspectors. With multiple files selected, stage or unstage only those files; with zero or one selected, keep Stage all and Unstage all. Show selected files together as read-only diffs or source previews. Preserve unchanged selections and previews during refresh, and clear them when changing repositories or commits.
- Show file history as a selectable revision list beside a read-only source preview. Compare a revision with the current working file or another revision side by side, with syntax highlighting, added/removed line colors, resizable panes and explicit missing-file states. Keep comparisons read-only and ignore results from an older file or repository selection.
- Make file and diff previews read-only text editors with normal selection across lines, keyboard shortcuts, and Copy / Select all menus. Preserve syntax highlighting, diff colors and line numbers, and selective line/hunk staging.
- Replace typed confirmation phrases with warning dialogs for resets, rebases, deletions, discarding file edits, LFS force unlocks, and pull request merges. Name the target and consequences, default destructive dialogs to Cancel, and reject confirmation if the repository or target changes.

## 0.3.2 - 2026-10-20

- Place LFS badges after the file status label consistently in staged and unstaged change lists.
- Show ahead and behind counts as badges at the top right of Push and Pull without changing the buttons' size.
- Give the submodule picker the same bordered appearance as the branch and push / pull pickers.
- Consolidate profile and repository author settings under Commit identity, with the effective author and precedence shown. Make Integrations the account sign-in entry point, move helper and temporary-token controls into advanced Git connections, and keep clearing temporary credentials separate from disconnecting integration accounts.
- Compact the commit inspector's file rows and empty change groups. Show colored Added, Modified, Deleted, and Renamed labels, including the correct status for each side of a partially staged file.
- Keep a commit selected by the first click when returning to the app. A refresh started on activation no longer restores an older selection over that click.
- Keep unchanged history rows and their controls during background refreshes so hovering a commit does not flicker or jump to another row. Update changed graph connections and references without clearing the list.
- Enable amending staged changes without retyping a commit message. Load the latest message when selecting Amend, keep any message already entered, and reuse the current message when left blank.
- Fix a crash when right-clicking a pull request by opening its context menu on the control that owns it. Keep right-click in the current workspace until an action is selected.
- Open the pull request workspace with a combined list loaded automatically from all current GitHub remotes. Remove the individual repository bar, identify each PR's source, and preserve the selected PR's details when the Git remote selection changes.
- Load every page of pull requests from the open repository's configured GitHub remotes, deduplicating aliases and hiding repositories without matching PRs. Keep PR comments, reviews, and merges scoped to their source repository, and clear previous results when switching repositories.
- Add a searchable pull request tree grouped by author, assignments, requested reviews, and all loaded PRs. Right-click a PR to merge its head into the current local branch through the repository's existing remote, without adding contributor remotes or local branches. Preserve other branches, check the selected PR commit before merging, and use the existing conflict resolver.
- Merge a remote-tracking branch directly into the checked-out local branch without creating or checking out a local copy. Preserve other local branches and use the existing conflict resolver when needed.
- Switch between sibling submodules from the toolbar's submodule dropdown, with the current submodule marked. Keep the separate close button to return to the parent repository in the same tab.

## 0.3.1 - 2026-10-16

- Keep repository creation errors in the dialog when a background refresh updates the status bar.
- Keep clone, fetch, pull, push, and tag transfers in libgit2 while using .NET's HTTPS transport to avoid misleading native TLS decryption failures. Stream large uploads, release connections before browser sign-in, preserve cancellation, and keep credentials scoped to their approved host or organization.
- Use the in-app key unlock and server fingerprint confirmation for direct SSH transfers. Prompt for unfamiliar hosts in SSH agent mode too, and share explicitly approved public host keys with the SSH helper.
- Restore Microsoft Entra OAuth sign-in for Azure DevOps alongside existing personal access tokens. Sign in with a device code, discover organizations, and allow or revoke each organization's use inside GitLlama. Keep application and directory configuration internal. Renew Microsoft authorization automatically, save the connection securely per profile, and use granted organizations in the repository manager and HTTPS Git operations. Keep PAT connections available for organizations that cannot use directory sign-in.
- Collapse the Azure DevOps personal access token form by default and mark it deprecated in GitLlama, with Microsoft sign-in preferred for new connections. Existing token connections remain supported.

## 0.3.0 - 2026-10-16

- Initializing a repository lets you name its branch and, by default, makes a first commit with a README so the branch has history straight away; other files in the folder stay uncommitted. A repository with no commits yet still shows its current branch in the sidebar.
- Speed up branch switching and large working-change lists by batching file updates, retaining unchanged rows, and reusing repository status instead of scanning twice.
- Show an animated llama while starting the app and switching branches. Keep the loading screen up until the workspace and repository monitoring are ready, and avoid an extra refresh immediately afterwards.
- Make sidebar categories vertically resizable, with a single divider grip, and keep their expand controls clear of the scrollbar.
- Push tags reachable from the current branch along with its commits, including when the branch is already up to date. Refuse to overwrite existing remote tags.
- Keep conflict selections, their insertion order, and manual output edits when leaving the resolver, switching files, or refreshing. Discard saved drafts when the underlying conflict changes.
- Refuse to open missing or invalid repositories, show a clear error, and keep the current workspace. Skip missing repositories when restoring saved tabs.
- Show persistent, dismissible error notifications for failed actions and missing required input. Group repeated errors and redact credentials from their messages.
- Select the Inspector dock tab when opening working changes and the Repository dock tab when returning to History, including when both tools share a dock group.
- Accept PuTTY SSH-2 public-key files as well as OpenSSH public keys. Copy PuTTY public keys in OpenSSH format for hosting providers; keep private keys out of the public-key display.
- Add a red Discard all changes action to the commit inspector when changes exist and there are no conflicts or unfinished Git operations. Ask for confirmation before discarding staged edits, unstaged edits, and untracked files; preserve ignored files and nested repositories.

## 0.2.2 - 2026-10-14

- Show where the current branch pushes and pulls in a push / pull picker next to the branch, with how far ahead and behind it is. Pick any branch on a remote, start a new branch on a remote, or stop tracking. Push, Pull and Fetch follow it (Push and Fetch use origin, or the only remote, until one is chosen), and their tooltips say exactly where they go. Pushing to a remote branch with a different name now works.
- Remove the + beside each remote in the sidebar for creating a branch on the server. The + on Remotes still adds a remote. Remove the + on Stashes too; stash from the Stash tab under your working changes or a file's right-click menu.
- Fix Discard changes leaving a file listed as modified: discarded files and files resolved with all of one side are written with your line-ending settings (core.autocrlf, .gitattributes) and Git LFS, the way checkout writes them. Discarding a file that only looked modified because of its line endings now clears it. When the file you were viewing has no changes left after discarding, stashing or ignoring it, the centre goes back to the history graph.
- Right-click a changed file to stage or unstage it, discard its changes, ignore it (the file, its extension or its folder), stash just that file, see its history or blame, open it or show it in its folder, copy its path, save a patch of its changes, or delete it. Discard and delete ask first.
- Merging a branch from the sidebar asks with a Merge or Cancel popup instead of making you type MERGE. Abort merge and applying a stash over your changes use the same popup.

## 0.2.1 - 2026-10-14

- Resolve merge conflicts in a dedicated resolver. While a merge is in progress the working changes inspector lists Conflicted files and Resolved files, with Mark all resolved for files fixed by hand (files that still contain conflict markers stay conflicted). Clicking a conflicted file shows A (ours) and B (theirs) side by side with syntax highlighting and the Output below: tick a conflict's lines from A, B or both, or hover a line and use + to keep just that line, with lines going into the output in the order you add them, step between conflicts, edit the output by hand, or use all of one side. Saving marks the file resolved and opens the next one.
- Show when a merge stops on conflicts: the working changes button reads "Merge conflicts (N) · Resolve", and the working changes inspector shows the merge with Resolve conflicts, Finish merge and Abort merge (after confirming). Resolve conflicts opens the Conflicts tab with the first conflicted file selected. Finishing a merge uses your profile or Git identity and Git's prepared merge message when none is entered. Fix a branch list drawn over the Merge, Conflicts and Rebase tabs.

## 0.2.0 - 2026-10-14

- List submodules in the sidebar with their status; double-click one (or right-click → Open) to work inside it in the same tab. The toolbar shows the path from the parent repository, with × to return. Uninitialized submodules are initialized first.
- Add submodules with the + on the Submodules category, and remove them from the right-click menu. Removal is staged for you to commit and keeps the submodule's cloned history in .git/modules. Uses system Git.
- Support Git LFS: files tracked by LFS are staged as pointers and checked out with their content through git-lfs, and their content is uploaded before each push. Track and stop tracking file types from the Git LFS sidebar category, download files that were checked out as pointers, and set up command-line Git for LFS. Pushing a repository that uses LFS requires Git LFS to be installed.
- Support Git LFS file locking: lock and unlock files from the working changes list, see every lock and who holds it in the Git LFS sidebar category, and force-unlock with confirmation. Mark tracked file types as lockable so they stay read-only until you lock them. Pushes that change files someone else has locked are refused with the list of files.
- Mark Git LFS files with an LFS badge in the inspector's working changes and commit file lists, where pointer line counts are hidden. Add Push all LFS files, to upload content for every branch and tag, and Free up disk space, which previews and then removes old local LFS content the server already has.
- Show stashes in the sidebar as a tree grouped by the branch they were saved on, replacing the View stashes button. Right-click a stash to apply, pop, drop or copy its message, and use the + on the Stashes category to stash your staged changes. Selecting a stash shows its changes in the inspector. Stashes are saved under your profile or Git identity instead of "GitLlama". Switch the working changes inspector between Commit and Stash to stash your staged changes with a message. Only staged changes are stashed: unstaged edits and untracked files stay in the working tree, and stashing is unavailable until something is staged. Uses system Git 2.35 or newer. Apply and pop a stash while you have uncommitted changes after confirming; a stash that would overwrite your changes is refused and kept. After stashing, the status bar says how many files were stashed and which changes were left in place: unstaged and untracked files and submodules. A stash that would contain nothing is refused instead of saved empty.
- Show local branches indented with branch icons like remote branches. Create branches with the + on Local branches, add remotes with the + on Remotes, and create a branch on a remote with the + beside it. Remove the Create branch and Remote settings buttons from the sidebar.

## 0.1.0 - 2026-10-14

- Show commit author pictures in History and the inspector, and owner pictures or host icons for remotes. Authors resolve through GitHub private addresses, Gravatar, then the commit's GitHub account across each GitHub remote, with initials as the fallback. Pictures are cached for 7 days and can be turned off in General settings.
- Move the working changes button to the right of the repository toolbar; remove the Changes, Reflog and Files sections from the sidebar (still available from the View menu).
- Add Azure DevOps Services integration through Microsoft Entra/MSAL device sign-in, silent renewal, encrypted per-profile cache restore, organization repository discovery and clone actions. Scope bearer credentials to the connected organization's HTTPS URLs for Git operations; keep SSH separate. Azure pull requests/Boards and on-premises Azure DevOps Server are outside this integration's current scope.
- Fix silent push feedback: report missing selections and server-side rejection, show persistent progress/results, select newly added remotes, and add target-specific push context actions. Publish an initial branch to an empty remote and set upstream only when none is configured; preserve existing tracking and non-force behavior.
- Keep GitHub OAuth accounts signed in with automatic access/refresh token rotation, encrypted per-profile Windows storage, restoration after restart, offline retry and revoked-authorization recovery. Replace the connected sign-in button with account status/Disconnect and remove the misleading expiry warning.
- Remove the redundant layout dropdown and Open repository button from the repository toolbar; keep repository tabs and File menu access.
- Replace the GitHub settings section with Integrations; add browser/device OAuth sign-in using the GitLlama Client ID, approval-code display/copy, cancellation, private-repository scope opt-in, verified accounts and session expiry. GitHub HTTPS operations can use the integration token; other hosts and SSH remain scoped separately. GitLab, Bitbucket and Azure DevOps are marked as planned.
- Group remote-tracking branches beneath named remotes in the sidebar and Remote screen; add nested prefix folders, branch counts, vector icons and remote context actions. Preserve tree expansion and unchanged nodes during refresh.
- Add named settings profiles with a current-profile card, creation/rename/switch/removal, separate commit identities and SSH/connection preferences. Migrate existing preferences to Default and restore the active profile on restart; clear session tokens on switching.
- Open Settings from the title-bar cogwheel or File menu; load private/public SSH key paths, preview and copy the public key, and show its SHA256 fingerprint.
- Label repository-open diagnostics explicitly so repository names are not confused with app branding.
- Reserve scrollbar space in inspector file lists so file names and change totals remain visible.
- Reconcile file rows during refresh instead of clearing/rebuilding them; preserve unchanged rows, selections and existing commit inspection while reloading.
- Rename the app, solution and projects to GitLlama; publish GitLlama.exe with the supplied llama SVG as its executable/window icon and in-app branding.
- Import existing TaRGit settings and workspace once, preserving original files and existing GitLlama preferences.
- Settings becomes a centered modal with a dimmed workspace and General, Authentication, SSH, GitHub, identity and diagnostics sections.
- HTTPS integrates with the configured system Git credential helper; scoped session tokens, account verification, access checks and stale-login recovery remain available.
- SSH clone/fetch/pull/push/tag/submodule operations use system Git/OpenSSH with agent or key-file configuration and required host verification.
- Replace the small line-by-line regex highlighter with AvaloniaEdit language grammars, multiline parsing, modern C# additions and dark-theme token colors. Diff sides retain independent parsing state.
- Restrained desaturated-blue selection/focus/primary accents on the charcoal palette, stronger panel contrast, and selected-file left indicators.
- Quieter file navigation; contextual Stage/Unstage line and hunk actions enable only for applicable diff selections.
- Clicking a sidebar branch returns to the full history graph, clears hiding filters, loads older history as needed, and selects/scrolls to the branch tip. Double-click checkout remains available.
- Remove title-bar commit search; Ctrl+F focuses the existing History search.
- Use vector window controls and stronger labeled Fetch, Pull, Push and Refresh buttons.
- Remove transparent capture padding; request the Windows compositor shadow outside the client surface.
- Empty repository tabs become opened, cloned or initialized repositories in place; tab styles and selection are consistent, and closing tabs preserves recents.
- Clone and initialization use modal forms instead of inline destination-first fields.
- Thin window outline and soft shadow; maximized windows remove the shadow margin.
- Double-click local branches to check them out; remote branches open the tracking-branch checkout prompt.
- Repository manager opens in a dedicated full-workspace tab, hiding navigation and inspector panes while preserving the repository review and draft.
- Repository + moves beside the tabs and opens a repository manager with browse/open, clone and initialization.
- Initialize existing project folders without deleting files or reinitializing existing Git repositories.
- Editor-style diffs hide patch metadata, use compact contiguous rows, a separate +/− gutter and language-aware syntax colors.
- Fix context flicker by suppressing refresh-driven navigation, coalescing view changes and retaining center controls.
- Restore a Discord-inspired charcoal/gray palette with neutral buttons and selections.
- Modern slate/blue theme, larger typography, file-type badges and a prominent commit action.
- Separate staged/unstaged groups with checkboxes; pinned commit summary and description form.
- Contextual center replaces document tabs; selected file diffs/content/history/blame appear centrally.
- Repository tabs at the top, independently collapsible sidebar categories and visible pane borders.
- Line-numbered, syntax-highlighted file view and red/green diff backgrounds.
- Borderless draggable title bar with window controls and resizing; full-height history without Output.
- Contextual inspector without tabs; local/remote sidebar groups and branch/tag/commit menus.
- Floating-document docking and closed History recovery; backed-up workspace schema migration.
- Ignored files excluded from status and untracked directories expanded into individual files.
- Neutral charcoal workspace with compact menus/toolbars, graph rows, contextual commit inspector, and named layout presets.
- Repository clone/init, branch and tag management, stash workflows, reflog recovery, worktrees, and submodule updates.
- Paged multi-branch history, author/date/text filters, commit context actions, file history and blame.
- Colored diff review, selected line/hunk staging and unstaging, patch export, whole-file/bulk staging, and staged-content amend.
- Three-way conflict result editing and guarded merge/rebase/history operations.
- Host-scoped session credentials, network progress/cancellation, automatic repository refresh, command palette, settings, and redacted diagnostics.
- Provider-isolated GitHub PR/issues lists, comments, creation, reviews, merging, and issue state changes with offline handling.
- Core/provider integration tests, headless desktop workflow checks, and portable packaging workflow.
