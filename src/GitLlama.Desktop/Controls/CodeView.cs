using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitLlama.Desktop.Controls;

public sealed class CodeView : UserControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<CodeView, string>(
        nameof(Text),
        ""
    );
    public static readonly StyledProperty<string> FilePathProperty = AvaloniaProperty.Register<CodeView, string>(
        nameof(FilePath),
        ""
    );
    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    public string FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }
    private readonly AvaloniaEdit.TextEditor _editor;
    private SourceLine[] _highlighted = [];
    public static readonly StyledProperty<IReadOnlyDictionary<int, string>?> LineKindsProperty =
        AvaloniaProperty.Register<CodeView, IReadOnlyDictionary<int, string>?>(nameof(LineKinds));
    public IReadOnlyDictionary<int, string>? LineKinds
    {
        get => GetValue(LineKindsProperty);
        set => SetValue(LineKindsProperty, value);
    }
    internal AvaloniaEdit.TextEditor Editor => _editor;

    public CodeView()
    {
        _editor = ReadOnlySourceEditor.Create();
        _editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackground(number =>
            LineKinds?.TryGetValue(number, out var kind) == true ? kind : null));
        _editor.TextArea.TextView.LineTransformers.Add(new SourceLineColorizer(number =>
            number <= _highlighted.Length ? _highlighted[number - 1] : null));
        Content = _editor;
    }

    internal static void CompactRows(ListBox list)
    {
        var style = new Style(selector => selector.OfType<ListBoxItem>());
        style.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(ListBoxItem.MinHeightProperty, 22d));
        style.Setters.Add(new Setter(ListBoxItem.HeightProperty, 22d));
        style.Setters.Add(
            new Setter(ListBoxItem.HorizontalContentAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Stretch)
        );
        list.Styles.Add(style);
    }

    internal static TextBlock Highlight(string code, string filePath = "") =>
        Highlight(SourceHighlighter.Highlight(code, filePath).First());

    internal static TextBlock Highlight(SourceLine line)
    {
        var text = new TextBlock
        {
            FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
            FontSize = 13,
            Foreground = Brush.Parse("#DBDEE1"),
        };
        var offset = 0;
        foreach (var span in line.Spans)
        {
            if (span.Start > offset)
            {
                text.Inlines!.Add(new Run(line.Text[offset..span.Start]));
            }

            text.Inlines!.Add(
                new Run(line.Text.Substring(span.Start, span.Length)) { Foreground = Brush.Parse(span.Color) }
            );
            offset = span.Start + span.Length;
        }

        if (offset < line.Text.Length)
        {
            text.Inlines!.Add(new Run(line.Text[offset..]));
        }

        return text;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_editor is not null && change.Property == LineKindsProperty)
            _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        if (_editor is not null && (change.Property == TextProperty || change.Property == FilePathProperty))
        {
            _highlighted = SourceHighlighter.Highlight(Text ?? "", FilePath);
            if (_editor.Text != (Text ?? "")) _editor.Text = Text ?? "";
            _editor.TextArea.TextView.Redraw();
        }
    }
}
