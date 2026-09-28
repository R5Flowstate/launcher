using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace R5Flowstate.Shell.Linux.ViewModels;

/// <summary>
/// Blog tab — port of the Windows launcher's MainWindow.Blog.cs (the card list,
/// the reader, the unread dot) over the ported <see cref="BlogClient"/> and
/// <see cref="BlogMarkdown"/>.
///
/// The same split as the leaderboards tab: the state and the fetching live here,
/// the window keeps what a view must own — the two scroll hosts, the panel
/// switches, the cover fetch that has to paint a control, and the scroll-to-top
/// on the two events this raises. Windows keeps the list, the bodies and the
/// open slug in the code-behind; nothing about the feature changes by moving
/// them, and it makes the whole tab reachable from the offline suite.
///
/// Read-only by construction: every call is a GET on /site/posts.
/// </summary>
public partial class MainViewModel
{
    List<SitePost> _blogPosts = new();
    readonly Dictionary<string, SitePost> _blogBodies = new(StringComparer.Ordinal);
    CancellationTokenSource? _blogCts;
    string _blogOpenSlug = "";

    /// <summary>Slug of the post in the reader, empty while the list is up.</summary>
    public string BlogOpenSlug => _blogOpenSlug;

    /// <summary>How many posts the last successful refresh returned.</summary>
    public int BlogCount => _blogPosts.Count;

    /// <summary>True while the Blog tab is the visible one. Windows reads its
    /// own tab field for this, and it is what decides whether an arriving list
    /// counts as "read" — a refresh that happens while the player is looking at
    /// another tab must not clear the unread dot.</summary>
    public bool BlogTabShown { get; private set; }

    /// <summary>The cards, in feed order. Each row raises PropertyChanged when
    /// its cover lands, so the list is filled once and never rebuilt for an image.</summary>
    [ObservableProperty] private ObservableCollection<BlogPostRow> _blogCards = new();

    [ObservableProperty] private bool _blogUnread;

    // ---- reader ----
    [ObservableProperty] private bool _blogReaderVisible;
    [ObservableProperty] private string _blogReaderStatus = "";
    [ObservableProperty] private string _blogReaderTitle = "";
    [ObservableProperty] private string _blogReaderTag = "";
    [ObservableProperty] private string _blogReaderDate = "";
    [ObservableProperty] private string _blogReaderSummary = "";
    [ObservableProperty] private string _blogReaderBody = "";

    /// <summary>The cover the reader should show. Only the URL travels through
    /// the view model: the fetch paints an Image control, so the window does it
    /// (BlogImageLoader.Bind) when the reader announces the paint — the same
    /// division Windows has, where the code-behind fills the row's ImageSource.</summary>
    [ObservableProperty] private Uri? _blogReaderCoverUri;

    [ObservableProperty] private bool _blogReaderCoverVisible;

    partial void OnBlogReaderStatusChanged(string value) =>
        OnPropertyChanged(nameof(BlogReaderStatusVisible));

    /// <summary>The reader's own line: hidden when it has nothing to say, so an
    /// empty status never leaves a blank row above the post.</summary>
    public bool BlogReaderStatusVisible => !string.IsNullOrWhiteSpace(BlogReaderStatus);

    partial void OnBlogReaderVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(BlogListVisible));

    /// <summary>The list and the reader occupy the same slot; exactly one shows.</summary>
    public bool BlogListVisible => !BlogReaderVisible;

    partial void OnBlogReaderTagChanged(string value) =>
        OnPropertyChanged(nameof(BlogReaderTagVisible));

    public bool BlogReaderTagVisible => BlogReaderTag.Length > 0;

    partial void OnBlogReaderSummaryChanged(string value) =>
        OnPropertyChanged(nameof(BlogReaderSummaryVisible));

    public bool BlogReaderSummaryVisible => BlogReaderSummary.Length > 0;

    /// <summary>Windows gives the header a 200px floor only when the post has a
    /// cover, so a coverless post does not get a band of empty space above its
    /// title. Bound straight onto GridBlogHeader.</summary>
    public double BlogReaderHeaderMinHeight => BlogReaderCoverVisible ? 200 : 0;

    partial void OnBlogReaderCoverVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(BlogReaderHeaderMinHeight));

    /// <summary>The list was swapped in (tab entry, or back from the reader) —
    /// the window scrolls it to the top.</summary>
    public event Action? BlogListShown;

    /// <summary>A post was painted into the reader — the window scrolls the
    /// reader to the top and hands the cover to the image loader.</summary>
    public event Action? BlogReaderPainted;

    void InitBlog()
    {
        // Windows opens on the empty list with a blank line; the first refresh
        // fills both. InitBrowser's and InitLeaderboards' counterpart.
        BlogStatus = "";
        SyncBlogUnread();
    }

    /// <summary>Called by the window's tab switch. Marks the feed read while the
    /// tab is up — the same moment Windows' SyncBlogTab does it.</summary>
    public void SetBlogTabShown(bool on)
    {
        BlogTabShown = on;
        SyncBlogUnread();
    }

    [RelayCommand]
    private Task RefreshBlogAsync() => RefreshBlogListAsync(quiet: false);

    async Task RefreshBlogListAsync(bool quiet)
    {
        _blogCts?.Cancel();
        var cts = new CancellationTokenSource();
        _blogCts = cts;

        if (!quiet)
            SetBlogStatus(Loc.Get("blog_loading"));

        BlogListResult result;
        try
        {
            result = await BlogClient.ListPostsAsync(MasterServerUrl, Loc.Code, cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppendLog("Blog list failed: " + ex.Message);
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

        _blogBodies.Clear();
        BindBlogListFrom(result.Posts);
        SyncBlogUnread();

        SetBlogStatus(_blogPosts.Count == 0 ? Loc.Get("blog_empty") : "");

        if (_blogOpenSlug.Length > 0)
        {
            // The open post may have been pulled from the feed since it was
            // read; if it is gone the reader has nothing to show, so fall back
            // to the list instead of leaving a post that no longer exists.
            if (_blogPosts.All(p => !string.Equals(p.Slug, _blogOpenSlug, StringComparison.Ordinal)))
                ShowBlogList();
            else
                await OpenBlogPostAsync(_blogOpenSlug).ConfigureAwait(true);
        }

        if (!quiet)
            AppendLog($"Blog: {_blogPosts.Count} post(s) listed.");
    }

    /// <summary>Take a feed the caller already has: the refresh path minus the
    /// request. Internal so the offline suite can check the card order and the
    /// image rules with fixtures — it starts no request of its own.</summary>
    internal void BindBlogListFrom(IReadOnlyList<SitePost> posts)
    {
        _blogPosts = posts.ToList();
        BindBlogList();
    }

    /// <summary>Fill the list and start a cover fetch per card. The rows are
    /// handed out before any image exists — the same order Windows uses, so the
    /// list is never held back by the slowest picture.
    ///
    /// Internal so the offline suite can prove the order and the image rules
    /// with fixture posts (the fetch it starts obeys the loader's allow-list, so
    /// a fixture cover on a foreign host resolves to "no image" without a
    /// socket).</summary>
    internal void BindBlogList()
    {
        var rows = _blogPosts.Select(BlogClient.ToRow).ToList();
        BlogCards = new ObservableCollection<BlogPostRow>(rows);
        foreach (var row in rows)
        {
            if (row.CoverUri is null)
                continue;
            _ = FillRowCoverAsync(row);
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
            // A card with no cover is a normal card — the sunken panel and the
            // R5 mark stay, exactly as they do while a cover is still loading.
        }
    }

    /// <summary>Open a post in the reader. Slugs are validated before anything
    /// is fetched, and a body already in hand is never fetched twice.</summary>
    public async Task OpenBlogPostAsync(string slug)
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
                got = await BlogClient.GetPostAsync(MasterServerUrl, slug, Loc.Code)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendLog("Blog post failed: " + ex.Message);
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

    [RelayCommand]
    private void BlogBack() => ShowBlogList();

    /// <summary>The list comes back; the status line returns to whatever the
    /// feed last said (empty feed, or nothing at all).</summary>
    public void ShowBlogList()
    {
        _blogOpenSlug = "";
        BlogReaderVisible = false;
        BlogReaderCoverUri = null;
        BlogReaderCoverVisible = false;
        SetBlogStatus(_blogPosts.Count == 0 ? Loc.Get("blog_empty") : "");
        BlogListShown?.Invoke();
    }

    /// <summary>Swap to the reader before the post exists, so the click answers
    /// immediately and the loading line has somewhere to appear.</summary>
    void ShowBlogReaderShell(string slug)
    {
        _blogOpenSlug = slug;
        BlogReaderVisible = true;
    }

    /// <summary>Paint a post into the reader. Internal because it is the half of
    /// OpenBlogPostAsync that runs after the body is in hand — the offline suite
    /// drives it directly, and nothing in it fetches.</summary>
    internal void PaintBlogReader(SitePost post)
    {
        var row = BlogClient.ToRow(post);
        ShowBlogReaderShell(row.Slug);
        SetBlogReaderStatus("");

        BlogReaderCoverUri = row.CoverUri;
        BlogReaderCoverVisible = row.HasCover;
        BlogReaderTag = row.Tag;
        BlogReaderDate = row.Date;
        BlogReaderTitle = row.Title;
        BlogReaderSummary = row.Summary;
        BlogReaderBody = post.Body ?? string.Empty;
        BlogReaderPainted?.Invoke();
    }

    /// <summary>Windows hides the feed's status line whenever a post is open —
    /// the reader has its own line, and two of them at once is noise. Internal
    /// because the offline suite needs a feed line without a feed to prove the
    /// hiding rule (a status string is never persisted).</summary>
    internal void SetBlogStatus(string text)
    {
        BlogStatus = text;
        OnPropertyChanged(nameof(BlogStatusVisible));
    }

    /// <summary>True when the feed's line should be on screen: it has something
    /// to say and no post is open.</summary>
    public bool BlogStatusVisible =>
        !string.IsNullOrWhiteSpace(BlogStatus) && _blogOpenSlug.Length == 0;

    void SetBlogReaderStatus(string text)
    {
        BlogReaderStatus = text;
    }

    /// <summary>The unread dot. The stamp is the feed's own fingerprint
    /// (slug + updated-at per post), so it survives a restart — the settings
    /// file carries it — and a post that is edited or added lights the dot
    /// again while a pure re-render does not.</summary>
    public void SyncBlogUnread()
    {
        var stamp = BlogClient.Stamp(_blogPosts);
        if (BlogTabShown && IsBlogUnread(stamp, _s.BlogSeenStamp))
        {
            _s.BlogSeenStamp = stamp;
            try { _s.Save(); }
            catch (Exception ex) { AppendLog("Settings save failed: " + ex.Message); }
        }

        BlogUnread = IsBlogUnread(stamp, _s.BlogSeenStamp);
    }

    /// <summary>The dot's rule on its own: a feed with a fingerprint that is not
    /// the stored one is unread. Split out so it can be checked without the
    /// settings write beside it — the offline suite must not touch the player's
    /// settings file, and the write is the only part that does.</summary>
    internal static bool IsBlogUnread(string stamp, string seen) =>
        stamp.Length > 0 && !string.Equals(seen, stamp, StringComparison.Ordinal);

    /// <summary>The site page for the post in the reader, empty when none is
    /// open — the same "nothing to follow" Windows gets from its own open slug,
    /// so the button does nothing rather than sending the player to an index.</summary>
    public string BlogSiteUrl() =>
        _blogOpenSlug.Length == 0
            ? ""
            : BlogClient.ToRow(new SitePost { Slug = _blogOpenSlug }).SiteUrl;
}
