using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    List<SitePost> _blogPosts = new();
    readonly Dictionary<string, SitePost> _blogBodies = new(StringComparer.Ordinal);
    CancellationTokenSource? _blogCts;
    string _blogOpenSlug = "";

    void OnSimpleTabBlog(object sender, RoutedEventArgs e)
    {
        ApplySimpleTab(SimpleTab.Blog);
        _ = RefreshBlogAsync();
    }

    void SyncBlogTab(SimpleTab tab)
    {
        var on = tab == SimpleTab.Blog;
        if (PanelSimpleBlog is not null)
            PanelSimpleBlog.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        StyleTab(BtnTabBlog, on);
        if (on && ScrollBlogList is not null && PanelBlogList?.Visibility == Visibility.Visible)
            Dispatcher.BeginInvoke(ScrollBlogList.ScrollToTop, DispatcherPriority.Background);
        SyncBlogUnreadDot(markSeen: on);
    }

    async Task RefreshBlogAsync()
    {
        _blogCts?.Cancel();
        var cts = new CancellationTokenSource();
        _blogCts = cts;
        SetBlogStatus(Loc.Get("blog_loading"));
        BlogListResult result;
        try
        {
            result = await BlogClient.ListPostsAsync(CurrentMasterServerUrl(), cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log("Blog list failed: " + ex.Message);
            SetBlogStatus(Loc.Get("blog_failed"));
            return;
        }

        if (cts.IsCancellationRequested)
            return;

        if (!result.Success)
        {
            SetBlogStatus(result.Error ?? Loc.Get("blog_failed"));
            return;
        }

        _blogPosts = result.Posts.ToList();
        BindBlogList();
        SyncBlogUnreadDot(markSeen: _simpleTab == SimpleTab.Blog);

        if (_blogPosts.Count == 0)
            SetBlogStatus(Loc.Get("blog_empty"));
        else
            SetBlogStatus(null);

        if (_blogOpenSlug.Length > 0
            && _blogPosts.All(p => !string.Equals(p.Slug, _blogOpenSlug, StringComparison.Ordinal)))
            ShowBlogList();
    }

    void BindBlogList()
    {
        if (ListBlog is null)
            return;
        var rows = _blogPosts.Select(BlogClient.ToRow).ToList();
        ListBlog.ItemsSource = rows;
        foreach (var row in rows)
        {
            if (row.CoverUri is null)
                continue;
            var captured = row;
            _ = FillRowCoverAsync(captured);
        }
    }

    static async Task FillRowCoverAsync(BlogPostRow row)
    {
        if (row.CoverUri is null)
            return;
        try
        {
            row.CoverImage = await BlogImageLoader.GetAsync(row.CoverUri).ConfigureAwait(true);
        }
        catch
        {
        }
    }

    void SetBlogStatus(string? text)
    {
        if (TxtBlogStatus is null)
            return;
        var show = !string.IsNullOrEmpty(text)
            && (_blogOpenSlug.Length == 0);
        TxtBlogStatus.Text = text ?? string.Empty;
        TxtBlogStatus.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnBlogCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BlogPostRow row })
            return;
        e.Handled = true;
        _ = OpenBlogPostAsync(row.Slug);
    }

    void OnBlogCardKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter and not Key.Space)
            return;
        if (sender is not FrameworkElement { Tag: BlogPostRow row })
            return;
        e.Handled = true;
        _ = OpenBlogPostAsync(row.Slug);
    }

    void OnBlogBack(object sender, RoutedEventArgs e) => ShowBlogList();

    void OnBlogOpenSite(object sender, RoutedEventArgs e)
    {
        var slug = _blogOpenSlug;
        if (slug.Length == 0)
            return;
        var row = BlogClient.ToRow(new SitePost { Slug = slug });
        OpenExternal(row.SiteUrl);
    }

    async Task OpenBlogPostAsync(string slug)
    {
        if (!BlogClient.IsValidSlug(slug))
            return;

        if (!_blogBodies.TryGetValue(slug, out var post) || string.IsNullOrEmpty(post.Body))
        {
            SetBlogReaderStatus(Loc.Get("blog_loading"));
            ShowBlogReaderShell(slug);
            BlogPostResult got;
            try
            {
                got = await BlogClient.GetPostAsync(CurrentMasterServerUrl(), slug)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log("Blog post failed: " + ex.Message);
                SetBlogReaderStatus(Loc.Get("blog_failed"));
                return;
            }

            if (!got.Success || got.Post is null)
            {
                SetBlogReaderStatus(got.Error ?? Loc.Get("blog_failed"));
                return;
            }

            post = got.Post;
            _blogBodies[slug] = post;
        }

        PaintBlogReader(post);
    }

    void ShowBlogList()
    {
        _blogOpenSlug = "";
        if (PanelBlogList is not null)
            PanelBlogList.Visibility = Visibility.Visible;
        if (PanelBlogReader is not null)
            PanelBlogReader.Visibility = Visibility.Collapsed;
        SetBlogStatus(_blogPosts.Count == 0 ? Loc.Get("blog_empty") : null);
        ScrollBlogList?.ScrollToTop();
    }

    void ShowBlogReaderShell(string slug)
    {
        _blogOpenSlug = slug;
        if (PanelBlogList is not null)
            PanelBlogList.Visibility = Visibility.Collapsed;
        if (PanelBlogReader is not null)
            PanelBlogReader.Visibility = Visibility.Visible;
        if (TxtBlogStatus is not null)
            TxtBlogStatus.Visibility = Visibility.Collapsed;
        ScrollBlogReader?.ScrollToTop();
    }

    void PaintBlogReader(SitePost post)
    {
        var row = BlogClient.ToRow(post);
        ShowBlogReaderShell(row.Slug);
        SetBlogReaderStatus(null);

        if (ImgBlogCover is not null)
            BlogImageLoader.Bind(ImgBlogCover, row.CoverUri);
        if (GridBlogHeader is not null)
            GridBlogHeader.MinHeight = row.HasCover ? 200 : 0;
        if (RectBlogCoverFade is not null)
            RectBlogCoverFade.Visibility = row.HasCover ? Visibility.Visible : Visibility.Collapsed;

        if (TxtBlogTag is not null)
        {
            TxtBlogTag.Text = row.Tag;
            if (BorderBlogTag is not null)
                BorderBlogTag.Visibility = row.HasTag ? Visibility.Visible : Visibility.Collapsed;
        }
        if (TxtBlogDate is not null)
            TxtBlogDate.Text = row.Date;
        if (TxtBlogTitle is not null)
            TxtBlogTitle.Text = row.Title;
        if (TxtBlogSummary is not null)
        {
            TxtBlogSummary.Text = row.Summary;
            TxtBlogSummary.Visibility = row.HasSummary ? Visibility.Visible : Visibility.Collapsed;
        }
        if (BlogBody is not null)
            BlogBody.Source = post.Body ?? string.Empty;
        ScrollBlogReader?.ScrollToTop();
    }

    void SetBlogReaderStatus(string? text)
    {
        if (TxtBlogReaderStatus is null)
            return;
        TxtBlogReaderStatus.Text = text ?? string.Empty;
        TxtBlogReaderStatus.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    void SyncBlogUnreadDot(bool markSeen)
    {
        var stamp = BlogClient.Stamp(_blogPosts);
        if (markSeen &&
            !string.Equals(_settings.BlogSeenStamp, stamp, StringComparison.Ordinal))
        {
            _settings.BlogSeenStamp = stamp;
            try { SettingsStore.Save(_settings); }
            catch (Exception ex) { Log("Settings save failed: " + ex.Message); }
        }

        var unread = stamp.Length > 0 &&
            !string.Equals(_settings.BlogSeenStamp, stamp, StringComparison.Ordinal);
        if (DotBlogUnread is not null)
            DotBlogUnread.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
    }
}
