using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace GitLlama.Desktop.Controls;

/// <summary>A selectable source document. User input never changes its contents.</summary>
internal static class ReadOnlySourceEditor
{
    internal static TextEditor Create(IReadOnlyList<Avalonia.Input.KeyGesture>? copyShortcuts = null,
        IReadOnlyList<Avalonia.Input.KeyGesture>? selectAllShortcuts = null)
    {
        var editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
            FontSize = 13,
            Background = Brush.Parse("#1E1F22"),
            Foreground = Brush.Parse("#DBDEE1"),
            LineNumbersForeground = Brush.Parse("#80848E"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 4),
        };
        var hotkeys = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration;
        copyShortcuts ??= hotkeys?.Copy ?? [];
        selectAllShortcuts ??= hotkeys?.SelectAll ?? [];
        var copy = new MenuItem { Header = "Copy", InputGesture = copyShortcuts.FirstOrDefault() };
        async Task CopySelectionAsync()
        {
            if (TopLevel.GetTopLevel(editor)?.Clipboard is { } clipboard && editor.SelectionLength > 0)
                await clipboard.SetTextAsync(editor.SelectedText);
        }
        copy.Click += async (_, _) => await CopySelectionAsync();
        // Handle these read-only actions before the editor's routed commands, and await the clipboard write.
        editor.TextArea.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, async (_, args) =>
        {
            if (copyShortcuts.Any(gesture => gesture.Matches(args)))
            {
                args.Handled = true;
                await CopySelectionAsync();
            }
            else if (selectAllShortcuts.Any(gesture => gesture.Matches(args)))
            {
                args.Handled = true;
                editor.SelectAll();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.TextArea.SelectionChanged += (_, _) => copy.IsEnabled = editor.SelectionLength > 0;
        copy.IsEnabled = false;
        var selectAll = new MenuItem { Header = "Select all", InputGesture = selectAllShortcuts.FirstOrDefault() };
        selectAll.Click += (_, _) => editor.SelectAll();
        editor.ContextMenu = new ContextMenu { ItemsSource = new[] { copy, selectAll } };
        return editor;
    }
}

internal sealed class SourceLineColorizer(Func<int, SourceLine?> source) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        if (source(line.LineNumber) is not { } highlighted) return;
        foreach (var span in highlighted.Spans)
        {
            var end = Math.Min(line.EndOffset, line.Offset + span.Start + span.Length);
            if (line.Offset + span.Start < end)
                ChangeLinePart(line.Offset + span.Start, end,
                    element => element.TextRunProperties.SetForegroundBrush(Brush.Parse(span.Color)));
        }
    }
}

internal sealed class DiffLineBackground(Func<int, string?> kind) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext context)
    {
        if (!textView.VisualLinesValid) return;
        foreach (var line in textView.VisualLines)
        {
            var color = kind(line.FirstDocumentLine.LineNumber) switch
            {
                "Added" => "#263C2C",
                "Deleted" => "#422A2D",
                "Hunk" => "#292E37",
                _ => null,
            };
            if (color is not null)
                context.FillRectangle(Brush.Parse(color), new Rect(0,
                    line.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, line.Height));
        }
    }
}

internal sealed class DiffLineMargin : Control
{
    private readonly TextView _view;
    private readonly Func<int, GitLlama.Git.GitPatchLine?> _line;

    internal DiffLineMargin(TextView view, Func<int, GitLlama.Git.GitPatchLine?> line)
    {
        _view = view;
        _line = line;
        Width = 122;
        IsHitTestVisible = false;
        // TextView owns this margin, so their event lifetimes are the same.
        view.VisualLinesChanged += (_, _) => InvalidateVisual();
        view.ScrollOffsetChanged += (_, _) => InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!_view.VisualLinesValid) return;
        foreach (var visual in _view.VisualLines)
        {
            if (_line(visual.FirstDocumentLine.LineNumber) is not { } line) continue;
            var y = visual.VisualTop - _view.ScrollOffset.Y;
            Draw(line.OldNumber?.ToString() ?? "", 40, "#80848E");
            Draw(line.NewNumber?.ToString() ?? "", 86, "#80848E");
            Draw(line.Kind == "Added" ? "+" : line.Kind == "Deleted" ? "−" : "", 111,
                line.Kind == "Deleted" ? "#F28B91" : "#85DAA0");

            void Draw(string value, double right, string color)
            {
                var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Consolas, Menlo, monospace"), 13, Brush.Parse(color));
                context.DrawText(text, new Point(right - text.Width, y));
            }
        }
    }
}
