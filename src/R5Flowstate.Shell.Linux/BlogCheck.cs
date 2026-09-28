using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R5Flowstate.Linux.Core;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Read-only probe of the site feed behind the Blog tab: the list, one post's
/// markdown as the reader would lay it out, and the unread stamp. Nothing is
/// written anywhere — no setting changes, no profile opened, no browser started
/// — so it answers "what would the tab show right now?" without opening the
/// launcher.
///
///     R5Flowstate --blog
///     R5Flowstate --blog --slug patch-1-1-0
///     R5Flowstate --blog --limit 5
///     R5Flowstate --blog --images 3
///
/// Every route it touches is a GET on /site/posts (and, with --images, on the
/// post's own pictures); the same calls the tab makes, with the same slug gate
/// in front of the post request.
/// </summary>
static class BlogCheck
{
    public static async Task<int> RunAsync(string? slug, string? limit, string? images = null)
    {
        Loc.Initialize(null);

        var url = MainViewModel.MasterServerUrl;
        var take = 50;
        if (int.TryParse(limit, out var parsed))
            take = Math.Clamp(parsed, 1, 50);

        Console.WriteLine($"Master:   {url}");
        Console.WriteLine($"Feed:     {BlogClient.LanguageQuery(Loc.Code)} limit={take}");
        Console.WriteLine();

        var failures = 0;

        // --- the feed the card list shows --------------------------------------
        var list = await BlogClient.ListPostsAsync(url, Loc.Code, CancellationToken.None)
            .ConfigureAwait(false);
        if (!list.Success)
        {
            failures++;
            Console.WriteLine($"FAIL  feed: {list.Error}");
            Console.WriteLine();
            Console.WriteLine("Nothing was written: no setting changed, no profile opened, no browser started.");
            return 1;
        }

        Console.WriteLine($"OK    feed: {list.Posts.Count} post(s)");
        foreach (var post in list.Posts.Take(take))
        {
            var row = BlogClient.ToRow(post);
            Console.WriteLine($"  {Trim(row.Date, 13),-13} {Trim(row.Tag, 10),-10} " +
                              $"{Trim(row.Title, 46),-46} " +
                              $"{(row.HasCover ? row.CoverUri!.AbsoluteUri : "--")}");
            if (row.HasSummary)
                Console.WriteLine($"                {Trim(row.Summary, 96)}");
        }

        // The unread dot is read, never written — the launcher owns that stamp.
        var stamp = BlogClient.Stamp(list.Posts);
        var settings = LinuxSettings.Load();
        var unread = MainViewModel.IsBlogUnread(stamp, settings.BlogSeenStamp);
        Console.WriteLine();
        Console.WriteLine($"      the dot would be {(unread ? "shown (the feed changed)" : "hidden (this feed was read)")}; " +
                          $"stored stamp is {(settings.BlogSeenStamp.Length == 0 ? "empty" : "set")}");

        // --- one post, laid out the way the reader does -------------------------
        var wanted = slug?.Trim();
        if (string.IsNullOrEmpty(wanted))
            wanted = list.Posts.FirstOrDefault()?.Slug;

        Console.WriteLine();
        if (string.IsNullOrEmpty(wanted))
        {
            Console.WriteLine("SKIP  post: the feed is empty (nothing to read)");
        }
        else if (!BlogClient.IsValidSlug(wanted))
        {
            failures++;
            Console.WriteLine($"FAIL  post: '{wanted}' is not a slug the launcher would ask for");
        }
        else
        {
            var got = await BlogClient.GetPostAsync(url, wanted, Loc.Code, CancellationToken.None)
                .ConfigureAwait(false);
            if (!got.Success || got.Post is null)
            {
                failures++;
                Console.WriteLine($"FAIL  post ({wanted}): {got.Error}");
            }
            else
            {
                var post = got.Post;
                var row = BlogClient.ToRow(post);
                var blocks = BlogMarkdown.Parse(post.Body);
                Console.WriteLine($"OK    post: {row.Title} ({row.Slug})");
                Console.WriteLine($"      {Trim(row.Date, 20)}   tag {Trim(row.Tag, 16)}   " +
                                  $"{(row.HasCover ? row.CoverUri!.AbsoluteUri : "no cover")}");
                Console.WriteLine($"      {Loc.Get("blog_open_site")}: {row.SiteUrl}");
                Console.WriteLine();

                // What the reader will paint, block by block.
                foreach (var kind in blocks.GroupBy(b => b.Kind).OrderBy(g => g.Key))
                    Console.WriteLine($"      {kind.Key,-10} x{kind.Count()}");

                var gallery = blocks.Where(b => b.Kind == BlogBlockKind.Images)
                                    .SelectMany(b => b.Images)
                                    .ToList();
                if (gallery.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"      gallery images: {gallery.Count}");
                    foreach (var img in gallery.Take(12))
                        Console.WriteLine($"        {(img.IsRaster ? "raster" : "vector"),-7} {img.Url.AbsoluteUri}");
                }

                var faq = blocks.Where(b => b.Kind == BlogBlockKind.Faq).SelectMany(b => b.Faq).ToList();
                if (faq.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"      faq: {faq.Count} question(s)");
                    foreach (var q in faq.Take(6))
                        Console.WriteLine($"        ? {Trim(q.Question, 70)}");
                }

                // Links are what the reader can click, so they are worth naming:
                // every one of them is a URL the launcher would hand a browser.
                var links = blocks.SelectMany(b => BlogMarkdown.ParseInlines(b.Text))
                                  .Where(s => s.Kind == InlineKind.Link && !string.IsNullOrEmpty(s.Href))
                                  .Select(s => s.Href!)
                                  .Distinct(StringComparer.Ordinal)
                                  .ToList();
                if (links.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"      links: {links.Count} clickable");
                    foreach (var href in links.Take(12))
                        Console.WriteLine($"        {href}");
                }

                failures += await CheckImagesAsync(row, gallery, images).ConfigureAwait(false);
            }
        }

        Console.WriteLine();
        Console.WriteLine("Nothing was written: no setting changed, no profile opened, no browser started.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The pictures, pulled through the loader the tab uses.
    ///
    /// This is the one part of the tab a windowless run cannot otherwise see,
    /// and it is the part most likely to be quietly broken: the site publishes
    /// WebP, which Avalonia 11 cannot decode at all, so the loader fetches with
    /// its own UA, decodes with Skia and re-encodes to PNG. A size here is proof
    /// the whole chain works on this machine. A picture on a host the loader
    /// refuses is not a failure — that is the allow-list doing its job on a post
    /// that pointed somewhere else — so the two are reported differently.
    ///
    /// Only the post's own pictures are pulled, and only when asked.</summary>
    static async Task<int> CheckImagesAsync(
        BlogPostRow row,
        IReadOnlyList<BlogImage> gallery,
        string? images)
    {
        var take = 0;
        if (int.TryParse(images, out var parsed))
            take = Math.Clamp(parsed, 1, 12);
        if (take == 0)
            return 0;

        var targets = new List<(string What, Uri Url)>();
        if (row.HasCover)
            targets.Add(("cover", row.CoverUri!));
        foreach (var img in gallery.Where(i => i.IsRaster))
            targets.Add(("gallery", img.Url));

        Console.WriteLine();
        Console.WriteLine($"      images: pulling {Math.Min(take, targets.Count)} of " +
                          $"{targets.Count} through the loader");

        var failures = 0;
        foreach (var (what, url) in targets.Take(take))
        {
            if (!BlogImageLoader.HostOk(url))
            {
                Console.WriteLine($"        SKIP     {what,-7} not a site host, refused before any request  " +
                                  $"{url.AbsoluteUri}");
                continue;
            }

            var size = await BlogImageLoader.FetchSizeAsync(url).ConfigureAwait(false);
            if (size is { } px)
            {
                Console.WriteLine($"        OK       {what,-7} {px.Width}x{px.Height}  {url.AbsoluteUri}");
            }
            else
            {
                failures++;
                Console.WriteLine($"        FAIL     {what,-7} fetched but would not decode  {url.AbsoluteUri}");
            }
        }
        return failures;
    }

    static string Trim(string? s, int max)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0)
            return "--";
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
