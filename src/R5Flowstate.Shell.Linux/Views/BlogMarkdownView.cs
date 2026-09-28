using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace R5Flowstate.Shell.Linux.Views;

/// <summary>
/// Renders the blog's markdown subset (BlogMarkdown) as Avalonia controls.
/// Ported from the Windows Shell's BlogMarkdownView, which built a WPF
/// StackPanel of TextBlocks — the block grammar, the spacing, the gallery
/// sizing and the FAQ expanders are the same; the inline links are not.
///
/// WPF has an inline Hyperlink; Avalonia has no such inline. So a paragraph is
/// one TextBlock whose Runs carry the spans, and the clickable ones are found
/// again at hit-test time through the paragraph's own TextLayout — which keeps
/// the real advantage of an inline: the link wraps with the sentence instead of
/// being a separate element that can start its own line. Link styling is the
/// theme's own (Button.link / the WPF Hyperlink style): AccentBright, no
/// decoration, underline only while the pointer is over the link.
/// </summary>
public sealed class BlogMarkdownView : UserControl
{
    /// <summary>Monospace for code spans and code blocks. Consolas is the
    /// Windows theme's pick; the Linux fallbacks are the families CachyOS
    /// actually ships, so a code span is never rendered in the body font.</summary>
    static readonly FontFamily Mono =
        FontFamily.Parse("Consolas, DejaVu Sans Mono, Noto Sans Mono, Liberation Mono");

    static readonly FontFamily Glyphs = FontFamily.Parse("Inter, DejaVu Sans");

    readonly StackPanel _root = new();

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<BlogMarkdownView, string?>(nameof(Source));

    public BlogMarkdownView()
    {
        Content = _root;
    }

    /// <summary>The post body. Setting it rebuilds the whole view; the reader
    /// only ever sets it when a post has been fetched, so there is no partial
    /// state to reconcile.</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Where a clicked link goes. The window points this at the view
    /// model's OpenUrl so a link inside a post behaves exactly like every other
    /// external link in the launcher — and so the self-test can prove a click
    /// resolves to a URL without launching anything.</summary>
    public Action<string>? OpenLink { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
            Rebuild();
    }

    void Rebuild()
    {
        _root.Children.Clear();
        var src = Source;
        if (string.IsNullOrWhiteSpace(src))
            return;

        foreach (var block in BlogMarkdown.Parse(src))
        {
            var el = block.Kind switch
            {
                BlogBlockKind.Heading => BuildHeading(block.Text),
                BlogBlockKind.Paragraph => BuildParagraph(block.Text),
                BlogBlockKind.Ul => BuildList(block.Items, ordered: false),
                BlogBlockKind.Ol => BuildList(block.Items, ordered: true),
                BlogBlockKind.Code => BuildCode(block.Text),
                BlogBlockKind.Images => BuildGallery(block.Images, block.Size),
                BlogBlockKind.Faq => BuildFaq(block.Faq),
                _ => null,
            };
            if (el is not null)
                _root.Children.Add(el);
        }
    }

    // ------------------------------------------------------------- theme brushes

    /// <summary>Resolve a theme brush in code. The view is normally already in
    /// the tree (so the theme's resources are visible through it); the app-level
    /// lookup covers a view built before it is attached, and the literal is the
    /// palette value from R5FTheme.axaml so a markdown view can never render
    /// invisible text. The palette lives in exactly one place for the XAML; the
    /// fallbacks are here only because Avalonia has no SetResourceReference.</summary>
    IBrush ThemeBrush(string key, string fallback)
    {
        if (ResourceNodeExtensions.TryFindResource(this, key, out var v) && v is IBrush hit)
            return hit;
        if (Application.Current is { } app
            && ResourceNodeExtensions.TryFindResource(app, key, out var av)
            && av is IBrush fromApp)
            return fromApp;
        return Avalonia.Media.Brush.Parse(fallback);
    }

    IBrush TextPrimary => ThemeBrush("TextPrimary", "#E9F1EC");
    IBrush TextSecondary => ThemeBrush("TextSecondary", "#A9B8AF");
    IBrush TextMuted => ThemeBrush("TextMuted", "#71827A");
    IBrush TextCode => ThemeBrush("TextCode", "#9BB0A4");
    IBrush Accent => ThemeBrush("Accent", "#3DDA8A");
    IBrush AccentBright => ThemeBrush("AccentBright", "#9FF0C0");
    IBrush SurfaceSunken => ThemeBrush("SurfaceSunken", "#0C1210");

    // ----------------------------------------------------------------- blocks

    Control BuildHeading(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 18, 0, 8),
            Foreground = TextPrimary,
        };
    }

    Control BuildParagraph(string text)
    {
        var tb = InlineText(text, 13.5);
        tb.Margin = new Thickness(0, 0, 0, 10);
        tb.Foreground = TextSecondary;
        return tb;
    }

    Control BuildList(IReadOnlyList<string> items, bool ordered)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        for (var n = 0; n < items.Count; n++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            Control mark;
            if (ordered)
            {
                var num = new TextBlock
                {
                    Text = (n + 1).ToString(CultureInfo.InvariantCulture) + ".",
                    FontSize = 13,
                    Margin = new Thickness(2, 1, 10, 0),
                    Width = 22,
                    Foreground = Accent,
                };
                DockPanel.SetDock(num, Dock.Left);
                mark = num;
            }
            else
            {
                var dot = new Rectangle
                {
                    Width = 5,
                    Height = 5,
                    RadiusX = 1,
                    RadiusY = 1,
                    Margin = new Thickness(1, 7, 10, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Fill = Accent,
                };
                DockPanel.SetDock(dot, Dock.Left);
                mark = dot;
            }
            var line = InlineText(items[n], 13);
            line.Foreground = TextSecondary;
            row.Children.Add(mark);
            row.Children.Add(line);
            stack.Children.Add(row);
        }
        return stack;
    }

    Control BuildCode(string text)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Background = SurfaceSunken,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = Mono,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextCode,
            },
        };
    }

    Control BuildGallery(IReadOnlyList<BlogImage> items, string size)
    {
        if (items.Count == 0)
            return new Border { Height = 0 };

        var host = new StackPanel { Margin = new Thickness(0, 4, 0, 14) };
        if (size == "small")
            host.MaxWidth = 384;
        else if (size == "medium")
            host.MaxWidth = 672;
        host.HorizontalAlignment = size.Length > 0
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Stretch;

        var stage = new DockPanel { LastChildFill = true };
        var img = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = size == "small" ? 220 : 340,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var placeholder = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
            Foreground = TextMuted,
        };

        var caption = new TextBlock
        {
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = TextMuted,
        };

        var index = 0;

        void Show()
        {
            var cur = items[index];
            var alt = (cur.Alt ?? string.Empty).Trim();
            ToolTip.SetTip(img, string.IsNullOrEmpty(cur.Alt) ? cur.Url.AbsoluteUri : cur.Alt);
            placeholder.Text = string.IsNullOrEmpty(cur.Alt) ? cur.Url.AbsoluteUri : cur.Alt;
            // An SVG has no raster form to decode, so it goes straight to the
            // placeholder — which is a link to the file, same as Windows.
            BlogImageLoader.Bind(img, cur.IsRaster ? cur.Url : null, placeholder);
            caption.Text = items.Count > 1
                ? Loc.Format("blog_gallery_pos", index + 1, items.Count)
                    + (alt.Length > 0 ? " - " + alt : string.Empty)
                : alt;
            caption.IsVisible = caption.Text.Length > 0;
        }

        img.Cursor = new Cursor(StandardCursorType.Hand);
        img.PointerPressed += (_, e) =>
        {
            Open(items[index].Url.AbsoluteUri);
            e.Handled = true;
        };
        placeholder.Cursor = new Cursor(StandardCursorType.Hand);
        placeholder.PointerPressed += (_, e) =>
        {
            Open(items[index].Url.AbsoluteUri);
            e.Handled = true;
        };

        if (items.Count > 1)
        {
            var prev = NavButton("‹");
            var next = NavButton("›");
            prev.Margin = new Thickness(0, 0, 8, 0);
            next.Margin = new Thickness(8, 0, 0, 0);
            prev.Click += (_, _) =>
            {
                index = (index + items.Count - 1) % items.Count;
                Show();
            };
            next.Click += (_, _) =>
            {
                index = (index + 1) % items.Count;
                Show();
            };
            DockPanel.SetDock(prev, Dock.Left);
            DockPanel.SetDock(next, Dock.Right);
            prev.VerticalAlignment = VerticalAlignment.Center;
            next.VerticalAlignment = VerticalAlignment.Center;
            stage.Children.Add(prev);
            stage.Children.Add(next);
        }

        stage.Children.Add(img);
        host.Children.Add(stage);
        host.Children.Add(placeholder);
        host.Children.Add(caption);
        Show();
        return host;
    }

    /// <summary>Gallery pager button. Windows used Segoe MDL2 chevron glyphs
    /// (U+E76B/U+E76C), which do not exist in Inter — the Linux theme maps those
    /// glyphs to vector paths elsewhere; here the single-character guillemets
    /// ‹ › carry the same meaning in a font the launcher actually ships.</summary>
    static Button NavButton(string glyph)
    {
        var btn = new Button
        {
            Content = glyph,
            FontFamily = Glyphs,
            FontSize = 14,
            Width = 28,
            Height = 28,
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Classes.Add("ghost");
        return btn;
    }

    Control BuildFaq(IReadOnlyList<BlogFaqItem> items)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var it in items)
        {
            var body = InlineText(it.Answer, 13);
            body.Margin = new Thickness(4, 6, 0, 4);
            body.Foreground = TextSecondary;
            var exp = new Expander
            {
                Header = new TextBlock
                {
                    Text = it.Question,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                },
                Content = body,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = TextPrimary,
            };
            stack.Children.Add(exp);
        }
        return stack;
    }

    // --------------------------------------------------------------- inlines

    /// <summary>A paragraph: the spans of one line of markdown, with the link
    /// spans remembered by character offset so a pointer can be turned back into
    /// a URL. The offsets are the concatenation of the Run texts — no separator
    /// is inserted between runs, so they line up with the TextLayout's own
    /// character indices.
    ///
    /// Internal because the self-test reaches for one of these: the click and
    /// hover rules live here, and proving them needs the paragraph rather than a
    /// screenshot.</summary>
    internal sealed class InlineTextBlock : TextBlock
    {
        internal readonly List<(int Start, int Length, string Href, Run Run)> Links = new();

        internal string? HrefAt(int index)
        {
            foreach (var (start, length, href, _) in Links)
            {
                if (index >= start && index < start + length)
                    return href;
            }
            return null;
        }

        /// <summary>The link under a point, or null. TextLayout is null until the
        /// paragraph has been measured, and a hit outside the text is not a link —
        /// both are simply "no link here".</summary>
        internal string? LinkAt(Point point)
        {
            if (TextLayout is not { } layout)
                return null;
            var hit = layout.HitTestPoint(point);
            if (!hit.IsInside)
                return null;
            return HrefAt(hit.TextPosition);
        }

        /// <summary>Underline the hovered link and nothing else — the WPF
        /// Hyperlink style the theme copied.</summary>
        internal void Hover(string? href)
        {
            foreach (var (_, _, link, run) in Links)
            {
                var on = href is not null && string.Equals(link, href, StringComparison.Ordinal);
                run.TextDecorations = on ? Avalonia.Media.TextDecorations.Underline : null;
            }
        }

        /// <summary>The paragraph's characters, in the same indexing the links
        /// use. TextBlock.Text stays null for a paragraph built from runs, so
        /// this is how anything outside the view reads the line back.</summary>
        internal string PlainText
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                if (Inlines is not { } inlines)
                    return "";
                foreach (var inline in inlines)
                {
                    if (inline is Run run)
                        sb.Append(run.Text);
                }
                return sb.ToString();
            }
        }

        internal bool IsUnderlined(string href)
        {
            foreach (var (_, _, link, run) in Links)
            {
                if (string.Equals(link, href, StringComparison.Ordinal))
                    return run.TextDecorations is not null;
            }
            return false;
        }
    }

    InlineTextBlock InlineText(string text, double size)
    {
        var tb = new InlineTextBlock
        {
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
        };
        var offset = 0;
        foreach (var span in BlogMarkdown.ParseInlines(text))
        {
            if (span.Text.Length == 0)
                continue;
            switch (span.Kind)
            {
                case InlineKind.Bold:
                    tb.Inlines!.Add(new Run(span.Text) { FontWeight = FontWeight.SemiBold });
                    break;
                case InlineKind.Code:
                    tb.Inlines!.Add(new Run(span.Text)
                    {
                        FontFamily = Mono,
                        FontSize = size - 0.5,
                    });
                    break;
                case InlineKind.Link when !string.IsNullOrEmpty(span.Href):
                {
                    var run = new Run(span.Text) { Foreground = AccentBright };
                    tb.Inlines!.Add(run);
                    tb.Links.Add((offset, span.Text.Length, span.Href!, run));
                    break;
                }
                default:
                    tb.Inlines!.Add(new Run(span.Text));
                    break;
            }
            offset += span.Text.Length;
        }

        if (tb.Links.Count > 0)
        {
            tb.Cursor = new Cursor(StandardCursorType.Hand);
            tb.PointerMoved += (_, e) => tb.Hover(tb.LinkAt(e.GetPosition(tb)));
            tb.PointerExited += (_, _) => tb.Hover(null);
            tb.PointerPressed += (_, e) =>
            {
                var href = tb.LinkAt(e.GetPosition(tb));
                if (href is null)
                    return;
                Open(href);
                e.Handled = true;
            };
        }
        return tb;
    }

    void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (OpenLink is { } hook)
        {
            hook(url);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }
}
