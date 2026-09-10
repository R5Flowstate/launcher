using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using R5Flowstate.Content.Rpak;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    private readonly ConcurrentDictionary<string, BitmapSource> _loadscreenCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _loadscreenLru = new();
    private const int LoadscreenCacheCap = 1;
    private string? _inflightLoadscreenKey;
    private CancellationTokenSource? _loadscreenCts;
    private string? _shownLoadscreenStem;
    private bool _suppressSimpleMap;
    private IReadOnlyList<string> _artPaks = Array.Empty<string>();
    private string? _artPaksRoot;
    private string? _shownArtPak;

    private void OnModeChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModeCardViewModel card })
            SelectModeCard(card, persist: true);
    }

    private void OnSimpleLaunchOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        OnLaunchOptionsChanged(sender, e);
    }

    private void OnSimpleOfflineChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;

        if (IsOfflineOn() && IsDediHostOnlineOn())
        {
            SetCheckSilently(ChkDediOnline, false);
            Log("Host online cleared: an offline launch skips the auth path Spire listing needs");
        }

        OnLaunchOptionsChanged(sender, e);
    }

    private void SyncSimpleLaunchOptions()
    {
        _suppressArgsPersist = true;
        try
        {
            if (ChkSimpleCheats is not null)
                ChkSimpleCheats.IsChecked = _settings.Cheats;
            SyncDeveloperChecks();
            if (ChkSimpleOffline is not null)
                ChkSimpleOffline.IsChecked = _settings.OfflineNoAuth;
            if (ChkSimplePassword is not null)
                ChkSimplePassword.IsChecked = IsPasswordProtectOn();
            if (ChkUseDx12 is not null)
                ChkUseDx12.IsChecked = _settings.UseDx12;
            if (ChkClientDx12 is not null)
                ChkClientDx12.IsChecked = _settings.UseDx12;
            if (ChkServersDx12 is not null)
                ChkServersDx12.IsChecked = _settings.UseDx12;
            ApplyPasswordProtectVisibility();
        }
        finally
        {
            _suppressArgsPersist = false;
        }
    }

    private void BindSimpleMapPicker(ModeCardViewModel card)
    {
        // The picker earns its place only when there is a real choice to make.
        if (BtnMapPicker is not null)
            BtnMapPicker.Visibility = !card.MapIsPinned && card.Maps.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (PanelMapPicker is not null && (card.MapIsPinned || card.Maps.Count <= 1))
            PanelMapPicker.Visibility = Visibility.Collapsed;
        else if (PanelMapPicker?.Visibility == Visibility.Visible)
            EnsureMapTiles();

        BindConsoleMapPicker();
    }

    private void RefreshHeroChrome()
    {
        if (TxtHeroTitle is not null)
            TxtHeroTitle.Text = _selectedMode?.Title ?? Loc.Get("pick_a_map");
        if (TxtHeroSubtitle is not null)
        {
            // A mode that IS its arena (Firing Range, Lobby) would otherwise read
            // its own name twice.
            var map = string.Equals(_selectedMode?.SelectedMap?.DisplayName,
                          _selectedMode?.Title, StringComparison.OrdinalIgnoreCase)
                      ? string.Empty
                      : _selectedMode?.SelectedMap?.DisplayName ?? string.Empty;

            if (_selectedMode is null)
                TxtHeroSubtitle.Text = string.Empty;
            else if (string.IsNullOrWhiteSpace(_selectedMode.Blurb))
                TxtHeroSubtitle.Text = map;
            else if (string.IsNullOrWhiteSpace(map))
                TxtHeroSubtitle.Text = _selectedMode.Blurb;
            else
                TxtHeroSubtitle.Text = map + "  ·  " + _selectedMode.Blurb;
        }

        if (TxtHeroCommand is not null)
        {
            var stem = _selectedMode?.SelectedMapStem;
            TxtHeroCommand.Text = _selectedMode is null || string.IsNullOrWhiteSpace(stem)
                ? string.Empty
                : $"+launchplaylist {_selectedMode.PlaylistId}   +map {stem}";
        }
    }

    private readonly List<MapTileViewModel> _mapTiles = new();
    private readonly ConcurrentDictionary<string, BitmapSource> _thumbCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbLru = new();
    // One playlist plus a leftover mode. 196px BGRA is ~90 KB each.
    private const int ThumbCacheCap = 24;
    private CancellationTokenSource? _thumbCts;
    private ModeCardViewModel? _tileMode;
    private string? _tileRoot;

    /// <summary>Matches the 196px map tile. Do not decode a hero just to shrink it.</summary>
    private const int ThumbWidth = 196;

    private void OnToggleMapPicker(object sender, RoutedEventArgs e)
    {
        if (PanelMapPicker is null)
            return;

        var opening = PanelMapPicker.Visibility != Visibility.Visible;
        PanelMapPicker.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        if (opening)
            EnsureMapTiles();
        else
            _thumbCts?.Cancel();
    }

    private void OnMapTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MapTileViewModel tile } || _selectedMode is null)
            return;

        _selectedMode.SelectedMap = tile.Option;
        foreach (var t in _mapTiles)
            t.IsSelected = ReferenceEquals(t, tile);

        BindConsoleMapPicker();

        if (PanelMapPicker is not null)
            PanelMapPicker.Visibility = Visibility.Collapsed;
        _thumbCts?.Cancel();

        PersistSettingsFromUi();
        RefreshHeroChrome();

        // The tile you picked is the screen you get; Reload is how you ask for
        // another, rather than the hero silently rolling its own.
        if (IsLobbyStem(tile.Stem) && tile.ArtPakPath is { Length: > 0 } pak)
        {
            RefreshLoadscreenChrome();
            ShowArtPak(pak);
        }
        else
        {
            QueueLoadscreen();
        }

        RefreshChangeMapButton();
    }

    private void EnsureMapTiles()
    {
        if (ListMapTiles is null || _selectedMode is null)
            return;

        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
        if (ReferenceEquals(_tileMode, _selectedMode)
            && string.Equals(_tileRoot, root, StringComparison.OrdinalIgnoreCase)
            && TilesMatch(_selectedMode.Maps))
        {
            SyncTileSelection();
            if (_mapTiles.Exists(t => t.Art is null))
                StartMissingThumbs();
            return;
        }

        BuildMapTiles();
    }

    private bool TilesMatch(IReadOnlyList<MapOption> maps)
    {
        if (_mapTiles.Count != maps.Count)
            return false;
        for (var i = 0; i < maps.Count; i++)
        {
            if (!string.Equals(_mapTiles[i].Stem, maps[i].Stem, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private void SyncTileSelection()
    {
        var stem = _selectedMode?.SelectedMapStem;
        foreach (var t in _mapTiles)
            t.IsSelected = string.Equals(t.Stem, stem, StringComparison.OrdinalIgnoreCase);
    }

    private void BuildMapTiles()
    {
        if (ListMapTiles is null || _selectedMode is null)
            return;

        _tileMode = _selectedMode;
        _tileRoot = TxtInstallRoot?.Text?.Trim() ?? string.Empty;

        _mapTiles.Clear();
        foreach (var m in _selectedMode.Maps)
        {
            _mapTiles.Add(new MapTileViewModel(m)
            {
                IsSelected = string.Equals(m.Stem, _selectedMode.SelectedMapStem,
                    StringComparison.OrdinalIgnoreCase),
            });
        }

        ListMapTiles.ItemsSource = null;
        ListMapTiles.ItemsSource = _mapTiles;

        StartMissingThumbs();
    }

    private void StartMissingThumbs()
    {
        if (_mapTiles.Count == 0)
            return;

        _thumbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        _ = LoadThumbnailsAsync(_mapTiles.ToList(), cts.Token);
    }

    /// <summary>
    /// Decodes are sequential on one worker: each is a multi-megabyte rpak plus a
    /// block decode, and a burst of them would starve the frame thread for art
    /// nobody is looking at yet.
    /// </summary>
    private async Task LoadThumbnailsAsync(List<MapTileViewModel> tiles, CancellationToken token)
    {
        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            return;

        try
        {
            await Task.Run(() => LoadscreenResolver.PrepareInstall(root), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (var tile in tiles)
        {
            if (token.IsCancellationRequested)
                return;

            // Already on the tile from a previous open -- leave it.
            if (tile.Art is not null)
                continue;

            // The lobby re-rolls its art, so it never reads or fills the cache.
            if (!IsLobbyStem(tile.Stem) && _thumbCache.TryGetValue(tile.Stem, out var cached))
            {
                tile.Art = cached;
                continue;
            }

            var lobby = IsLobbyStem(tile.Stem);
            BitmapSource? thumb = null;
            string? pickedPak = null;
            try
            {
                var decoded = await Task.Run(() =>
                {
                    // The lobby pak is a stub, so its tile borrows the art set the
                    // hero uses -- a different screen each time it is built.
                    if (lobby)
                    {
                        var art = LoadscreenResolver.ListArtPaks(root);
                        if (art.Count == 0)
                            return (null, (string?)null);
                        var pick = art[Random.Shared.Next(art.Count)];
                        return LoadscreenResolver.TryDecodePak(pick, out var apx, out _, ThumbWidth) && apx is not null
                            ? (ToThumbnail(apx), pick)
                            : (null, (string?)null);
                    }

                    if (!LoadscreenResolver.TryDecode(root, tile.Stem, out var px, out _, ThumbWidth) || px is null)
                        return (null, (string?)null);
                    return (ToThumbnail(px), (string?)null);
                }, token).ConfigureAwait(true);

                thumb = decoded.Item1;
                pickedPak = decoded.Item2;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                continue;
            }

            if (token.IsCancellationRequested)
                return;
            if (thumb is null)
                continue;

            if (!lobby)
                Remember(_thumbCache, _thumbLru, ThumbCacheCap, tile.Stem, thumb);
            tile.ArtPakPath = pickedPak;
            tile.Art = thumb;
        }
    }

    private static BitmapSource ToThumbnail(LoadscreenPixels px)
    {
        if (px.Width <= ThumbWidth)
            return ToBitmap(px);

        var th = Math.Max(1, (int)((long)px.Height * ThumbWidth / px.Width));
        var small = new byte[ThumbWidth * th * 4];
        for (var y = 0; y < th; y++)
        {
            var sy = y * px.Height / th;
            var srcRow = sy * px.Width;
            var dstRow = y * ThumbWidth;
            for (var x = 0; x < ThumbWidth; x++)
            {
                var src = (srcRow + x * px.Width / ThumbWidth) * 4;
                var dst = (dstRow + x) * 4;
                small[dst] = px.Bgra[src];
                small[dst + 1] = px.Bgra[src + 1];
                small[dst + 2] = px.Bgra[src + 2];
                small[dst + 3] = px.Bgra[src + 3];
            }
        }

        return ToBitmap(new LoadscreenPixels
        {
            Width = ThumbWidth,
            Height = th,
            Bgra = small,
            SourcePath = px.SourcePath,
        });
    }

    /// <summary>The lobby ships a stub loadscreen, so it borrows the art set instead.</summary>
    private static bool IsLobbyStem(string? stem) =>
        !string.IsNullOrWhiteSpace(stem) &&
        stem.Contains("lobby", StringComparison.OrdinalIgnoreCase);

    private void OnReloadLoadscreen(object sender, RoutedEventArgs e) =>
        QueueArtLoadscreen(reroll: true);

    private void RefreshLoadscreenChrome()
    {
        if (BtnReloadLoadscreen is null)
            return;
        BtnReloadLoadscreen.Visibility = IsLobbyStem(_selectedMode?.SelectedMapStem)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Show one named art pak, bypassing the random pick.</summary>
    private void ShowArtPak(string pakPath)
    {
        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            return;

        _shownLoadscreenStem = null;
        _shownArtPak = pakPath;

        if (_loadscreenCache.TryGetValue(pakPath, out var cached))
        {
            if (ImgLoadscreen is not null)
                ImgLoadscreen.Source = cached;
            return;
        }

        _inflightLoadscreenKey = "art:" + pakPath;
        _loadscreenCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadscreenCts = cts;
        _ = DecodeArtLoadscreenAsync(root, pakPath, cts.Token);
    }

    private void QueueArtLoadscreen(bool reroll)
    {
        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
        {
            ShowLoadscreen(null, null);
            return;
        }

        if (!string.Equals(_artPaksRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            _artPaks = LoadscreenResolver.ListArtPaks(root);
            _artPaksRoot = root;
        }

        if (_artPaks.Count == 0)
        {
            ShowLoadscreen(null, null);
            return;
        }

        if (!reroll &&
            (_shownArtPak is not null && ImgLoadscreen?.Source is not null ||
             _inflightLoadscreenKey is not null))
            return;

        var pick = _artPaks[Random.Shared.Next(_artPaks.Count)];
        for (var tries = 0; tries < 4 && _artPaks.Count > 1 &&
             string.Equals(pick, _shownArtPak, StringComparison.OrdinalIgnoreCase); tries++)
            pick = _artPaks[Random.Shared.Next(_artPaks.Count)];

        _shownLoadscreenStem = null;
        _shownArtPak = pick;

        if (_loadscreenCache.TryGetValue(pick, out var cached))
        {
            if (ImgLoadscreen is not null)
                ImgLoadscreen.Source = cached;
            return;
        }

        _inflightLoadscreenKey = "art:" + pick;
        _loadscreenCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadscreenCts = cts;
        _ = DecodeArtLoadscreenAsync(root, pick, cts.Token);
    }

    private async Task DecodeArtLoadscreenAsync(string root, string pakPath, CancellationToken token)
    {
        LoadscreenPixels? pixels = null;
        string? error = null;
        try
        {
            await Task.Run(() =>
            {
                LoadscreenResolver.PrepareInstall(root);
                LoadscreenResolver.TryDecodePak(pakPath, out pixels, out error);
                LoadscreenResolver.TrimDecodeHeap();
            }, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (string.Equals(_inflightLoadscreenKey, "art:" + pakPath, StringComparison.OrdinalIgnoreCase))
            _inflightLoadscreenKey = null;

        if (token.IsCancellationRequested || !string.Equals(_shownArtPak, pakPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (pixels is null)
        {
            if (!string.IsNullOrWhiteSpace(error))
                Log("Loadscreen: " + error);
            if (ImgLoadscreen is not null)
                ImgLoadscreen.Source = null;
            return;
        }

        var bmp = ToBitmap(pixels);
        Remember(_loadscreenCache, _loadscreenLru, LoadscreenCacheCap, pakPath, bmp);
        if (ImgLoadscreen is not null)
            ImgLoadscreen.Source = bmp;
    }

    private void QueueLoadscreen()
    {
        var stem = _selectedMode?.SelectedMapStem;
        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;

        RefreshLoadscreenChrome();

        if (IsLobbyStem(stem))
        {
            QueueArtLoadscreen(reroll: false);
            return;
        }

        _shownArtPak = null;

        if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(root))
        {
            ShowLoadscreen(null, null);
            return;
        }

        var key = CacheKey(root, stem);
        if (string.Equals(_inflightLoadscreenKey, key, StringComparison.OrdinalIgnoreCase))
            return;
        if (string.Equals(_shownLoadscreenStem, stem, StringComparison.OrdinalIgnoreCase) &&
            ImgLoadscreen?.Source is not null)
            return;

        if (_loadscreenCache.TryGetValue(key, out var hit))
        {
            ShowLoadscreen(stem, hit);
            return;
        }

        _inflightLoadscreenKey = key;
        _loadscreenCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadscreenCts = cts;
        var token = cts.Token;
        _ = DecodeLoadscreenAsync(root, stem, token);
    }

    private async Task DecodeLoadscreenAsync(string root, string stem, CancellationToken token)
    {
        LoadscreenPixels? pixels = null;
        string? error = null;
        try
        {
            await Task.Run(() =>
            {
                LoadscreenResolver.TryDecode(root, stem, out pixels, out error);
                LoadscreenResolver.TrimDecodeHeap();
            }, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (string.Equals(_inflightLoadscreenKey, CacheKey(root, stem), StringComparison.OrdinalIgnoreCase))
            _inflightLoadscreenKey = null;

        if (token.IsCancellationRequested)
            return;

        if (pixels is null)
        {
            if (!string.IsNullOrWhiteSpace(error))
                Log("Loadscreen: " + error);
            ShowLoadscreen(stem, null);
            return;
        }

        var bmp = ToBitmap(pixels);
        Remember(_loadscreenCache, _loadscreenLru, LoadscreenCacheCap, CacheKey(root, stem), bmp);
        ShowLoadscreen(stem, bmp);
    }

    private void ShowLoadscreen(string? stem, BitmapSource? image)
    {
        _shownLoadscreenStem = stem;
        if (ImgLoadscreen is not null)
            ImgLoadscreen.Source = image;
    }

    private static string CacheKey(string root, string stem) =>
        root.TrimEnd('\\', '/') + "|" + stem;

    private static void Remember(
        ConcurrentDictionary<string, BitmapSource> cache,
        LinkedList<string> lru,
        int cap,
        string key,
        BitmapSource bmp)
    {
        cache[key] = bmp;
        lru.Remove(key);
        lru.AddFirst(key);
        while (lru.Count > cap)
        {
            var old = lru.Last!.Value;
            lru.RemoveLast();
            cache.TryRemove(old, out _);
        }
    }

    private static BitmapSource ToBitmap(LoadscreenPixels pixels)
    {
        var bmp = new WriteableBitmap(
            pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(
            new Int32Rect(0, 0, pixels.Width, pixels.Height),
            pixels.Bgra,
            pixels.Width * 4,
            0);
        bmp.Freeze();
        return bmp;
    }
}
