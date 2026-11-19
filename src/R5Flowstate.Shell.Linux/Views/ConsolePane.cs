using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux.Views;

/// <summary>
/// One console pane: the child's output, coloured, as it arrives.
///
/// Windows renders a pane as a single <c>RichTextBox</c> holding one
/// <c>Paragraph</c> of <c>Run</c>s and trims it by dropping inlines up to a
/// <c>LineBreak</c>. Avalonia has no <c>FlowDocument</c>, and the honest
/// alternative is not one enormous <c>TextBlock</c>: a boot prints tens of
/// thousands of lines and every arriving batch would rebuild the whole
/// <c>TextLayout</c>. So the pane is a virtualized list of lines — same text,
/// same colours, same trim (see MainViewModel.Console) — and only the visible
/// lines are ever laid out.
///
/// The one behaviour that has to be re-created by hand is the tail: WPF's
/// <c>ScrollToEnd</c> on a caret is <see cref="ScrollViewer.ScrollToEnd"/>, and
/// "follow unless the player has scrolled away" is tracked here from the
/// scroll offset, exactly as the Windows pane tracked it from its scroll
/// events.
/// </summary>
public sealed class ConsolePane : UserControl
{
    const double AtEndSlack = 8;

    /// <summary>The find highlight — Windows' own colour (<c>#7A5300</c>).
    /// Windows paints it as a <c>TextRange</c>'s background; here it is the line
    /// control's selection, because that is the one highlight Avalonia will draw
    /// on a read-only text line without the pane holding focus.</summary>
    static readonly IBrush FindPaint =
        new SolidColorBrush(Color.FromRgb(0x7A, 0x53, 0x00)).ToImmutable();

    readonly ScrollViewer _scroll;
    readonly ItemsControl _list;
    INotifyCollectionChanged? _watched;
    SelectableTextBlock? _findPainted;
    int _findPaint;
    SelectableTextBlock? _selected;

    public static readonly StyledProperty<IEnumerable?> LinesProperty =
        AvaloniaProperty.Register<ConsolePane, IEnumerable?>(nameof(Lines));

    /// <summary>True while the pane should stay pinned to the newest line. It
    /// goes false the moment the player scrolls up, and true again when they
    /// come back to the bottom — Windows' OnConsoleScrollChanged rule.</summary>
    public bool Follow { get; set; } = true;

    public ConsolePane()
    {
        _list = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<ConsoleLineVM>((line, _) => Build(line)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // The pane's press surface. Avalonia does not hit-test a control that
        // paints nothing, and a TemplatedControl's background is only painted
        // where its template paints it — the list's template does not paint it,
        // so a Background on the list alone would leave the empty part of a
        // console, which is where a press usually lands, belonging to the frame
        // around the pane. A border paints its own background, so the press
        // surface is a border around the list.
        var surface = new Border
        {
            Background = Brushes.Transparent,
            Child = _list,
        };

        _scroll = new ScrollViewer
        {
            Content = surface,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,

            // The scroll viewer's own background, painted by its template's
            // presenter; the surface above is what covers the empty space the
            // viewer leaves unpainted.
            Background = Brushes.Transparent,
        };
        _scroll.ScrollChanged += OnScrollChanged;
        Content = _scroll;
    }

    public IEnumerable? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>The scroll host, so the window can hand it to the find bar and
    /// so the suite can read the offset back.</summary>
    public ScrollViewer ScrollHost => _scroll;

    /// <summary>The player's own selection in this pane, which a find bar seeds
    /// its query with (Windows' SelectedConsoleWord). Long or multi-line
    /// selections are refused there and here.</summary>
    public string SelectedText
    {
        get
        {
            var text = _selected?.SelectedText ?? "";
            if (text.Length is 0 or > 120)
                return "";
            return text.Contains('\n') || text.Contains('\r') ? "" : text;
        }
    }

    /// <summary>
    /// Paint one match: select it and bring its line into view. Windows paints
    /// the match's own background and scrolls so it lands a third of the way
    /// down its viewport; a virtualized list asks the list to scroll to the item
    /// instead, which cannot promise a position inside the line's row but does
    /// guarantee the match is on screen.
    ///
    /// The scroll has to be deferred: an item that has never been realized has no
    /// control to select until layout has run for it.
    /// </summary>
    public void ShowFindMatch(ConsoleFindMatch? match)
    {
        ClearFindMatch();
        if (match is not { } m || m.Line < 0)
            return;

        // The paint below is deferred, and by the time it runs the player may
        // have stepped to another match or closed the bar altogether — so each
        // request takes a token, and a superseded one does nothing.
        var token = ++_findPaint;

        _list.ScrollIntoView(m.Line);
        Dispatcher.UIThread.Post(() =>
        {
            if (token != _findPaint)
                return;

            var control = LineControl(m.Line);
            var length = control is null ? 0 : TextOf(control).Length;
            if (control is null || m.Start >= length)
                return;

            control.SelectionBrush = FindPaint;
            control.SelectionStart = m.Start;
            control.SelectionEnd = Math.Min(m.Start + m.Length, length);
            _findPainted = control;
            PaintedLine = m.Line;
            control.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The line the highlight is actually on, or -1. The paint is deferred until
    /// layout has realized the line, so a caller that has just asked for a match
    /// has to pump the dispatcher before this means anything — which is exactly
    /// what the suite does, instead of trusting that the paint happened.
    /// </summary>
    public int PaintedLine { get; private set; } = -1;

    /// <summary>The highlighted text, for reading back what landed.</summary>
    public string PaintedText => _findPainted?.SelectedText ?? "";

    /// <summary>Take the highlight back off the line it is on.</summary>
    public void ClearFindMatch()
    {
        // Any deferred paint from before this moment is stale.
        _findPaint++;
        PaintedLine = -1;
        if (_findPainted is null)
            return;

        try
        {
            _findPainted.SelectionStart = 0;
            _findPainted.SelectionEnd = 0;
            _findPainted.SelectionBrush = null;
        }
        catch
        {
            // The line has been trimmed away from under the highlight.
        }

        _findPainted = null;
    }

    /// <summary>
    /// A line's text. This pane builds every line out of coloured runs rather
    /// than assigning <c>Text</c>, and a match's offsets are offsets into the
    /// run of them — so measuring with <c>Text</c> would measure nothing, and
    /// every match would look like it started past the end of its line.
    /// </summary>
    static string TextOf(SelectableTextBlock text)
    {
        if (text.Text is { Length: > 0 } plain)
            return plain;
        if (text.Inlines is not { Count: > 0 } inlines)
            return "";

        var builder = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is Run run)
                builder.Append(run.Text);
        }

        return builder.ToString();
    }

    /// <summary>The realized control for a line, or null when the list has not
    /// materialized that item.</summary>
    SelectableTextBlock? LineControl(int index)
    {
        if (_list.ContainerFromIndex(index) is not Visual container)
            return null;
        return container.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LinesProperty)
            return;

        // The list's items. Without this the pane is a frame around nothing:
        // the lines live in the view model, and this is the only thing that
        // hands them to the control that draws them.
        _list.ItemsSource = change.NewValue as IEnumerable;

        if (_watched is not null)
            _watched.CollectionChanged -= OnLinesChanged;
        _watched = change.NewValue as INotifyCollectionChanged;
        if (_watched is not null)
            _watched.CollectionChanged += OnLinesChanged;

        // A replaced source (a cleared pane) starts following again: the player
        // is looking at a new console, not at the tail of the old one.
        Follow = true;
    }

    void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !Follow)
            return;
        PinToEnd();
    }

    /// <summary>Scroll to the newest line. Deferred to the loaded pass because a
    /// virtualized list has no extent for the new lines until layout runs —
    /// the same reason the Windows pane re-scrolled after a dispatcher hop.</summary>
    public void PinToEnd()
    {
        _scroll.ScrollToEnd();
        Dispatcher.UIThread.Post(() =>
        {
            if (Follow)
                _scroll.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Follow the tail unless the player has scrolled away from it. A collapsed
    /// pane has no viewport, so that measurement is ignored; an extent change is
    /// the pane growing, not the player moving.
    /// </summary>
    void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll.Viewport.Height < AtEndSlack)
            return;
        if (Math.Abs(e.ExtentDelta.Y) > 0.5)
            return;

        var atEnd = _scroll.Extent.Height <= _scroll.Viewport.Height + AtEndSlack
            || _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - AtEndSlack;
        if (atEnd)
            Follow = true;
        else if (e.OffsetDelta.Y < 0)
            Follow = false;
    }

    /// <summary>One line: a selectable row whose spans carry the SDK's own
    /// colours. A span with no brush inherits the pane's foreground.</summary>
    Control Build(ConsoleLineVM? line)
    {
        var text = new SelectableTextBlock
        {
            FontFamily = ThemeLookup.Font(this, "Mono",
                FontFamily.Parse("Consolas, DejaVu Sans Mono, monospace")),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = ThemeLookup.Brush(this, "TextCode", "#9BB0A4"),
        };

        if (line is null)
            return text;

        // The pane's own selection: one line at a time, because a new selection
        // in another line is a new selection (two highlighted lines would be a
        // find highlight, which is not this).
        // Avalonia 11 has no SelectionChanged on a text block, so the two
        // properties are watched instead.
        text.PropertyChanged += (_, e) =>
        {
            if (e.Property != SelectableTextBlock.SelectionStartProperty
                && e.Property != SelectableTextBlock.SelectionEndProperty)
            {
                return;
            }

            if (text.SelectedText?.Length > 0)
            {
                if (_selected is not null && !ReferenceEquals(_selected, text))
                {
                    _selected.SelectionStart = 0;
                    _selected.SelectionEnd = 0;
                }
                _selected = text;
            }
            else if (ReferenceEquals(_selected, text))
            {
                _selected = null;
            }
        };

        if (line.Spans.Count == 0)
        {
            // A blank line still takes a row: the boot's shape is part of what
            // the player is reading.
            text.Inlines!.Add(new Run(" "));
            return text;
        }

        foreach (var span in line.Spans)
        {
            var run = new Run(span.Text);
            if (span.Brush is not null)
                run.Foreground = span.Brush;
            text.Inlines!.Add(run);
        }
        return text;
    }
}
