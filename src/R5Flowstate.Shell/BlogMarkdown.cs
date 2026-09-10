using System.Text.RegularExpressions;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public enum BlogBlockKind
{
    Heading,
    Paragraph,
    Ul,
    Ol,
    Code,
    Images,
    Faq,
}

public sealed class BlogImage
{
    public required string Alt { get; init; }
    public required Uri Url { get; init; }
    public bool IsRaster { get; init; }
}

public sealed class BlogFaqItem
{
    public required string Question { get; init; }
    public required string Answer { get; init; }
}

public sealed class BlogBlock
{
    public required BlogBlockKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    public IReadOnlyList<BlogImage> Images { get; init; } = Array.Empty<BlogImage>();
    public IReadOnlyList<BlogFaqItem> Faq { get; init; } = Array.Empty<BlogFaqItem>();
}

/// <summary>Same markdown subset the public site renders. No HTML.</summary>
public static class BlogMarkdown
{
    static readonly Regex s_img = new(
        @"^!\[([^\]]*)\]\((https://[^)\s""<>]+|/[^)\s""<>]+)\)\s*(?:\{(small|medium)\})?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex s_inline = new(
        @"\[([^\]]+)\]\(([^)\s]+)\)|\*\*([^*]+)\*\*|`([^`]+)`",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<BlogBlock> Parse(string source)
    {
        var lines = (source ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var outBlocks = new List<BlogBlock>();
        var para = new List<string>();
        var i = 0;

        void FlushPara()
        {
            var text = string.Join(' ', para).Trim();
            para.Clear();
            if (text.Length > 0)
                outBlocks.Add(new BlogBlock { Kind = BlogBlockKind.Paragraph, Text = text });
        }

        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushPara();
                i++;
                var body = new List<string>();
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                    body.Add(lines[i++]);
                if (i < lines.Length)
                    i++;
                outBlocks.Add(new BlogBlock
                {
                    Kind = BlogBlockKind.Code,
                    Text = string.Join('\n', body),
                });
                continue;
            }

            if (s_img.IsMatch(line))
            {
                FlushPara();
                var images = new List<BlogImage>();
                var size = "";
                while (i < lines.Length && images.Count < 12)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                    {
                        i++;
                        continue;
                    }
                    var m = s_img.Match(lines[i]);
                    if (!m.Success)
                        break;
                    var url = ResolveMediaUrl(m.Groups[2].Value);
                    if (url is not null)
                    {
                        var href = url.AbsoluteUri;
                        images.Add(new BlogImage
                        {
                            Alt = m.Groups[1].Value,
                            Url = url,
                            IsRaster = !href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                                && !href.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase),
                        });
                    }
                    if (m.Groups[3].Success)
                        size = m.Groups[3].Value;
                    i++;
                }
                if (images.Count > 0)
                {
                    outBlocks.Add(new BlogBlock
                    {
                        Kind = BlogBlockKind.Images,
                        Images = images,
                        Size = size,
                    });
                }
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushPara();
                outBlocks.Add(new BlogBlock
                {
                    Kind = BlogBlockKind.Heading,
                    Text = line[3..].Trim(),
                });
                i++;
                continue;
            }

            if (line.StartsWith("? ", StringComparison.Ordinal))
            {
                FlushPara();
                var faq = new List<BlogFaqItem>();
                while (i < lines.Length && lines[i].StartsWith("? ", StringComparison.Ordinal))
                {
                    var q = lines[i][2..].Trim();
                    i++;
                    var ans = new List<string>();
                    while (i < lines.Length
                           && !lines[i].StartsWith("? ", StringComparison.Ordinal)
                           && !lines[i].StartsWith("## ", StringComparison.Ordinal)
                           && !lines[i].StartsWith("```", StringComparison.Ordinal))
                    {
                        ans.Add(lines[i]);
                        i++;
                    }
                    faq.Add(new BlogFaqItem
                    {
                        Question = q,
                        Answer = string.Join(' ', ans).Trim(),
                    });
                }
                if (faq.Count > 0)
                    outBlocks.Add(new BlogBlock { Kind = BlogBlockKind.Faq, Faq = faq });
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushPara();
                var items = new List<string>();
                while (i < lines.Length)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                    {
                        i++;
                        continue;
                    }
                    if (!lines[i].StartsWith("- ", StringComparison.Ordinal)
                        && !lines[i].StartsWith("* ", StringComparison.Ordinal))
                        break;
                    items.Add(lines[i][2..].Trim());
                    i++;
                }
                outBlocks.Add(new BlogBlock { Kind = BlogBlockKind.Ul, Items = items });
                continue;
            }

            if (IsOl(line))
            {
                FlushPara();
                var items = new List<string>();
                while (i < lines.Length)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                    {
                        i++;
                        continue;
                    }
                    if (!IsOl(lines[i]))
                        break;
                    var dot = lines[i].IndexOf(". ", StringComparison.Ordinal);
                    items.Add(dot >= 0 ? lines[i][(dot + 2)..].Trim() : lines[i].Trim());
                    i++;
                }
                outBlocks.Add(new BlogBlock { Kind = BlogBlockKind.Ol, Items = items });
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushPara();
                i++;
                continue;
            }

            para.Add(line.Trim());
            i++;
        }

        FlushPara();
        return outBlocks;
    }

    public static IReadOnlyList<InlineSpan> ParseInlines(string text)
    {
        var src = text ?? string.Empty;
        var outSpans = new List<InlineSpan>();
        var last = 0;
        foreach (Match m in s_inline.Matches(src))
        {
            if (m.Index > last)
                outSpans.Add(InlineSpan.Plain(src[last..m.Index]));
            if (m.Groups[1].Success)
            {
                var href = ResolveHref(m.Groups[2].Value);
                outSpans.Add(new InlineSpan
                {
                    Kind = InlineKind.Link,
                    Text = m.Groups[1].Value,
                    Href = href,
                });
            }
            else if (m.Groups[3].Success)
            {
                outSpans.Add(new InlineSpan { Kind = InlineKind.Bold, Text = m.Groups[3].Value });
            }
            else
            {
                outSpans.Add(new InlineSpan { Kind = InlineKind.Code, Text = m.Groups[4].Value });
            }
            last = m.Index + m.Length;
        }
        if (last < src.Length)
            outSpans.Add(InlineSpan.Plain(src[last..]));
        return outSpans;
    }

    public static Uri? ResolveMediaUrl(string raw)
    {
        if (!TryResolveUrl(raw, out var url))
            return null;
        try
        {
            return new Uri(url, UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    public static string? ResolveHref(string raw)
    {
        return TryResolveUrl(raw, out var url) ? url : null;
    }

    public static bool TryResolveUrl(string raw, out string url)
    {
        url = string.Empty;
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0 || s.Length > 512)
            return false;
        foreach (var c in s)
        {
            if (c is '"' or '<' or '>' || char.IsControl(c))
                return false;
        }

        if (s.StartsWith("https://", StringComparison.Ordinal))
        {
            url = s;
            return true;
        }

        if (s.StartsWith('/') && !s.StartsWith("//", StringComparison.Ordinal))
        {
            url = ProductConstants.WebsiteUrl.TrimEnd('/') + s;
            return true;
        }

        return false;
    }

    static bool IsOl(string line)
    {
        var n = 0;
        while (n < line.Length && char.IsAsciiDigit(line[n]))
            n++;
        return n > 0 && n + 1 < line.Length && line[n] == '.' && line[n + 1] == ' ';
    }
}

public enum InlineKind
{
    Text,
    Link,
    Bold,
    Code,
}

public sealed class InlineSpan
{
    public InlineKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Href { get; init; }

    public static InlineSpan Plain(string text) =>
        new() { Kind = InlineKind.Text, Text = text };
}
