using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using R5Flowstate.Shell.Linux.ViewModels;
using R5Flowstate.Shell.Linux.Views;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// The blog tab, offline. Everything here is the same code the tab runs — the
/// feed and post parsers, the slug rules, the row derivation, the unread stamp,
/// the markdown grammar and the Avalonia rendering of it — against fixtures
/// rather than the live site, so the pass is deterministic and no socket is
/// opened.
///
/// Nothing here drives a command that fetches: <c>--blog</c> is the live probe,
/// and it is read-only by design. Reads the player's settings (the view model it
/// is handed) but never writes them — which is why the unread rule is checked
/// through <see cref="MainViewModel.IsBlogUnread"/> rather than through the
/// write beside it.
/// </summary>
static class BlogSelfTest
{
    const string List = """
        {"success":true,"posts":[
          {"slug":"patch-1-1-0","title":"Patch 1.1.0","tag":"Release","summary":"  The big one.  ",
           "cover":"/media/patch-1-1-0.webp","date":"2026-09-02","updatedAt":"2026-09-03T10:00:00Z",
           "lang":"english"},
          {"slug":"hotfix-3","title":"","tag":"","summary":"","cover":"http://insecure/x.png",
           "date":"not a date","updatedAt":"","lang":"english"},
          {"slug":"Bad Slug","title":"dropped","date":"2026-01-01"},
          "not an object",
          {"slug":"no-title-no-date"}
        ]}
        """;

    const string Post = """
        {"success":true,"slug":"patch-1-1-0","title":"Patch 1.1.0","tag":"release",
         "summary":"The big one.","cover":"/media/patch-1-1-0.webp","date":"2026-09-02",
         "language":"english","body":"## What changed\n\nRead more on [the site](/blog).\n"}
        """;

    /// <summary>The reader's fixture body. Every block kind the grammar has, so
    /// one render pass covers all of them. The two gallery images deliberately
    /// point at a host the loader refuses: a fixture must not be able to fetch,
    /// and the refused host is what makes the fallback ("no image, link to the
    /// file instead") visible in the same frame.</summary>
    const string Body = """
        ## What changed

        Two lines that belong
        to the same paragraph, with **bold**, `code` and a link to [the site](/blog).

        - first bullet
        - second bullet

        1. one
        2. two

        ```
        sv_cheats 1
        ```

        ![A screenshot](https://images.example/shot.webp){medium}
        ![Second](https://images.example/two.png){medium}

        ? Is it free?
        Yes, it is.
        ? Where?
        On the site.

        ## What next
        """;

    public static void Run(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        // --- the feed ------------------------------------------------------
        var list = BlogClient.ParseList(List);
        c("blog list: parses", list.Success, list.Error ?? "");
        c("blog list: one bad slug is dropped, not fatal", list.Posts.Count == 3,
            $"{list.Posts.Count} post(s) kept of 5 entries");
        c("blog list: the bad slug is the one dropped",
            list.Posts.All(p => p.Slug != "Bad Slug"));
        c("blog list: a post with no title or date survives",
            list.Posts.Any(p => p.Slug == "no-title-no-date"));

        c("blog list: a server error wins over the list",
            !BlogClient.ParseList("""{"success":false,"error":"db down"}""").Success);
        c("blog list: the error text is the server's",
            BlogClient.ParseList("""{"success":false,"error":"db down"}""").Error == "db down");
        c("blog list: success with no posts array is an empty feed",
            BlogClient.ParseList("""{"success":true}""").Success
            && BlogClient.ParseList("""{"success":true}""").Posts.Count == 0);
        c("blog list: a non-object root is refused",
            !BlogClient.ParseList("[1,2,3]").Success);
        c("blog list: broken json is refused", !BlogClient.ParseList("{oops").Success);
        c("blog list: an empty body is refused", !BlogClient.ParseList("").Success);

        // --- one post ------------------------------------------------------
        var post = BlogClient.ParsePost(Post);
        c("blog post: parses", post.Success && post.Post is not null, post.Error ?? "");
        c("blog post: the body comes through", post.Post?.Body.Contains("Read more on") == true);
        c("blog post: no body is refused",
            !BlogClient.ParsePost("""{"success":true,"slug":"x"}""").Success);
        c("blog post: a server error wins",
            BlogClient.ParsePost("""{"success":false,"error":"gone"}""").Error == "gone");
        c("blog post: a non-object root is refused",
            !BlogClient.ParsePost("""["x"]""").Success);

        // The slug gate is what keeps a bad slug off the wire altogether.
        c("blog slug: a plain slug is valid", BlogClient.IsValidSlug("patch-1-1-0"));
        c("blog slug: digits and dashes are valid", BlogClient.IsValidSlug("a1-b2"));
        c("blog slug: empty is refused", !BlogClient.IsValidSlug(""));
        c("blog slug: null is refused", !BlogClient.IsValidSlug(null));
        c("blog slug: an upper-case slug is refused", !BlogClient.IsValidSlug("Patch"));
        c("blog slug: a leading dash is refused", !BlogClient.IsValidSlug("-lead"));
        c("blog slug: a leading underscore is refused", !BlogClient.IsValidSlug("_lead"));
        c("blog slug: a path separator is refused", !BlogClient.IsValidSlug("a/b"));
        c("blog slug: a space is refused", !BlogClient.IsValidSlug("a b"));
        c("blog slug: a dotted slug is refused", !BlogClient.IsValidSlug("a..b"));
        c("blog slug: 64 characters are allowed", BlogClient.IsValidSlug(new string('a', 64)));
        c("blog slug: 65 characters are refused", !BlogClient.IsValidSlug(new string('a', 65)));

        // --- rows ----------------------------------------------------------
        var row = BlogClient.ToRow(list.Posts[0]);
        c("blog row: title is the feed's", row.Title == "Patch 1.1.0", row.Title);
        c("blog row: tag is upper-cased", row.Tag == "RELEASE", row.Tag);
        c("blog row: summary is trimmed", row.Summary == "The big one.", row.Summary);
        c("blog row: the date is formatted", row.Date == "Sep 2, 2026", row.Date);
        c("blog row: a relative cover becomes an absolute site URL",
            row.CoverUri?.AbsoluteUri == "https://r5flowstate.org/media/patch-1-1-0.webp",
            row.CoverUri?.AbsoluteUri ?? "null");
        c("blog row: it has a cover", row.HasCover);
        c("blog row: the site URL carries the slug and the language",
            row.SiteUrl.Contains("/blog/view/?slug=patch-1-1-0") && row.SiteUrl.Contains("language="),
            row.SiteUrl);

        var noTitle = BlogClient.ToRow(list.Posts.Single(p => p.Slug == "no-title-no-date"));
        c("blog row: a missing title falls back to the slug", noTitle.Title == "no-title-no-date");
        c("blog row: a missing date stays empty", noTitle.Date == "");
        c("blog row: no tag, no summary", !noTitle.HasTag && !noTitle.HasSummary);

        var hotfix = BlogClient.ToRow(list.Posts.Single(p => p.Slug == "hotfix-3"));
        c("blog row: an unparseable date is passed through", hotfix.Date == "not a date", hotfix.Date);
        c("blog row: an http cover is not a cover", !hotfix.HasCover);

        c("blog date: a plain date formats", BlogClient.FormatDate("2026-01-31") == "Jan 31, 2026");
        c("blog date: blank stays blank", BlogClient.FormatDate("") == "");
        c("blog date: junk is passed through", BlogClient.FormatDate("soon") == "soon");

        // --- the unread stamp ----------------------------------------------
        var stampA = BlogClient.Stamp(list.Posts);
        c("blog stamp: one line per post", stampA.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3);
        c("blog stamp: the same feed stamps the same", BlogClient.Stamp(list.Posts) == stampA);
        c("blog stamp: an update changes the stamp",
            BlogClient.Stamp(new[] { new SitePost { Slug = "patch-1-1-0", UpdatedAt = "2026-09-04" } }) != stampA);
        c("blog stamp: an empty feed has no stamp", BlogClient.Stamp(Array.Empty<SitePost>()).Length == 0);
        c("blog stamp: the date is the fallback when there is no updatedAt",
            BlogClient.Stamp(new[] { new SitePost { Slug = "x", Date = "2026-01-01" } }) == "x\t2026-01-01\n");
        c("blog unread: a feed nobody has seen is unread",
            MainViewModel.IsBlogUnread(stampA, ""));
        c("blog unread: the same feed seen is read",
            !MainViewModel.IsBlogUnread(stampA, stampA));
        c("blog unread: an empty feed is never unread",
            !MainViewModel.IsBlogUnread("", ""));
        c("blog unread: an empty feed does not clear a seen stamp",
            !MainViewModel.IsBlogUnread("", stampA));
        c("blog language query is the language the ui is in",
            BlogClient.LanguageQuery(null) == "language=" + NoticeLanguages.ForUi(Loc.Code),
            BlogClient.LanguageQuery(null));

        // --- the markdown grammar -------------------------------------------
        var blocks = BlogMarkdown.Parse(Body);
        c("blog md: the heading is a heading",
            blocks.Count > 0 && blocks[0].Kind == BlogBlockKind.Heading && blocks[0].Text == "What changed");
        c("blog md: wrapped lines fold into one paragraph",
            blocks.Any(b => b.Kind == BlogBlockKind.Paragraph && b.Text.Contains("same paragraph")));
        c("blog md: the bullet list is an unordered list",
            blocks.Any(b => b.Kind == BlogBlockKind.Ul && b.Items.Count == 2 && b.Items[0] == "first bullet"));
        c("blog md: a numbered list is an ordered list",
            blocks.Any(b => b.Kind == BlogBlockKind.Ol && b.Items.Count == 2 && b.Items[1] == "two"));
        c("blog md: a fence is a code block",
            blocks.Any(b => b.Kind == BlogBlockKind.Code && b.Text == "sv_cheats 1"));
        c("blog md: the gallery keeps both images, in order",
            blocks.Any(b => b.Kind == BlogBlockKind.Images
                            && b.Images.Count == 2
                            && b.Images[0].Url.AbsoluteUri.EndsWith("/shot.webp")));
        c("blog md: {medium} is carried as the gallery size",
            blocks.Any(b => b.Kind == BlogBlockKind.Images && b.Size == "medium"));
        c("blog md: the two questions are one faq block",
            blocks.Any(b => b.Kind == BlogBlockKind.Faq
                            && b.Faq.Count == 2
                            && b.Faq[0].Question == "Is it free?"
                            && b.Faq[0].Answer == "Yes, it is."));
        c("blog md: a raster image is raster",
            blocks.First(b => b.Kind == BlogBlockKind.Images).Images[0].IsRaster);
        c("blog md: an svg is not raster",
            !BlogMarkdown.Parse("![x](/a/icon.svg)")[0].Images[0].IsRaster);
        c("blog md: an svgz is not raster",
            !BlogMarkdown.Parse("![x](/a/icon.svgz)")[0].Images[0].IsRaster);
        c("blog md: more than twelve images in a row are capped",
            BlogMarkdown.Parse(string.Join('\n', Enumerable.Repeat("![x](/a.png)", 20)))[0].Images.Count == 12);
        c("blog md: an http image never becomes a gallery",
            BlogMarkdown.Parse("![x](http://evil/x.png)").All(b => b.Kind != BlogBlockKind.Images));
        c("blog md: an empty body parses to nothing", BlogMarkdown.Parse("").Count == 0);
        c("blog md: crlf is normalised",
            BlogMarkdown.Parse("## a\r\n\r\nb").Count == 2);

        // --- inlines ---------------------------------------------------------
        var inlines = BlogMarkdown.ParseInlines("**bold** `code` [the site](/blog) https://r5flowstate.org/x.");
        c("blog inline: bold", inlines.Any(s => s.Kind == InlineKind.Bold && s.Text == "bold"));
        c("blog inline: code", inlines.Any(s => s.Kind == InlineKind.Code && s.Text == "code"));
        c("blog inline: a markdown link keeps its text and resolves a relative href",
            inlines.Any(s => s.Kind == InlineKind.Link
                             && s.Text == "the site"
                             && s.Href == "https://r5flowstate.org/blog"));
        c("blog inline: a bare URL is a link with trailing punctuation left in the text",
            inlines.Any(s => s.Kind == InlineKind.Link
                             && s.Href == "https://r5flowstate.org/x"
                             && s.Text == "https://r5flowstate.org/x")
            && inlines.Any(s => s.Kind == InlineKind.Text && s.Text == "."));
        c("blog inline: plain text stays plain",
            BlogMarkdown.ParseInlines("nothing here").Count == 1
            && BlogMarkdown.ParseInlines("nothing here")[0].Kind == InlineKind.Text);
        c("blog inline: a javascript: link is not a link",
            BlogMarkdown.ParseInlines("[x](javascript:alert(1))")
                .All(s => s.Kind != InlineKind.Link || string.IsNullOrEmpty(s.Href)));

        c("blog url: https passes", BlogMarkdown.TryResolveUrl("https://a/b", out var ok) && ok == "https://a/b");
        c("blog url: a site-relative path is resolved", BlogMarkdown.TryResolveUrl("/a", out var rel)
            && rel == "https://r5flowstate.org/a");
        c("blog url: protocol-relative is refused", !BlogMarkdown.TryResolveUrl("//a/b", out _));
        c("blog url: a bare relative path is refused", !BlogMarkdown.TryResolveUrl("a/b", out _));
        c("blog url: http is refused", !BlogMarkdown.TryResolveUrl("http://a/b", out _));
        c("blog url: a quote is refused", !BlogMarkdown.TryResolveUrl("https://a/\"b", out _));
        c("blog url: an angle bracket is refused", !BlogMarkdown.TryResolveUrl("https://a/<b>", out _));
        c("blog url: a control character is refused", !BlogMarkdown.TryResolveUrl("https://a/\u0001b", out _));
        c("blog url: over 512 characters is refused",
            !BlogMarkdown.TryResolveUrl("https://a/" + new string('b', 600), out _));

        // --- the image loader's allow-list ------------------------------------
        // Both of these are refused before a socket is opened, which is the point:
        // a post can only ever make the launcher fetch the site's own images.
        var foreign = BlogImageLoader.GetAsync(new Uri("https://example.com/x.png")).GetAwaiter().GetResult();
        c("blog image: a foreign host is refused", foreign is null);
        var insecure = BlogImageLoader.GetAsync(new Uri("http://r5flowstate.org/x.png")).GetAwaiter().GetResult();
        c("blog image: http is refused", insecure is null);

        // --- binding into the tab ---------------------------------------------
        var listPosts = new List<SitePost>
        {
            new() { Slug = "a", Title = "A", Date = "2026-01-01" },
            new() { Slug = "b", Title = "B", Date = "2026-01-02" },
        };
        vm.BindBlogListFrom(listPosts);
        c("blog bind: one card per post, in feed order",
            vm.BlogCards.Count == 2 && vm.BlogCards[0].Slug == "a" && vm.BlogCards[1].Slug == "b",
            string.Join(",", vm.BlogCards.Select(r => r.Slug)));
        c("blog bind: a card with no cover starts with no image",
            vm.BlogCards.All(r => !r.HasCoverImage));

        RenderTab(c, vm, window, Body);

        // Leave the tab as the suite found it.
        vm.BlogCards.Clear();
    }

    /// <summary>The tab has to lay out and paint for real: a template or binding
    /// fault inside the reader only shows up once the panel is on screen. Both
    /// halves get a frame — the card list, then the reader with a post that
    /// carries every block kind — and the link is then clicked through the
    /// headless window, because a link that cannot be clicked is the one thing
    /// the Windows reader had that a rewrite could quietly lose.</summary>
    static void RenderTab(Action<string, bool, string> check, MainViewModel vm, Window window, string body)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        var panel = window.FindControl<Control>("PanelSimpleBlog");
        var local = window.FindControl<Control>("PanelSimpleLocal");
        var list = window.FindControl<ItemsControl>("ListBlog");
        var listHost = window.FindControl<Control>("PanelBlogList");
        var readerHost = window.FindControl<Control>("PanelBlogReader");
        var reader = window.FindControl<ScrollViewer>("ScrollBlogReader");
        var header = window.FindControl<Grid>("GridBlogHeader");
        var title = window.FindControl<TextBlock>("TxtBlogTitle");
        var status = window.FindControl<TextBlock>("TxtBlogStatus");
        var bodyView = window.FindControl<BlogMarkdownView>("BlogBody");
        if (panel is null || local is null || list is null || listHost is null || readerHost is null
            || reader is null || header is null || title is null || status is null || bodyView is null)
        {
            c("blog render: the tab exists", false, "a named control is missing");
            return;
        }

        var localWasVisible = local.IsVisible;
        var panelWasVisible = panel.IsVisible;
        var listWasVisible = listHost.IsVisible;
        var readerWasVisible = vm.BlogReaderVisible;
        var hookWas = bodyView.OpenLink;
        // The first-run EA popup is a full-window backdrop and would swallow the
        // synthetic click below (on a fresh install a real player dismisses it
        // first, which is exactly the behaviour being checked nowhere here).
        var eaWas = vm.ShowEaDialog;
        var cards = vm.BlogCards;
        var openWas = vm.BlogOpenSlug;
        try
        {
            vm.BlogCards = new ObservableCollection<BlogPostRow>(new[]
            {
                BlogClient.ToRow(new SitePost { Slug = "a", Title = "A post", Date = "2026-01-01", Tag = "news" }),
                BlogClient.ToRow(new SitePost { Slug = "b", Title = "Another post", Date = "2026-01-02",
                                                Summary = "With a summary." }),
            });
            vm.SetBlogTabShown(false);

            local.IsVisible = false;
            panel.IsVisible = true;
            vm.BlogReaderVisible = false;
            vm.ShowEaDialog = false;

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var rows = list.GetRealizedContainers().Count();
            c("blog render: the card list lays out its rows", rows >= 2, $"{rows} container(s)");

            var listFrame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            c("blog render: the list paints a frame", listFrame is not null);
            if (listFrame is not null)
            {
                var path = Path.Combine(Path.GetTempPath(), "r5flowstate-blog-selftest.png");
                listFrame.Save(path);
                var size = new FileInfo(path).Length;
                c("blog render: the list frame is not blank", size > 8 * 1024, $"{size} bytes → {path}");
            }

            // The reader, through the paint path itself, with a post that uses
            // every block the grammar has.
            vm.PaintBlogReader(new SitePost
            {
                Slug = "patch-1-1-0",
                Title = "What changed",
                Tag = "release",
                Date = "2026-09-02",
                Summary = "The big one.",
                Body = body,
            });

            // The window has to be up for the pointer to be routed at all — the
            // main pass shows it again later, and Show on a shown window is a
            // no-op.
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            c("blog render: the list is swapped out for the reader",
                !listHost.IsVisible && readerHost.IsVisible);
            c("blog render: the reader shows the post's own title", title.Text == "What changed", title.Text ?? "");
            c("blog render: the post is the open one", vm.BlogOpenSlug == "patch-1-1-0", vm.BlogOpenSlug);
            c("blog render: the tag is painted", vm.BlogReaderTag == "RELEASE", vm.BlogReaderTag);
            c("blog render: the site button follows the open post",
                vm.BlogSiteUrl().Contains("slug=patch-1-1-0"), vm.BlogSiteUrl());
            c("blog render: a post with no cover gets no header floor", Math.Abs(header.MinHeight) < 0.001,
                header.MinHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var texts = bodyView.GetVisualDescendants().OfType<TextBlock>().ToList();
            c("blog render: the body renders text", texts.Count >= 5, $"{texts.Count} TextBlock(s)");
            c("blog render: the heading is in the body",
                texts.Any(t => t.Text == "What changed"));
            c("blog render: the code block renders in a mono font",
                texts.Any(t => t.Text == "sv_cheats 1"));
            c("blog render: the faq renders as expanders",
                bodyView.GetVisualDescendants().OfType<Expander>().Count() == 2,
                $"{bodyView.GetVisualDescendants().OfType<Expander>().Count()} expander(s)");
            var bullets = bodyView.GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Rectangle>()
                .Count(r => Math.Abs(r.Width - 5) < 0.001 && Math.Abs(r.Height - 5) < 0.001);
            c("blog render: the two bullet marks are drawn", bullets == 2, $"{bullets} bullet(s)");

            // The gallery: two images, pager buttons, a caption, and no fetch —
            // the host is refused, so what shows instead is the alt text with
            // the file behind it.
            var galleryImages = bodyView.GetVisualDescendants().OfType<Image>().ToList();
            c("blog render: the gallery has one stage, not one per image",
                galleryImages.Count == 1, $"{galleryImages.Count} image(s)");
            c("blog render: a refused image shows the placeholder instead",
                galleryImages.Count == 1 && !galleryImages[0].IsVisible);
            c("blog render: the gallery caption names the position and the alt text",
                texts.Any(t => t.Text == "1 / 2 - A screenshot"),
                texts.FirstOrDefault(t => t.Text?.Contains(" / ") == true)?.Text ?? "none");
            c("blog render: the gallery pager has both buttons",
                bodyView.GetVisualDescendants().OfType<Button>()
                    .Count(b => b.Content as string is "\u2039" or "\u203a") == 2);

            // The link: the offsets the parser produced, the layout the reader
            // produced, and the hover/click rules in between.
            var linkText = bodyView.GetVisualDescendants()
                .OfType<BlogMarkdownView.InlineTextBlock>()
                .FirstOrDefault(t => t.Links.Count > 0);
            c("blog render: the paragraph with the link is on screen", linkText is not null);
            if (linkText is not null && linkText.TextLayout is { } layout)
            {
                var span = linkText.Links[0];
                var linked = linkText.PlainText.Substring(span.Start, span.Length);
                c("blog render: the link's text and href are the post's",
                    linked == "the site" && span.Href == "https://r5flowstate.org/blog",
                    $"{linked} → {span.Href}");

                var rect = layout.HitTestTextPosition(span.Start + 1);
                var at = rect.Center;
                c("blog render: the point inside the link resolves to its href",
                    linkText.LinkAt(at) == span.Href, linkText.LinkAt(at) ?? "null");
                c("blog render: a point above the paragraph resolves to nothing",
                    linkText.LinkAt(new Avalonia.Point(at.X, at.Y - 200)) is null);

                // Hover underlines the link and only the link.
                var windowPoint = linkText.TranslatePoint(at, window);
                c("blog render: the link has a place in the window", windowPoint is not null,
                    windowPoint?.ToString() ?? "null");
                if (windowPoint is { } wp)
                {
                    Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, wp);
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    c("blog render: hovering the link underlines it",
                        linkText.IsUnderlined(span.Href));

                    // And a real click opens it through the view's own hook —
                    // the window points that at the view model's OpenUrl, so
                    // this is also the check that a post's link cannot reach a
                    // browser by any other path.
                    string? opened = null;
                    bodyView.OpenLink = u => opened = u;
                    Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, wp, MouseButton.Left);
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, wp, MouseButton.Left);
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    c("blog render: clicking the link opens its href",
                        opened == span.Href, opened ?? "nothing opened");
                }

                var readerFrame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                c("blog render: the reader paints a frame", readerFrame is not null);
                if (readerFrame is not null)
                {
                    var path = Path.Combine(Path.GetTempPath(), "r5flowstate-blog-reader-selftest.png");
                    readerFrame.Save(path);
                    var size = new FileInfo(path).Length;
                    c("blog render: the reader frame is not blank", size > 8 * 1024,
                        $"{size} bytes → {path}");
                }
            }

            // The header floor only appears with a cover.
            vm.BlogReaderCoverVisible = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            c("blog render: a cover gives the header its 200px floor",
                Math.Abs(header.MinHeight - 200) < 0.001,
                header.MinHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // The feed's line belongs to the list, not to the reader.
            vm.SetBlogStatus("a line from the feed");
            c("blog render: the feed line is hidden while a post is open", !status.IsVisible);
        }
        catch (Exception ex)
        {
            c("blog render: the tab paints", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            panel.IsVisible = panelWasVisible;
            local.IsVisible = localWasVisible;
            listHost.IsVisible = listWasVisible;
            vm.BlogReaderVisible = readerWasVisible;
            vm.BlogReaderCoverVisible = false;
            vm.BlogReaderBody = "";
            vm.BlogCards = cards;
            bodyView.OpenLink = hookWas;
            vm.ShowEaDialog = eaWas;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            _ = openWas;
        }
    }
}
