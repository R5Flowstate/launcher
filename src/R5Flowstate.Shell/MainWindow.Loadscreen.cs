using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using R5Flowstate.Content.Rpak;
using R5Flowstate.Spawn;

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

    private void OnUseEaPersonaChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;

        if (IsOfflineOn() && IsDediHostOnlineOn())
        {
            SetCheckSilently(ChkDediOnline, false);
            Log("Host online cleared: an offline launch skips the auth path Spire listing needs");
        }

        SyncOfflineNameText();
        OnLaunchOptionsChanged(sender, e);
    }

    private void SyncOfflineNameText()
    {
        if (TxtOfflineName is null)
            return;
        if (!TxtOfflineName.IsKeyboardFocusWithin)
            TxtOfflineName.Text = _settings.OfflineName;
    }

    private void OnOfflineNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist || TxtOfflineName is null)
            return;

        var cleaned = LaunchArgs.SanitizeOfflineName(TxtOfflineName.Text);
        TxtOfflineName.Text = cleaned;
        if (cleaned == _settings.OfflineName)
            return;

        _settings.OfflineName = cleaned;
        Log(cleaned.Length > 0 ? $"Offline name: {cleaned}" : "Offline name cleared (game default)");
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
            if (ChkUseEaPersona is not null)
                ChkUseEaPersona.IsChecked = !_settings.OfflineNoAuth;
            SyncOfflineNameText();
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
        // The strip and picker earn their place only when there is a real choice to make.
        var choice = !card.MapIsPinned && card.Maps.Count > 1;
        if (BarMapStrip is not null)
            BarMapStrip.Visibility = choice ? Visibility.Visible : Visibility.Collapsed;
        if (BtnMapPicker is not null)
            BtnMapPicker.Visibility = choice ? Visibility.Visible : Visibility.Collapsed;
        if (choice)
        {
            EnsureMapTiles();
        }
        else
        {
            _thumbCts?.Cancel();
            if (PanelMapPicker is not null)
                PanelMapPicker.Visibility = Visibility.Collapsed;
        }

        BindConsoleMapPicker();
    }

    private void RefreshHeroChrome()
    {
        var heroBefore = TxtHeroTitle?.Text + "|" + TxtHeroMap?.Text;
        if (TxtHeroTitle is not null)
            TxtHeroTitle.Text = _selectedMode?.Title ?? Loc.Get("pick_a_map");
        // A mode that IS its arena (Firing Range, Lobby) would otherwise read its own name twice.
        if (TxtHeroMap is not null)
            TxtHeroMap.Text = string.Equals(_selectedMode?.SelectedMap?.DisplayName,
                                  _selectedMode?.Title, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : _selectedMode?.SelectedMap?.DisplayName ?? string.Empty;
        if (TxtHeroStem is not null)
            TxtHeroStem.Text = _selectedMode?.SelectedMap?.Stem ?? string.Empty;
        if (TxtHeroSubtitle is not null)
            TxtHeroSubtitle.Text = _selectedMode?.Blurb ?? string.Empty;
        if (heroBefore != TxtHeroTitle?.Text + "|" + TxtHeroMap?.Text)
            AnimateHeroText();
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

    // Tiles flex between the arrows so a whole number always fits; the row keeps the art's proportions.
    private const double StripArrowSpace = 38;
    private const double StripGap = 8;
    private const double StripMinTileWidth = 116;
    private const double StripArtAspect = 2.2;
    private const double StripLabelHeight = 0;
    private const double StripGlowPad = 10;
    // Only the row's transform animates, so a move costs no layout.
    private const int StripSlideMs = 170;
    // Saving and decoding wait until the slide has finished, so they can never hitch it.
    private const int MapCommitDelayMs = 220;
    private static readonly IEasingFunction s_stripEase = CreateStripEase();
    private readonly List<Button> _stripPool = new();
    private readonly List<int> _stripPoolOffsets = new();
    private readonly TranslateTransform _stripShift = new();
    private readonly Canvas _stripRow = new();
    private List<MapTileViewModel> _stripTiles = new();
    private MapTileViewModel? _stripCenter;
    private int _stripRestHalf;
    private int _stripSlideId;
    private DispatcherTimer? _mapCommitTimer;
    private MapTileViewModel? _mapCommitTile;

    private static IEasingFunction CreateStripEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

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

        SelectMapTile(tile);
    }

    private void OnMapPrevious(object sender, RoutedEventArgs e) => StepMap(-1);

    private void OnMapNext(object sender, RoutedEventArgs e) => StepMap(1);

    private void OnMapStripWheel(object sender, MouseWheelEventArgs e)
    {
        StepMap(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void StepMap(int step)
    {
        var count = _mapTiles.Count;
        if (count < 2)
            return;
        var at = _mapTiles.FindIndex(t => t.IsSelected);
        SelectMapTile(_mapTiles[((at < 0 ? 0 : at) + step + count) % count]);
    }

    private void SelectMapTile(MapTileViewModel tile)
    {
        if (_selectedMode is null)
            return;

        _selectedMode.SelectedMap = tile.Option;
        foreach (var t in _mapTiles)
            t.IsSelected = ReferenceEquals(t, tile);

        if (PanelMapPicker is not null)
            PanelMapPicker.Visibility = Visibility.Collapsed;
        RefreshMapStrip();
        RefreshHeroChrome();

        _mapCommitTile = tile;
        if (_mapCommitTimer is null)
        {
            _mapCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MapCommitDelayMs) };
            _mapCommitTimer.Tick += (_, _) => CommitMapSelection();
        }
        _mapCommitTimer.Stop();
        _mapCommitTimer.Start();
    }

    private void CommitMapSelection()
    {
        _mapCommitTimer?.Stop();
        var tile = _mapCommitTile;
        _mapCommitTile = null;
        if (tile is null || !tile.IsSelected)
            return;

        var clock = Stopwatch.StartNew();
        BindConsoleMapPicker();
        // Only the mode's map changed, so skip the full settings pass and its folder checks.
        CaptureModeSettingsFromUi();
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        StartMissingThumbs();

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
        if (clock.ElapsedMilliseconds > 50)
            Log($"Map change blocked the UI for {clock.ElapsedMilliseconds} ms.");
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
            RefreshMapStrip();
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

        RefreshMapStrip();
        StartMissingThumbs();
    }

    /// <summary>
    /// The selected map sits in the middle. A move rebuilds the slots around the new center and starts
    /// the whole row shifted back by the same distance, so it slides into place.
    /// </summary>
    private void RefreshMapStrip()
    {
        if (BarMapStrip is null || CanvasMapStrip is null || BtnMapPicker is null)
            return;

        var width = BarMapStrip.ActualWidth;
        var count = _mapTiles.Count;
        var selectedAt = _mapTiles.FindIndex(t => t.IsSelected);
        if (width <= 0 || count == 0 || selectedAt < 0)
            return;

        var row = width - 2 * StripArrowSpace;
        var fit = (int)((row + StripGap) / (StripMinTileWidth + StripGap));
        fit = Math.Max(3, fit % 2 == 0 ? fit - 1 : fit);
        var tileWidth = Math.Floor((row - (fit - 1) * StripGap) / fit);
        var artHeight = Math.Round(tileWidth / StripArtAspect);
        var tileHeight = artHeight + StripLabelHeight;
        var slotWidth = tileWidth + StripGap;

        // Two maps cannot wrap without showing one twice, so they keep one side empty.
        var wraps = count >= 3;
        var shown = wraps ? Math.Min(fit, count % 2 == 0 ? count - 1 : count) : 3;
        var half = (shown - 1) / 2;
        var rowWidth = shown * slotWidth - StripGap;

        CanvasMapStrip.Width = rowWidth;
        CanvasMapStrip.Height = tileHeight + 2 * StripGlowPad;
        StripGlow.Width = tileWidth + 8;
        StripGlow.Height = tileHeight + 8;
        Canvas.SetLeft(StripGlow, rowWidth / 2 - tileWidth / 2 - 4);
        Canvas.SetTop(StripGlow, StripGlowPad - 4);
        Canvas.SetTop(_stripRow, StripGlowPad);
        BtnMapPrev.Height = artHeight;
        BtnMapNext.Height = artHeight;

        var center = _mapTiles[selectedAt];
        var previousAt = _stripCenter is null ? -1 : _mapTiles.IndexOf(_stripCenter);
        var move = previousAt < 0 ? 0 : selectedAt - previousAt;
        if (wraps && Math.Abs(move) > count / 2)
            move -= Math.Sign(move) * count;
        _stripCenter = center;
        _stripRestHalf = half;

        if (_stripRow.Parent is null)
        {
            _stripRow.RenderTransform = _stripShift;
            CanvasMapStrip.Children.Add(_stripRow);
        }

        var used = 0;
        var reach = half + Math.Abs(move);
        var visible = new List<(int Offset, MapTileViewModel Tile)>();
        for (var offset = -reach; offset <= reach; offset++)
        {
            var onNow = Math.Abs(offset) <= half;
            if (!onNow && Math.Abs(offset + move) > half)
                continue;

            var index = selectedAt + offset;
            if (wraps)
                index = ((index % count) + count) % count;
            else if (index < 0 || index >= count)
                continue;

            var tile = _mapTiles[index];
            var slot = StripSlot(used++);
            if (!ReferenceEquals(slot.DataContext, tile))
            {
                slot.DataContext = tile;
                slot.Tag = tile;
                slot.ToolTip = tile.DisplayName + "\n" + tile.Stem;
            }
            slot.Width = tileWidth;
            slot.Height = tileHeight;
            Canvas.SetLeft(slot, rowWidth / 2 + offset * slotWidth - tileWidth / 2);
            slot.Visibility = Visibility.Visible;
            _stripPoolOffsets[used - 1] = offset;
            if (onNow)
                visible.Add((offset, tile));
        }
        for (var i = used; i < _stripPool.Count; i++)
        {
            _stripPool[i].Visibility = Visibility.Collapsed;
            _stripPoolOffsets[i] = int.MaxValue;
        }

        _stripTiles = visible.OrderBy(v => Math.Abs(v.Offset)).Select(v => v.Tile).Distinct().ToList();
        BtnMapPicker.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (TxtMapPickerLabel is not null)
            TxtMapPickerLabel.Text = Loc.Format("all_maps_n", count);

        SlideMapStrip(move * slotWidth);
    }

    private Button StripSlot(int index)
    {
        while (_stripPool.Count <= index)
        {
            var slot = new Button { Style = (Style)FindResource("MapStripTile") };
            slot.Click += OnMapTileClick;
            _stripPool.Add(slot);
            _stripPoolOffsets.Add(int.MaxValue);
            _stripRow.Children.Add(slot);
        }
        return _stripPool[index];
    }

    private void SlideMapStrip(double distance)
    {
        var id = ++_stripSlideId;
        var from = _stripShift.X + distance;
        if (Math.Abs(from) < 0.5)
        {
            SettleMapStrip();
            return;
        }

        var slide = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(StripSlideMs)) { EasingFunction = s_stripEase };
        slide.Completed += (_, _) =>
        {
            if (id == _stripSlideId)
                SettleMapStrip();
        };
        _stripShift.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void SettleMapStrip()
    {
        _stripShift.BeginAnimation(TranslateTransform.XProperty, null);
        _stripShift.X = 0;
        for (var i = 0; i < _stripPool.Count; i++)
        {
            if (Math.Abs(_stripPoolOffsets[i]) > _stripRestHalf)
                _stripPool[i].Visibility = Visibility.Collapsed;
        }
    }

    private void StartMissingThumbs()
    {
        // Only art someone can see: the strip until the full picker opens.
        var wanted = PanelMapPicker?.Visibility == Visibility.Visible ? _mapTiles : _stripTiles;
        if (!wanted.Exists(t => t.Art is null))
            return;

        _thumbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        _ = LoadThumbnailsAsync(wanted.ToList(), cts.Token);
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
                        var art = LoadscreenResolver.ListLobbyPaks(root);
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
            SetHeroImage(cached);
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
            _artPaks = LoadscreenResolver.ListLobbyPaks(root);
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
            SetHeroImage(cached);
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
            SetHeroImage(null);
            return;
        }

        var bmp = ToBitmap(pixels);
        Remember(_loadscreenCache, _loadscreenLru, LoadscreenCacheCap, pakPath, bmp);
        SetHeroImage(bmp);
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
        SetHeroImage(image);
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
