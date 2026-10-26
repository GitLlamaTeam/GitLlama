using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using GitLlama.Git;

namespace GitLlama.Desktop.Controls;

public sealed class PatchView : UserControl
{
    public static readonly StyledProperty<string> PatchProperty = AvaloniaProperty.Register<PatchView, string>(
        nameof(Patch),
        ""
    );
    public static readonly StyledProperty<string> FilePathProperty = AvaloniaProperty.Register<PatchView, string>(
        nameof(FilePath),
        ""
    );
    public static readonly StyledProperty<GitPatchLine?> SelectedLineProperty = AvaloniaProperty.Register<
        PatchView,
        GitPatchLine?
    >(nameof(SelectedLine), defaultBindingMode: BindingMode.TwoWay);
    private readonly AvaloniaEdit.TextEditor _editor;
    private IReadOnlyList<GitPatchLine> _displayLines = [];
    private bool _updating;
    internal AvaloniaEdit.TextEditor Editor => _editor;
    private GitPatchLine? LineAt(int number) => number > 0 && number <= _displayLines.Count ? _displayLines[number - 1] : null;
    private IReadOnlyDictionary<int, SourceLine> _highlightedLines = new Dictionary<int, SourceLine>();
    public string Patch
    {
        get => GetValue(PatchProperty);
        set => SetValue(PatchProperty, value);
    }
    public string FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }
    public GitPatchLine? SelectedLine
    {
        get => GetValue(SelectedLineProperty);
        set => SetValue(SelectedLineProperty, value);
    }

    public PatchView()
    {
        _editor = ReadOnlySourceEditor.Create();
        _editor.ShowLineNumbers = false;
        _editor.TextArea.LeftMargins.Clear();
        _editor.TextArea.LeftMargins.Add(new DiffLineMargin(_editor.TextArea.TextView, LineAt));
        _editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackground(number => LineAt(number)?.Kind));
        _editor.TextArea.TextView.LineTransformers.Add(new SourceLineColorizer(number =>
            LineAt(number) is { } line && _highlightedLines.TryGetValue(line.Index, out var source) ? source : null));
        _editor.TextArea.Caret.PositionChanged += (_, _) => SelectCurrentLine();
        _editor.TextArea.PointerReleased += (_, _) => SelectCurrentLine();
        Content = _editor;
    }

    private void SelectCurrentLine()
    {
        if (!_updating) SetCurrentValue(SelectedLineProperty, LineAt(_editor.TextArea.Caret.Line));
    }

    internal static string SourceText(GitPatchLine line) =>
        line.Kind is "Added" or "Deleted" or "Context" && line.Text.Length > 0 && line.Text[0] is '+' or '-' or ' '
            ? line.Text[1..] : line.Text;

    internal static IReadOnlyList<GitPatchLine> DisplayLines(string patch)
    {
        var lines = GitPartialStageService.Lines(patch);
        var code = lines.Where(line => line.Kind != "Metadata").ToArray();
        if (code.Length > 0)
        {
            return code;
        }

        var message = patch.Contains("diff --git", StringComparison.Ordinal)
            ? (
                patch.Contains("Binary", StringComparison.OrdinalIgnoreCase)
                    ? "Binary files differ; no text preview is available."
                    : "No text changes to display."
            )
            : patch.Trim();
        return string.IsNullOrEmpty(message) ? [] : [new GitPatchLine(0, message, "Metadata", "")];
    }

    internal static IReadOnlyDictionary<int, SourceLine> HighlightDiff(IReadOnlyList<GitPatchLine> lines, string path)
    {
        var result = new Dictionary<int, SourceLine>();
        foreach (var hunk in lines.Where(line => !string.IsNullOrEmpty(line.Hunk)).GroupBy(line => line.Hunk))
        {
            // Deleted lines belong to the old document; additions belong to the new document.
            // Keep their multiline parser states separate so one side cannot color the other.
            foreach (var side in new[] { "Deleted", "Added" })
            {
                var source = hunk.Where(line => line.Kind == "Context" || line.Kind == side).ToArray();
                if (source.Length == 0)
                {
                    continue;
                }

                var highlighted = SourceHighlighter.Highlight(
                    string.Join("\n", source.Select(line => line.Text.Length > 0 ? line.Text[1..] : "")),
                    path
                );
                for (var i = 0; i < source.Length; i++)
                {
                    result[source[i].Index] = highlighted[i];
                }
            }
        }

        return result;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_editor is null || _updating) return;
        _updating = true;
        try
        {
            if (change.Property == PatchProperty || change.Property == FilePathProperty)
            {
                _displayLines = DisplayLines(Patch ?? "");
                _highlightedLines = HighlightDiff(_displayLines, FilePath);
                var content = string.Join("\n", _displayLines.Select(SourceText));
                if (_editor.Text != content)
                {
                    _editor.Text = content;
                    SetCurrentValue(SelectedLineProperty, null);
                }
                _editor.TextArea.TextView.Redraw();
            }
            if (change.Property == SelectedLineProperty && SelectedLine is { } selected)
            {
                var index = _displayLines.ToList().FindIndex(line => line.Index == selected.Index && line.Text == selected.Text);
                if (index >= 0 && _editor.TextArea.Caret.Line != index + 1)
                {
                    _editor.TextArea.Caret.Line = index + 1;
                    _editor.ScrollToLine(index + 1);
                }
            }
        }
        finally { _updating = false; }
    }
}
