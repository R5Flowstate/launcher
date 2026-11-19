using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell.Linux;

public sealed class SitePost
{
    public string Slug { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Cover { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Lang { get; init; } = string.Empty;
}

public sealed class BlogListResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<SitePost> Posts { get; init; } = Array.Empty<SitePost>();

    public static BlogListResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class BlogPostResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public SitePost? Post { get; init; }

    public static BlogPostResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>One card in the blog list. The cover arrives later — the list
/// renders first and each card fills in when its image has been fetched — so
/// this raises PropertyChanged rather than being rebuilt. Ported from the
/// Windows Shell; only the image type differs (WPF ImageSource → Avalonia Bitmap).</summary>
public sealed class BlogPostRow : INotifyPropertyChanged
{
    Bitmap? _coverImage;

    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Tag { get; init; }
    public required string Summary { get; init; }
    public required string Date { get; init; }
    public required string UpdatedAt { get; init; }
    public Uri? CoverUri { get; init; }
    public bool HasCover => CoverUri is not null;
    public bool HasTag => Tag.Length > 0;
    public bool HasSummary => Summary.Length > 0;
    public string SiteUrl { get; init; } = string.Empty;

    public Bitmap? CoverImage
    {
        get => _coverImage;
        set
        {
            if (ReferenceEquals(_coverImage, value))
                return;
            _coverImage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCoverImage)));
        }
    }

    public bool HasCoverImage => _coverImage is not null;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Public GET /site/posts from the play host. Same feed the website uses.
/// Ported 1:1 from the Windows Shell — same routes, same query string, same
/// parse rules. Transport is the ported MasterServerClient, so the https-or-
/// loopback rule, the timeout and the no-retry behaviour are shared with the
/// servers, notes and stats lanes instead of being re-decided here.</summary>
public static class BlogClient
{
    public static async Task<BlogListResult> ListPostsAsync(
        string? baseUrl,
        string? language = null,
        CancellationToken cancel = default)
    {
        var got = await MasterServerClient.GetPublicAsync(
                baseUrl,
                "/site/posts?limit=50&" + LanguageQuery(language),
                cancel)
            .ConfigureAwait(false);
        if (!got.Ok)
            return BlogListResult.Fail(got.Error ?? Loc.Get("blog_failed"));
        return ParseList(got.Body);
    }

    public static async Task<BlogPostResult> GetPostAsync(
        string? baseUrl,
        string slug,
        string? language = null,
        CancellationToken cancel = default)
    {
        if (!IsValidSlug(slug))
            return BlogPostResult.Fail(Loc.Get("blog_failed"));

        var got = await MasterServerClient.GetPublicAsync(
                baseUrl,
                "/site/posts/" + Uri.EscapeDataString(slug) + "?" + LanguageQuery(language),
                cancel)
            .ConfigureAwait(false);
        if (!got.Ok)
            return BlogPostResult.Fail(got.Error ?? Loc.Get("blog_failed"));
        return ParsePost(got.Body);
    }

    public static string LanguageQuery(string? language = null)
    {
        var lang = NoticeLanguages.ForUi(
            string.IsNullOrWhiteSpace(language) ? Loc.Code : language);
        return "language=" + Uri.EscapeDataString(lang);
    }

    public static BlogListResult ParseList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return BlogListResult.Fail(Loc.Get("blog_failed"));
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return BlogListResult.Fail(Loc.Get("blog_failed"));
            if (root.TryGetProperty("success", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                return BlogListResult.Fail(ReadError(root) ?? Loc.Get("blog_failed"));
            if (!root.TryGetProperty("posts", out var postsEl) || postsEl.ValueKind != JsonValueKind.Array)
                return new BlogListResult { Success = true, Posts = [] };

            var list = new List<SitePost>();
            foreach (var el in postsEl.EnumerateArray())
            {
                var post = ReadMeta(el, body: "");
                if (post is not null)
                    list.Add(post);
            }
            return new BlogListResult { Success = true, Posts = list };
        }
        catch (JsonException)
        {
            return BlogListResult.Fail(Loc.Get("blog_failed"));
        }
    }

    public static BlogPostResult ParsePost(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return BlogPostResult.Fail(Loc.Get("blog_failed"));
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return BlogPostResult.Fail(Loc.Get("blog_failed"));
            if (root.TryGetProperty("success", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                return BlogPostResult.Fail(ReadError(root) ?? Loc.Get("blog_failed"));
            if (!root.TryGetProperty("body", out var bodyEl) || bodyEl.ValueKind != JsonValueKind.String)
                return BlogPostResult.Fail(Loc.Get("blog_failed"));
            var body = bodyEl.GetString() ?? string.Empty;
            var post = ReadMeta(root, body);
            if (post is null)
                return BlogPostResult.Fail(Loc.Get("blog_failed"));
            return new BlogPostResult { Success = true, Post = post };
        }
        catch (JsonException)
        {
            return BlogPostResult.Fail(Loc.Get("blog_failed"));
        }
    }

    public static BlogPostRow ToRow(SitePost post)
    {
        var slug = (post.Slug ?? string.Empty).Trim();
        var cover = BlogMarkdown.ResolveMediaUrl(post.Cover ?? string.Empty);
        return new BlogPostRow
        {
            Slug = slug,
            Title = string.IsNullOrWhiteSpace(post.Title) ? slug : post.Title.Trim(),
            Tag = (post.Tag ?? string.Empty).Trim().ToUpperInvariant(),
            Summary = (post.Summary ?? string.Empty).Trim(),
            Date = FormatDate(post.Date),
            UpdatedAt = (post.UpdatedAt ?? string.Empty).Trim(),
            CoverUri = cover,
            SiteUrl = ProductConstants.WebsiteUrl
                + "/blog/view/?slug=" + Uri.EscapeDataString(slug)
                + "&language=" + Uri.EscapeDataString(NoticeLanguages.ForUi(Loc.Code)),
        };
    }

    /// <summary>Fingerprint of what the feed looks like right now, one
    /// "slug\tupdated" line per post — the same string Windows stores under
    /// BlogSeenStamp, so the unread dot means the same thing on both.</summary>
    public static string Stamp(IReadOnlyList<SitePost> posts)
    {
        var sb = new StringBuilder();
        foreach (var p in posts)
        {
            sb.Append(p.Slug ?? "");
            sb.Append('\t');
            sb.Append(string.IsNullOrWhiteSpace(p.UpdatedAt) ? (p.Date ?? "") : p.UpdatedAt);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static string FormatDate(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (DateTime.TryParseExact(
                s,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var d))
            return d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        return s;
    }

    /// <summary>Lowercase letters, digits and dashes, starting with an
    /// alphanumeric, at most 64 characters — the site's own slug shape. This is
    /// a refusal gate, not a sanitiser: a bad slug never reaches the wire, so it
    /// can never be used to walk out of /site/posts/.</summary>
    public static bool IsValidSlug(string? slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > 64)
            return false;
        if (!char.IsAsciiLetterOrDigit(slug[0]))
            return false;
        foreach (var c in slug)
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
                continue;
            return false;
        }
        return true;
    }

    static SitePost? ReadMeta(JsonElement el, string body)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        var slug = ReadString(el, "slug");
        if (!IsValidSlug(slug))
            return null;
        return new SitePost
        {
            Slug = slug,
            Title = ReadString(el, "title"),
            Tag = ReadString(el, "tag"),
            Summary = ReadString(el, "summary"),
            Cover = ReadString(el, "cover"),
            Date = ReadString(el, "date"),
            UpdatedAt = ReadString(el, "updatedAt"),
            Lang = ReadString(el, "lang"),
            Body = body,
        };
    }

    static string ReadString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return string.Empty;
        return v.GetString() ?? string.Empty;
    }

    static string? ReadError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            return err.GetString();
        return null;
    }
}
