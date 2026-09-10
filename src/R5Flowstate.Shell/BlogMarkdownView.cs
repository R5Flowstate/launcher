using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace R5Flowstate.Shell;

public sealed class BlogMarkdownView : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(string),
        typeof(BlogMarkdownView),
        new PropertyMetadata(null, OnSourceChanged));

    readonly StackPanel _root = new();

    public BlogMarkdownView()
    {
        Content = _root;
    }

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BlogMarkdownView view)
            view.Rebuild();
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

    static FrameworkElement BuildHeading(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 18, 0, 8),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        return tb;
    }

    static FrameworkElement BuildParagraph(string text)
    {
        var tb = InlineText(text, 13.5);
        tb.Margin = new Thickness(0, 0, 0, 10);
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return tb;
    }

    static FrameworkElement BuildList(IReadOnlyList<string> items, bool ordered)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        for (var n = 0; n < items.Count; n++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            UIElement mark;
            if (ordered)
            {
                var num = new TextBlock
                {
                    Text = (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
                    FontSize = 13,
                    Margin = new Thickness(2, 1, 10, 0),
                    Width = 22,
                };
                num.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
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
                };
                dot.SetResourceReference(Shape.FillProperty, "Accent");
                DockPanel.SetDock(dot, Dock.Left);
                mark = dot;
            }
            var line = InlineText(items[n], 13);
            line.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            row.Children.Add(mark);
            row.Children.Add(line);
            stack.Children.Add(row);
        }
        return stack;
    }

    static FrameworkElement BuildCode(string text)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12),
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceSunken");
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextCode");
        border.Child = tb;
        return border;
    }

    static FrameworkElement BuildGallery(IReadOnlyList<BlogImage> items, string size)
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
            Visibility = Visibility.Collapsed,
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var caption = new TextBlock
        {
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var index = 0;

        void Show()
        {
            var cur = items[index];
            var alt = (cur.Alt ?? string.Empty).Trim();
            img.ToolTip = string.IsNullOrEmpty(cur.Alt) ? cur.Url.AbsoluteUri : cur.Alt;
            placeholder.Text = string.IsNullOrEmpty(cur.Alt) ? cur.Url.AbsoluteUri : cur.Alt;
            if (cur.IsRaster)
                BlogImageLoader.Bind(img, cur.Url, placeholder);
            else
                BlogImageLoader.Bind(img, null, placeholder);
            caption.Text = items.Count > 1
                ? Loc.Format("blog_gallery_pos", index + 1, items.Count)
                    + (alt.Length > 0 ? " - " + alt : string.Empty)
                : alt;
            caption.Visibility = caption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        img.MouseLeftButtonUp += (_, e) =>
        {
            OpenUrl(items[index].Url.AbsoluteUri);
            e.Handled = true;
        };
        img.Cursor = Cursors.Hand;
        placeholder.Cursor = Cursors.Hand;
        placeholder.MouseLeftButtonUp += (_, e) =>
        {
            OpenUrl(items[index].Url.AbsoluteUri);
            e.Handled = true;
        };

        if (items.Count > 1)
        {
            var prev = NavButton("\uE76B");
            var next = NavButton("\uE76C");
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

    static Button NavButton(string glyph)
    {
        var btn = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 28,
            Height = 28,
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        return btn;
    }

    static FrameworkElement BuildFaq(IReadOnlyList<BlogFaqItem> items)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var it in items)
        {
            var exp = new Expander
            {
                Header = it.Question,
                Margin = new Thickness(0, 0, 0, 6),
            };
            exp.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
            var body = InlineText(it.Answer, 13);
            body.Margin = new Thickness(4, 6, 0, 4);
            body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            exp.Content = body;
            stack.Children.Add(exp);
        }
        return stack;
    }

    static TextBlock InlineText(string text, double size)
    {
        var tb = new TextBlock
        {
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
        };
        foreach (var span in BlogMarkdown.ParseInlines(text))
        {
            switch (span.Kind)
            {
                case InlineKind.Bold:
                    tb.Inlines.Add(new Run(span.Text) { FontWeight = FontWeights.SemiBold });
                    break;
                case InlineKind.Code:
                    tb.Inlines.Add(new Run(span.Text)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = size - 0.5,
                    });
                    break;
                case InlineKind.Link when !string.IsNullOrEmpty(span.Href):
                {
                    var link = new Hyperlink(new Run(span.Text))
                    {
                        NavigateUri = TryUri(span.Href),
                    };
                    var href = span.Href;
                    link.Click += (_, e) =>
                    {
                        OpenUrl(href);
                        e.Handled = true;
                    };
                    tb.Inlines.Add(link);
                    break;
                }
                default:
                    tb.Inlines.Add(new Run(span.Text));
                    break;
            }
        }
        return tb;
    }

    static Uri? TryUri(string? href)
    {
        if (string.IsNullOrEmpty(href))
            return null;
        return Uri.TryCreate(href, UriKind.Absolute, out var u) ? u : null;
    }

    static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
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
