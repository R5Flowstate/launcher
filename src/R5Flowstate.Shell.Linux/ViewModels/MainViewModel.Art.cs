using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using R5Flowstate.Content.Rpak;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux.ViewModels;

/// <summary>
/// The loadscreen lane: the hero pane's picture and the map tiles' thumbnails.
/// Ported from the Windows shell's MainWindow.Loadscreen.cs, which is where the
/// rules come from — the lobby's stub pak borrowing the standalone art set, the
/// shuffle button belonging to the lobby, tiles decoded lazily when the picker
/// opens, one worker per batch, and a cache keyed by stem. The decode itself is
/// <see cref="ArtDecode"/>'s, inside the prefix; <see cref="LoadscreenArt"/> reads
/// its cache files back.
///
/// Threading: decodes run on the pool (a load-screen rpak is a multi-megabyte
/// read plus a block decode), and every touch of VM state comes back through
/// <see cref="OnUi"/> rather than relying on which thread a continuation landed
/// on.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Windows' ThumbCacheCap: one playlist plus a leftover mode.</summary>
    const int ThumbCacheCap = 24;

    /// <summary>Show the map picker: Windows' rule is the card's own — a pinned
    /// mode has one map, so there is nothing to pick.</summary>
    [ObservableProperty] private bool _canPickMap;

    /// <summary>Show the shuffle button. Windows' RefreshLoadscreenChrome shows it
    /// for the lobby only: the lobby ships a stub loadscreen and borrows the
    /// standalone art set, so it is the one mode with art to shuffle.</summary>
    [ObservableProperty] private bool _canShuffleLoadscreen;

    readonly Dictionary<string, IImage> _thumbCache = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _thumbLru = new();

    CancellationTokenSource? _thumbCts;
    CancellationTokenSource? _heroCts;

    List<string> _artPaks = new();
    string _artPaksRoot = string.Empty;

    /// <summary>The art pak the hero is showing, when it is showing one.</summary>
    string? _shownArtPak;

    /// <summary>Windows' <c>_shownLoadscreenStem</c>: the map the hero is showing,
    /// set when a picture is shown or cleared rather than when one is asked for.
    /// That timing is the point — it is what makes "that one is already up" a real
    /// guard.</summary>
    string? _shownStem;

    /// <summary>
    /// Windows' <c>_inflightLoadscreenKey</c> (MainWindow.Loadscreen.cs:16): what
    /// the hero is decoding <em>right now</em>, or null. This is the guard a
    /// superseded decode needed and did not have — a second request for the picture
    /// already being decoded is not a request at all, so it must not cancel the
    /// batch that is decoding it.
    ///
    /// <para>Its absence was the whole bug in §25's report: every chrome pass
    /// (a playlist reload, a mode rebuild, a selection restore) ends at
    /// <see cref="RefreshHero"/>, which queues the hero, and a burst of those in one
    /// second killed the art host before it could write a single record — which the
    /// caller then read as a decoder that had failed.</para>
    /// </summary>
    string? _inflightKey;

    /// <summary>
    /// Called from <see cref="RefreshHero"/> — the hero buttons' two visibility
    /// rules are Windows' chrome pass, and the hero is exactly where they are
    /// drawn.
    /// </summary>
    internal void RefreshLoadscreenChrome()
    {
        CanPickMap = SelectedMode is { MapIsPinned: false } mode && mode.Maps.Count > 1;
        CanShuffleLoadscreen = IsLobbyStem(SelectedMode?.SelectedMapStem);
    }

    /// <summary>
    /// Queue the hero's art for what is selected now. Windows calls this from
    /// SelectModeCard and from the tile click, which is why the port does too:
    /// the rail, the hero and the grid cannot disagree about which map is up.
    /// </summary>
    internal void QueueLoadscreen()
    {
        var stem = SelectedMode?.SelectedMapStem;

        // The lobby's own pak is a stub, so its hero is art-set art instead.
        if (IsLobbyStem(stem))
        {
            QueueArtPak(reroll: false);
            return;
        }

        // Windows clears the shown pak for a map stem before it decides anything
        // else (MainWindow.Loadscreen.cs:518): a map hero is never the pak the
        // lobby left up.
        _shownArtPak = null;

        var root = InstallPath;
        if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(root))
        {
            _heroCts?.Cancel();
            _inflightKey = null;
            ShowHeroArt(null, null);
            return;
        }

        // Windows' two guards (MainWindow.Loadscreen.cs:527-531), and together they
        // are the fix: the same picture already being decoded is not a second
        // request, and neither is one already on screen. Without the first, each
        // rebuild burst started a decode per chrome pass and cancelled the one
        // before it, so the art host never got to write its records.
        var key = HeroKey(root, stem);
        if (string.Equals(_inflightKey, key, StringComparison.OrdinalIgnoreCase))
            return;
        if (string.Equals(_shownStem, stem, StringComparison.OrdinalIgnoreCase) && HeroArt is not null)
            return;

        _inflightKey = key;
        _heroCts?.Cancel();
        var cts = new CancellationTokenSource();
        _heroCts = cts;
        _ = MapArtAsync(root, stem, key, cts.Token);
    }

    /// <summary>Windows' CacheKey (MainWindow.Loadscreen.cs:594): the install root
    /// is part of the key, so the same stem reached through two installs is two
    /// pictures. Used for the in-flight identity; the decode cache itself is keyed
    /// by picture — see §25's note.</summary>
    static string HeroKey(string root, string stem)
        => root.TrimEnd('\\', '/') + "|" + stem;

    /// <summary>Windows' key for a standalone art pak: <c>art:&lt;path&gt;</c>.</summary>
    static string PakKey(string pakPath) => "art:" + pakPath;

    /// <summary>
    /// Windows clears the in-flight key as a decode returns, and only when it is
    /// still the decode that set it (MainWindow.Loadscreen.cs:568) — so a
    /// superseded decode never releases the key its replacement is holding. It goes
    /// through <see cref="OnUi"/> because that is where the field is read.
    /// </summary>
    void ClearInflight(string key)
    {
        OnUi(() =>
        {
            if (string.Equals(_inflightKey, key, StringComparison.OrdinalIgnoreCase))
                _inflightKey = null;
        });
    }

    /// <summary>The shuffle button: Windows' OnReloadLoadscreen, reroll: true.</summary>
    internal void RerollLoadscreen() => QueueArtPak(reroll: true);

    /// <summary>
    /// Decode the tiles' thumbnails. Windows defers this to the moment the picker
    /// opens (EnsureMapTiles): a burst of decodes for art nobody is looking at
    /// yet just starves the frame thread, and the tiles are hidden until then.
    /// The collection is snapshotted here, on the UI thread, before the worker
    /// starts touching the tiles.
    /// </summary>
    internal void EnsureMapTiles()
    {
        var card = SelectedMode;
        var root = InstallPath;
        if (card is null || string.IsNullOrWhiteSpace(root))
            return;

        var tiles = MapTiles.ToList();
        if (tiles.Count == 0)
            return;

        // Windows' rule for a picker that is opened again (MainWindow.Loadscreen.cs:189-197):
        // the tiles already holding art are left alone, and a batch only starts when
        // something is missing. A second open is therefore not a reason to abandon
        // the decode the first one started — which, before this, it was.
        if (!tiles.Exists(t => t.Art is null))
            return;

        _thumbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        _ = TileArtAsync(tiles, root, cts.Token);
    }

    /// <summary>
    /// One sequential worker per batch, exactly as Windows does it: each tile is
    /// a cache file read. The decoding itself happens once, up front, in
    /// <see cref="WarmAsync"/> — a tile read before the host has written its file
    /// would find nothing and then find it a moment later, which is a grid that
    /// fills in twice.
    /// </summary>
    async Task TileArtAsync(List<MapTileVM> tiles, string root, CancellationToken token)
    {
        // The lobby's tile is the one that borrows a random standalone art pak,
        // so it is the one pak this batch has to include.
        var lobbyPak = tiles.Exists(t => t.Art is null && IsLobbyStem(t.Stem))
            ? PickArtPak(root)
            : null;

        var stems = tiles
            .Where(t => t.Art is null && !IsLobbyStem(t.Stem))
            .Select(t => t.Stem)
            .Where(s => !string.IsNullOrWhiteSpace(s));

        var batch = await WarmAsync(root, LoadscreenArt.ThumbWidth, stems,
            lobbyPak is null ? null : new[] { lobbyPak }, token).ConfigureAwait(false);

        foreach (var tile in tiles)
        {
            if (token.IsCancellationRequested)
                return;

            // Already on the tile from a previous open -- leave it.
            if (tile.Art is not null)
                continue;

            var art = await TileArtForAsync(batch, tile.Stem,
                IsLobbyStem(tile.Stem) ? lobbyPak : null, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return;
            if (art is null)
                continue;

            var picture = art;
            OnUi(() =>
            {
                if (!token.IsCancellationRequested)
                    tile.Art = picture;
            });
        }
    }

    /// <summary>A tile's picture, through the cache unless it is the lobby's —
    /// the lobby re-rolls, so it never reads or fills one (Windows' rule).</summary>
    async Task<IImage?> TileArtForAsync(ArtBatch batch, string stem, string? lobbyPak, CancellationToken token)
    {
        if (lobbyPak is null && _thumbCache.TryGetValue(stem, out var cached))
            return cached;

        var result = lobbyPak is not null
            ? await LoadscreenArt.ForPakAsync(lobbyPak, LoadscreenArt.ThumbWidth).ConfigureAwait(false)
            : await LoadscreenArt.ForMapAsync(stem, LoadscreenArt.ThumbWidth).ConfigureAwait(false);

        if (!result.Ok)
        {
            // A tile with no art is a normal state (the install may not ship that
            // pak), and WarmAsync has already logged why the decoder produced
            // nothing. What it cannot see is a decode that worked and a cache file
            // that still will not read — so only that case gets a line here.
            if (batch.For(lobbyPak is null ? "stem" : "pak", lobbyPak ?? stem) is { Ok: true })
                LogOnly($"Loadscreen tile {stem}: {result.Reason}");
            return null;
        }

        if (lobbyPak is null)
            Remember(stem, result.Art!);
        return result.Art;
    }

    async Task MapArtAsync(string root, string stem, string key, CancellationToken token)
    {
        var batch = await WarmAsync(root, LoadscreenArt.HeroWidth, new[] { stem }, null, token)
            .ConfigureAwait(false);

        // Released as the decode returns, before the token is looked at: Windows
        // clears the key either way (MainWindow.Loadscreen.cs:568).
        ClearInflight(key);
        if (token.IsCancellationRequested)
            return;

        var result = await LoadscreenArt.ForMapAsync(stem, LoadscreenArt.HeroWidth)
            .ConfigureAwait(false);
        if (token.IsCancellationRequested)
            return;

        if (!result.Ok)
        {
            // Same rule as the tiles: the decoder's own reason is WarmAsync's line,
            // and this one is for a cache file that will not read after a decode
            // that worked.
            if (batch.For("stem", stem) is { Ok: true })
                LogOnly($"Loadscreen {stem}: {result.Reason}");
            OnUi(() => ShowHeroArt(stem, null));
            return;
        }

        var art = result.Art;
        OnUi(() =>
        {
            if (!token.IsCancellationRequested)
                ShowHeroArt(stem, art);
        });
    }

    async Task ArtPakAsync(string pakPath, string key, CancellationToken token)
    {
        var batch = await WarmAsync(InstallPath, LoadscreenArt.HeroWidth, null,
            new[] { pakPath }, token).ConfigureAwait(false);
        ClearInflight(key);
        if (token.IsCancellationRequested)
            return;

        var result = await LoadscreenArt.ForPakAsync(pakPath, LoadscreenArt.HeroWidth)
            .ConfigureAwait(false);
        if (token.IsCancellationRequested)
            return;

        if (!result.Ok)
        {
            if (batch.For("pak", pakPath) is { Ok: true })
                LogOnly($"Loadscreen {Path.GetFileName(pakPath)}: {result.Reason}");
            return;
        }

        var art = result.Art;
        OnUi(() =>
        {
            // Windows shows a pak decode only while that pak is still the one up
            // (MainWindow.Loadscreen.cs:487): a re-roll or a map selection in the
            // meantime must not have this picture land on top of it.
            if (token.IsCancellationRequested ||
                !string.Equals(_shownArtPak, pakPath, StringComparison.OrdinalIgnoreCase))
                return;

            HeroArt = art;
        });
    }

    /// <summary>
    /// Test seam: stands in for one run of the hosted decoder
    /// (<see cref="LoadscreenArt.EnsureCachedAsync"/>). Null — an unset seam — means
    /// the real batch, the idiom <see cref="DisplayModes"/>'s two overrides follow.
    /// The suite uses it to watch this lane's own bookkeeping: which requests reach
    /// the decoder, how many, and what the log says about a batch that was replaced
    /// before it reported. Never set outside the suite.
    /// </summary>
    internal static Func<ArtRequest, CancellationToken, Task<ArtBatch>>? BatchOverride;

    /// <summary>
    /// One run of the hosted decoder, for whatever is missing at this width. Every
    /// read above it is a cache read, so this call is what decides whether art
    /// appears at all — and it is the one that gets to say why when it does not:
    /// no art host installed, no Proton build, no install yet, a decoder that
    /// refused a specific pak, or a batch killed at its deadline.
    ///
    /// <para>A missing runtime is a note and no art, never an exception: a launcher
    /// that cannot draw a load-screen still has to start the game.</para>
    /// </summary>
    async Task<ArtBatch> WarmAsync(string root, int width, IEnumerable<string>? stems,
        IEnumerable<string>? paks, CancellationToken token)
    {
        // The seam sits above the Proton and install gate deliberately: it is the
        // decoder that is stood in for, not the gate, so a check can drive this lane
        // without a Proton build, a host or an install — and nothing is spawned. It
        // stands in for the *run*, not for this method: the note and the per-item
        // lines below are this launcher's own reading of a batch, and a check that
        // skipped them would not be checking the thing §25 was about.
        ArtBatch batch;
        if (BatchOverride is { } seam)
        {
            batch = await seam(new ArtRequest(
                    string.Empty, root ?? string.Empty, width,
                    stems is null ? null : new List<string>(stems),
                    paks is null ? null : new List<string>(paks),
                    Log: AppendLogThreadSafe),
                token).ConfigureAwait(false);
        }
        else
        {
            var proton = EffectiveProtonDir();
            if (string.IsNullOrWhiteSpace(proton) || string.IsNullOrWhiteSpace(root))
                return ArtBatch.Skipped("no Proton build or no install selected yet");

            batch = await LoadscreenArt.EnsureCachedAsync(proton, root, width, stems, paks,
                AppendLogThreadSafe, token).ConfigureAwait(false);
        }

        if (batch.Note.Length > 0)
            AppendLogThreadSafe($"Loadscreen art: {batch.Note}");

        foreach (var outcome in batch.Outcomes)
        {
            // One line per failed picture, except for the batches whose cause is
            // already the note: one the launcher walked away from, and one whose
            // host never wrote a record. Repeating either once per item is exactly
            // the noise §25 was about — the note is the honest version of the same
            // fact, and it is the one that carries the reason.
            if (!outcome.Ok
                && outcome.Reason != ArtDecode.ReplacedReason
                && outcome.Reason != ArtDecode.NotStartedReason)
            {
                AppendLogThreadSafe($"Loadscreen {outcome.Kind} {outcome.Input}: {outcome.Reason}");
            }
        }

        return batch;
    }

    /// <summary>Windows' QueueArtLoadscreen: a random art pak, never the one
    /// already up, and not a second time for the same call when art is already
    /// showing and the caller did not ask for a re-roll.</summary>
    void QueueArtPak(bool reroll)
    {
        var root = InstallPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            ShowHeroArt(null, null);
            return;
        }

        if (!string.Equals(_artPaksRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            _artPaks = LoadscreenResolver.ListArtPaks(root).ToList();
            _artPaksRoot = root;
        }

        if (_artPaks.Count == 0)
        {
            AppendLog("Loadscreen shuffle: this install ships no standalone art loadscreens.");
            ShowHeroArt(null, null);
            return;
        }

        // Windows' guard (MainWindow.Loadscreen.cs:435), and the second half of it
        // is the one this port was missing: a non-reroll request while *anything* is
        // being decoded is not a request. Keeping only the "art is already up" half
        // meant that during a decode — when both of those are still empty — every
        // chrome pass rolled a new random pak and killed the decode the last pass
        // had started, so the lobby's hero could restart forever without finishing.
        if (!reroll && (_shownArtPak is not null && HeroArt is not null || _inflightKey is not null))
            return;

        var pick = _artPaks[Random.Shared.Next(_artPaks.Count)];
        for (var tries = 0; tries < 4 && _artPaks.Count > 1 &&
             string.Equals(pick, _shownArtPak, StringComparison.OrdinalIgnoreCase); tries++)
        {
            pick = _artPaks[Random.Shared.Next(_artPaks.Count)];
        }

        AppendLog($"Loadscreen: {Path.GetFileName(pick)} (one of {_artPaks.Count} art loadscreens)");

        // Shown state before the decode, as Windows does it: the pick is what is up
        // from here on, so the picture the decode returns is still the one wanted,
        // and the guard above can see it.
        _shownStem = null;
        _shownArtPak = pick;

        var key = PakKey(pick);
        _inflightKey = key;
        _heroCts?.Cancel();
        var cts = new CancellationTokenSource();
        _heroCts = cts;
        _ = ArtPakAsync(pick, key, cts.Token);
    }

    string? PickArtPak(string root)
    {
        if (!string.Equals(_artPaksRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            _artPaks = LoadscreenResolver.ListArtPaks(root).ToList();
            _artPaksRoot = root;
        }
        return _artPaks.Count == 0 ? null : _artPaks[Random.Shared.Next(_artPaks.Count)];
    }

    /// <summary>Windows' ShowLoadscreen: the stem that is up and the picture, with
    /// null for both meaning "nothing". The stem is recorded on the show — a failure
    /// included — which is what lets <see cref="QueueLoadscreen"/> tell "already up"
    /// from "not up yet".</summary>
    void ShowHeroArt(string? stem, IImage? art)
    {
        _shownStem = stem;
        HeroArt = art;
    }

    void Remember(string stem, IImage art)
    {
        _thumbCache[stem] = art;
        _thumbLru.Remove(stem);
        _thumbLru.AddFirst(stem);
        while (_thumbLru.Count > ThumbCacheCap)
        {
            var old = _thumbLru.Last!.Value;
            _thumbLru.RemoveLast();
            _thumbCache.Remove(old);
        }
    }

    /// <summary>VM state is bound, so it is touched on the UI thread — the same
    /// rule <see cref="AppendLogThreadSafe"/> follows.</summary>
    static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
