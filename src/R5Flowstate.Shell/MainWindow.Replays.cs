using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

public sealed class ReplayTickViewModel
{
    public double Left { get; init; }
    public Brush Fill { get; init; } = Brushes.White;
}

public sealed class ReplayChapterViewModel
{
    public ReplayRowViewModel Row { get; init; } = null!;
    public string Label { get; init; } = "";
    public string Tip { get; init; } = "";
    public double StartSec { get; init; }
    public double Width { get; init; }
    public Brush Background { get; init; } = Brushes.Transparent;
    public Brush Foreground { get; init; } = Brushes.White;
    public Brush Border { get; init; } = Brushes.Transparent;
}

public sealed class ReplayClipViewModel
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class ReplayRowViewModel : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public DateTime Modified { get; init; }
    public long SizeBytes { get; init; }

    public string ModeText { get; init; } = "";
    public string MapText { get; init; } = "";
    public string ResultText { get; init; } = "";
    public Brush ResultBrush { get; init; } = Brushes.White;
    public string StatsText { get; init; } = "";
    public string LengthText { get; init; } = "";
    public string WhenText { get; init; } = "";
    public IReadOnlyList<ReplayTickViewModel> Ticks { get; init; } = Array.Empty<ReplayTickViewModel>();
    public IReadOnlyList<ReplayChapterViewModel> Chapters { get; set; } = Array.Empty<ReplayChapterViewModel>();
    public List<ReplayClipViewModel> Clips { get; } = new();
    public string BookmarksText { get; init; } = "";
    public string MapStem { get; init; } = "";
    public DateTime Day { get; init; }
    public string TimeText { get; init; } = "";
    public string InfoLine { get; init; } = "";
    public Visibility StripVisibility => Ticks.Count > 0 && Chapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    string _dayHeader = "";
    public string DayHeader
    {
        get => _dayHeader;
        set { _dayHeader = value; Changed(); Changed(nameof(DayHeaderVisibility)); }
    }
    public Visibility DayHeaderVisibility => _dayHeader.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    ImageSource? _art;
    public ImageSource? Art
    {
        get => _art;
        set { _art = value; Changed(); }
    }

    public Visibility ChaptersVisibility => Chapters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClipsVisibility => Clips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BookmarksVisibility => BookmarksText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string SearchText { get; init; } = "";

    bool _pinned;
    public bool IsPinned
    {
        get => _pinned;
        set { _pinned = value; Changed(); Changed(nameof(PinFill)); Changed(nameof(PinTip)); }
    }
    public Brush PinFill => _pinned ? PinOnBrush : Brushes.Transparent;
    public string PinTip => Loc.Get(_pinned ? "replays_unpin" : "replays_pin");
    public static Brush PinOnBrush { get; set; } = Brushes.Gold;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class MainWindow
{
    // Game-side contract: platform/demos/<yyyy-mm-dd>/<name>.r5dem (or .part while
    // recording or after a killed client). Names are [A-Za-z0-9_-]{1,64}.
    private static readonly Regex ReplayNameRx = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    // A clip is "<replay>_c<mmss>" or "<replay>_c<mmss>_<n>".
    private static readonly Regex ReplayClipRx = new("^(.+)_c[0-9]{4}(?:_[0-9]{1,2})?$", RegexOptions.Compiled);

    private const int ReplayListMax = 300;
    private const double ReplayStripWidth = 220.0;
    private const double ReplayChapterBarWidth = 520.0;
    private const double ReplayMomentLead = 5.0;

    private bool _replaysBusy;
    private List<ReplayRowViewModel> _replayRows = new();
    private ReplayIndexCache? _replayCache;
    private ReplaySettings? _replaySettings;
    private bool _replaySettingsLoading;

    /// <summary>Client started by Watch; while it is the live client there is no server console.</summary>
    private int? _replayClientPid;

    private ReplaySettings ReplayPrefs => _replaySettings ??= ReplaySettings.Load();

    private void OnSimpleTabReplays(object sender, RoutedEventArgs e)
    {
        if (TabBlockedBySetup())
            return;

        ApplySimpleTab(SimpleTab.Replays);
        _ = RefreshReplaysAsync();
    }

    private void OnReplaysRefresh(object sender, RoutedEventArgs e) =>
        _ = RefreshReplaysAsync();

    private string ReplaysDir() =>
        System.IO.Path.Combine(ReadInstallPathBox(), "platform", "demos");

    private void OnReplaysOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = ReplaysDir();
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("Replays: open folder failed: " + ex.Message);
        }
    }

    private readonly record struct ReplayScanProgress(int FilesDone, int FilesTotal, double Fraction);

    private sealed class ReplayScanResult
    {
        public List<(FileInfo File, string Name, ReplaySummary Summary)> Items { get; } = new();
        public long TotalBytes { get; set; }
    }

    private async Task RefreshReplaysAsync()
    {
        if (_replaysBusy || ListReplays is null)
            return;
        _replaysBusy = true;
        try
        {
            TxtReplaysStatus.Text = Loc.Get("replays_loading");
            BarReplays.Value = 0;
            BarReplays.Visibility = Visibility.Visible;
            var progress = new Progress<ReplayScanProgress>(p =>
            {
                BarReplays.Value = p.Fraction * 100.0;
                TxtReplaysStatus.Text = Loc.Format("replays_loading_n", p.FilesDone, p.FilesTotal);
            });
            var dir = ReplaysDir();
            _replayCache ??= ReplayIndexCache.Load();
            var cache = _replayCache;
            var scan = await Task.Run(() => ScanReplays(dir, cache, progress)).ConfigureAwait(true);
            _replayRows = BuildReplayRows(scan, ReplayPins.Load(dir));
            RefreshReplayModeFilter();
            ApplyReplayFilter();
            TxtReplaysStorage.Text = Loc.Format("replays_storage",
                (scan.TotalBytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture),
                ReplayPrefs.MaxGb);
        }
        catch (Exception ex)
        {
            Log("Replays: scan failed: " + ex.Message);
            TxtReplaysStatus.Text = Loc.Get("replays_empty");
        }
        finally
        {
            BarReplays.Visibility = Visibility.Collapsed;
            _replaysBusy = false;
        }
    }

    private static ReplayScanResult ScanReplays(string dir, ReplayIndexCache cache, IProgress<ReplayScanProgress>? progress)
    {
        var result = new ReplayScanResult();
        if (!Directory.Exists(dir))
            return result;

        var files = new DirectoryInfo(dir)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.Extension.Equals(".r5dem", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase))
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(ReplayListMax)
            .ToList();

        result.TotalBytes = files.Sum(f => f.Length);

        // Only files the cache does not know are walked; progress follows their bytes.
        var toRead = files.Where(f => !cache.TryGet(f, out _)).ToList();
        var totalBytes = Math.Max(1L, toRead.Sum(f => f.Length));
        long doneBytes = 0;
        long lastStep = -1;
        void Report(int filesDone, long bytes)
        {
            var step = bytes * 200 / totalBytes;
            if (step == lastStep)
                return;
            lastStep = step;
            progress?.Report(new ReplayScanProgress(filesDone, toRead.Count, Math.Min(1.0, (double)bytes / totalBytes)));
        }

        var readIndex = 0;
        foreach (var f in files)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(f.Name);
            if (!ReplayNameRx.IsMatch(name))
                continue;
            if (!cache.TryGet(f, out var summary))
            {
                var done = doneBytes;
                var idx = readIndex;
                var read = ReplayReader.Read(f, pos => Report(idx, done + pos));
                doneBytes += f.Length;
                readIndex++;
                Report(readIndex, doneBytes);
                // A .part is still growing; cache only finished replays.
                if (read is not null && f.Extension.Equals(".r5dem", StringComparison.OrdinalIgnoreCase))
                    cache.Put(f, read);
                if (read is null)
                    continue;
                summary = read;
            }
            result.Items.Add((f, name, summary));
        }
        cache.Save(files.Select(f => f.FullName));
        return result;
    }

    private Brush ThemeBrush(string key, string fallbackHex) =>
        TryFindResource(key) as Brush ?? (Brush)new BrushConverter().ConvertFromString(fallbackHex)!;

    private Brush MomentBrush(string type) => type switch
    {
        "kill" => ThemeBrush("Accent", "#3DDA8A"),
        "knock" => ThemeBrush("PingFair", "#E0C25A"),
        "death" or "knocked" => ThemeBrush("DangerLine", "#D95060"),
        "ring" => (Brush)new BrushConverter().ConvertFromString("#B79CF0")!,
        "bookmark" => ThemeBrush("TextPrimary", "#E9F1EC"),
        _ => Brushes.Transparent,
    };

    private static bool IsMarkerMoment(string type) =>
        type is "kill" or "knock" or "death" or "knocked" or "ring" or "bookmark";

    private List<ReplayRowViewModel> BuildReplayRows(ReplayScanResult scan, HashSet<string> pins)
    {
        ReplayRowViewModel.PinOnBrush = ThemeBrush("PingFair", "#E0C25A");
        var rows = new List<ReplayRowViewModel>();
        var byName = new Dictionary<string, ReplayRowViewModel>(StringComparer.OrdinalIgnoreCase);
        var clips = new List<(FileInfo File, string Name, ReplaySummary Summary)>();

        foreach (var item in scan.Items)
        {
            if (item.Summary.ClipStart >= 0 && ReplayClipRx.IsMatch(item.Name))
            {
                clips.Add(item);
                continue;
            }
            var row = BuildReplayRow(item.File, item.Name, item.Summary, pins.Contains(item.Name));
            rows.Add(row);
            byName[item.Name] = row;
        }

        foreach (var (file, name, summary) in clips)
        {
            var parent = ReplayClipRx.Match(name).Groups[1].Value;
            var label = Loc.Format("replays_clip_label", Clock(summary.ClipStart));
            if (byName.TryGetValue(parent, out var owner))
            {
                owner.Clips.Add(new ReplayClipViewModel { Name = name, Path = file.FullName, Label = label });
                continue;
            }
            // The replay it came from is gone: the clip stands on its own.
            var row = BuildReplayRow(file, name, summary, pins.Contains(name));
            row.Clips.Add(new ReplayClipViewModel { Name = name, Path = file.FullName, Label = label });
            rows.Add(row);
        }
        return rows;
    }

    private ReplayRowViewModel BuildReplayRow(FileInfo file, string name, ReplaySummary s, bool pinned)
    {
        var when = s.StartMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)s.StartMs).LocalDateTime
            : file.LastWriteTime;
        var bookmarks = ReplayReader.ReadBookmarks(file.FullName);
        var moments = s.Events.Where(e => IsMarkerMoment(e.Type)).ToList();
        moments.AddRange(bookmarks.Select(t => new ReplayEvent { Type = "bookmark", T = t }));

        var len = Math.Max(1, s.Seconds);
        var ticks = moments
            .Select(e => new ReplayTickViewModel
            {
                Left = Math.Clamp(e.T / len, 0.0, 1.0) * (ReplayStripWidth - 3.0),
                Fill = MomentBrush(e.Type),
            })
            .ToList();

        var kills = s.Events.Count(e => e.Type == "kill");
        var deaths = s.Events.Count(e => e.Type == "death");
        var chapters = ReplayReader.Chapters(s);
        var won = chapters.Count(c => c.Result > 0);
        var lost = chapters.Count(c => c.Result < 0);

        var mode = ModeLabel(s.Mode, s.ModeTitle);
        var map = MapLabel(s.Map);
        var sizeMb = file.Length / (1024.0 * 1024.0);
        var row = new ReplayRowViewModel
        {
            Name = name,
            Path = file.FullName,
            Modified = file.LastWriteTime,
            SizeBytes = file.Length,
            Title = when.ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture) + "   " + JoinNonEmpty("   |   ", mode, map),
            Detail = JoinNonEmpty("   |   ", s.Player, Clock(s.Seconds),
                sizeMb.ToString("0.0", CultureInfo.InvariantCulture) + " MB"),
            ModeText = mode.Length > 0 ? mode : "-",
            MapText = map.Length > 0 ? map : "-",
            WhenText = when.ToString("MMM d  HH:mm", CultureInfo.CurrentCulture),
            TimeText = when.ToString("d MMM yyyy", CultureInfo.CurrentCulture) + "   " + when.ToString("t", CultureInfo.CurrentCulture),
            Day = when.Date,
            MapStem = s.Map,
            InfoLine = JoinNonEmpty("   |   ", mode, s.Player,
                kills + deaths > 0 ? Loc.Format("replays_kd", kills, deaths) : "",
                sizeMb.ToString("0.0", CultureInfo.InvariantCulture) + " MB"),
            LengthText = Clock(s.Seconds),
            StatsText = kills + deaths > 0 ? Loc.Format("replays_kd", kills, deaths) : "",
            ResultText = won + lost > 0 ? Loc.Format("replays_result_wl", won, lost) : "",
            ResultBrush = won >= lost ? ThemeBrush("Accent", "#3DDA8A") : ThemeBrush("DangerFg", "#F3B7BE"),
            Ticks = ticks,
            BookmarksText = bookmarks.Count > 0
                ? Loc.Get("replays_bookmarks") + "  " + string.Join("   ", bookmarks.Take(12).Select(Clock))
                : "",
            SearchText = string.Join(' ', name, s.Player, mode, map, s.Mode, s.Map).ToLowerInvariant(),
            IsPinned = pinned,
        };

        var bar = new List<ReplayChapterViewModel>();
        foreach (var c in chapters)
        {
            var (bg, border, fg) = c.Result > 0
                ? (ThemeBrush("AccentDim", "#15291D"), ThemeBrush("WindowEdge", "#1F6141"), ThemeBrush("AccentBright", "#9FF0C0"))
                : c.Result < 0
                    ? (ThemeBrush("DangerBg", "#2A1418"), ThemeBrush("DangerLine", "#D95060"), ThemeBrush("DangerFg", "#F3B7BE"))
                    : (ThemeBrush("SurfaceRaised", "#18211C"), ThemeBrush("BorderLine", "#26332C"), ThemeBrush("TextSecondary", "#A9B8AF"));
            var word = Loc.Format(c.IsRound ? "replays_chapter_round" : "replays_chapter_fight", c.Number);
            bar.Add(new ReplayChapterViewModel
            {
                Row = row,
                Label = word,
                Tip = Loc.Format("replays_watch_from", word, Clock(c.Start)),
                StartSec = Math.Max(0.0, c.Start - ReplayMomentLead),
                Width = Math.Max(44.0, (c.End - c.Start) / len * ReplayChapterBarWidth - 3.0),
                Background = bg,
                Border = border,
                Foreground = fg,
            });
        }
        row.Chapters = bar;
        return row;
    }

    private static string Clock(double seconds)
    {
        var s = (int)Math.Max(0, seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", s / 60, s % 60);
    }

    private static string JoinNonEmpty(string sep, params string[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    // A #token title only localizes in game, so the playlist id stands in here.
    private static string ModeLabel(string mode, string title)
    {
        if (!string.IsNullOrWhiteSpace(mode) && Loc.TryGet("mode_title." + mode, out var localized))
            return localized;
        if (!string.IsNullOrWhiteSpace(title) && !title.StartsWith('#'))
            return title;
        if (string.IsNullOrWhiteSpace(mode))
            return "";
        var s = mode.StartsWith("fs_", StringComparison.OrdinalIgnoreCase) ? mode[3..] : mode;
        var words = s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Any(char.IsDigit) ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }

    private static string MapLabel(string map)
    {
        if (string.IsNullOrWhiteSpace(map))
            return "";
        var s = map.StartsWith("mp_rr_", StringComparison.OrdinalIgnoreCase) ? map[6..] :
                map.StartsWith("mp_", StringComparison.OrdinalIgnoreCase) ? map[3..] : map;
        var words = s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 2 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }

    // ------------------------------------------------------------------ filter

    private void RefreshReplayModeFilter()
    {
        if (CmbReplaysMode is null)
            return;
        var keep = CmbReplaysMode.SelectedItem as string;
        var all = Loc.Get("replays_all_modes");
        var modes = new List<string> { all };
        modes.AddRange(_replayRows.Select(r => r.ModeText).Where(m => m != "-").Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase));
        _replaySettingsLoading = true;
        CmbReplaysMode.ItemsSource = modes;
        CmbReplaysMode.SelectedItem = keep is not null && modes.Contains(keep) ? keep : all;
        _replaySettingsLoading = false;
    }

    private void ApplyReplayFilter()
    {
        if (ListReplays is null)
            return;
        var text = (TxtReplaysSearch?.Text ?? "").Trim().ToLowerInvariant();
        var mode = CmbReplaysMode?.SelectedItem as string;
        var allModes = mode is null || mode == Loc.Get("replays_all_modes");
        var rows = _replayRows
            .Where(r => text.Length == 0 || r.SearchText.Contains(text, StringComparison.Ordinal))
            .Where(r => allModes || string.Equals(r.ModeText, mode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DateTime? day = null;
        foreach (var r in rows)
        {
            r.DayHeader = r.Day == day ? "" : DayLabel(r.Day);
            day = r.Day;
        }
        ListReplays.ItemsSource = rows;
        StartReplayThumbs(rows);
        TxtReplaysStatus.Text = _replayRows.Count == 0
            ? Loc.Get("replays_empty")
            : string.Format(CultureInfo.InvariantCulture, Loc.Get("replays_count"), rows.Count);
    }

    private static string DayLabel(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today)
            return Loc.Get("replays_today");
        if (day == today.AddDays(-1))
            return Loc.Get("replays_yesterday");
        return day.ToString("dddd, MMM d", CultureInfo.CurrentUICulture);
    }

    private System.Threading.CancellationTokenSource? _replayThumbCts;

    /// <summary>Map loadscreen art for each row, sharing the map picker's thumbnail cache.</summary>
    private void StartReplayThumbs(List<ReplayRowViewModel> rows)
    {
        _replayThumbCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _replayThumbCts = cts;
        _ = LoadReplayThumbsAsync(rows, cts.Token);
    }

    private async Task LoadReplayThumbsAsync(List<ReplayRowViewModel> rows, System.Threading.CancellationToken token)
    {
        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            return;
        try
        {
            await Task.Run(() => R5Flowstate.Content.Rpak.LoadscreenResolver.PrepareInstall(root), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (var stem in rows.Select(r => r.MapStem).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (token.IsCancellationRequested)
                return;
            if (!_thumbCache.TryGetValue(stem, out var art))
            {
                try
                {
                    art = await Task.Run(() =>
                        R5Flowstate.Content.Rpak.LoadscreenResolver.TryDecode(root, stem, out var px, out _, ThumbWidth) && px is not null
                            ? ToThumbnail(px)
                            : null, token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    art = null;
                }
                if (art is null)
                    continue;
                Remember(_thumbCache, _thumbLru, ThumbCacheCap, stem, art);
            }
            foreach (var r in rows.Where(r => string.Equals(r.MapStem, stem, StringComparison.OrdinalIgnoreCase)))
                r.Art = art;
        }
    }

    private void OnReplaysSearchChanged(object sender, TextChangedEventArgs e) => ApplyReplayFilter();

    private void OnReplaysModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_replaySettingsLoading)
            ApplyReplayFilter();
    }

    // ------------------------------------------------------------------ rows

    private void OnReplayPin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ReplayRowViewModel row || !ReplayNameRx.IsMatch(row.Name))
            return;
        var dir = ReplaysDir();
        var pins = ReplayPins.Load(dir);
        if (row.IsPinned)
            pins.Remove(row.Name);
        else
            pins.Add(row.Name);
        foreach (var clip in row.Clips)
        {
            if (row.IsPinned)
                pins.Remove(clip.Name);
            else
                pins.Add(clip.Name);
        }
        if (ReplayPins.Save(dir, pins))
            row.IsPinned = !row.IsPinned;
    }

    private void OnReplayWatch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ReplayRowViewModel row)
            _ = LaunchReplayAsync(row.Name, -1.0, sender, e);
    }

    private void OnReplayWatchChapter(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ReplayChapterViewModel ch)
            _ = LaunchReplayAsync(ch.Row.Name, ch.StartSec, sender, e);
    }

    private void OnReplayWatchClip(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ReplayClipViewModel clip)
            _ = LaunchReplayAsync(clip.Name, -1.0, sender, e);
    }

    private async Task LaunchReplayAsync(string name, double seekSec, object sender, RoutedEventArgs e)
    {
        if (!ReplayNameRx.IsMatch(name))
            return;

        var root = TxtInstallRoot.Text.Trim();
        if (ProcessSpawner.IsRoleAlive(LaunchRole.Client, root))
        {
            var answer = MessageBox.Show(this, Loc.Get("replays_running_confirm"), Loc.Get("tab_replays"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;
            OnKillClient(sender, e);
            await Task.Delay(1500).ConfigureAwait(true);
        }

        PersistSettingsFromUi();
        var (ok, reason) = await EnsurePlayContentAsync(
            requireClient: true, requireServer: false, autoRepairCorrupt: true)
            .ConfigureAwait(true);
        if (!ok)
        {
            Log("Replay refused: " + reason);
            MessageBox.Show(this, reason ?? Loc.Get("msg_content_not_ready"), Loc.Get("title_content"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var args = new List<string>(BuildClientArgs(includeConnect: false)) { "-replay", name };
        if (seekSec >= 0)
            args.AddRange(new[] { "-replay_seek", seekSec.ToString("0.0", CultureInfo.InvariantCulture) });
        // A replay plays inside the client (it stands in for the server), so no
        // dedicated server is started; the console stays hidden like Play Local.
        Log("Replay: launching the game into " + name + (seekSec >= 0 ? " at " + Clock(seekSec) : ""));
        var result = SpawnRoleResult(LaunchRole.Client, args, quiet: _settings.SimpleMode);
        if (!result.Ok)
        {
            TxtReplaysStatus.Text = Loc.Get("replays_launch_failed");
            Log("Replay: client spawn failed: " + (result.Error ?? "unknown"));
            return;
        }
        _lastClientPid = result.ProcessId;
        _replayClientPid = result.ProcessId;
        UpdateKillButtons();
        RefreshConsoleChrome();
        TxtReplaysStatus.Text = Loc.Get("replays_launching");
    }

    private void OnReplayDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ReplayRowViewModel row)
            return;
        var answer = MessageBox.Show(this, string.Format(CultureInfo.InvariantCulture,
                Loc.Get("replays_delete_confirm"), row.Title),
            Loc.Get("tab_replays"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            var full = System.IO.Path.GetFullPath(row.Path);
            var dir = System.IO.Path.GetFullPath(ReplaysDir()) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                return;
            File.Delete(full);
            if (File.Exists(full + ".marks"))
                File.Delete(full + ".marks");
        }
        catch (Exception ex)
        {
            Log("Replays: delete failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, Loc.Get("tab_replays"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _ = RefreshReplaysAsync();
    }

    // ------------------------------------------------------------------ settings

    private static readonly int[] s_replayKeepDays = { 7, 14, 30, 0 };
    private static readonly int[] s_replayMaxGb = { 4, 10, 25 };

    private void OnReplaysSettings(object sender, RoutedEventArgs e)
    {
        if (PopReplaysSettings is null)
            return;
        _replaySettingsLoading = true;
        var p = ReplayPrefs;
        ChkReplayRecord.IsChecked = p.AutoRecord;
        CmbReplayKeep.ItemsSource = s_replayKeepDays.Select(d => d == 0 ? Loc.Get("replays_never") : Loc.Format("replays_days", d)).ToList();
        CmbReplayKeep.SelectedIndex = Math.Max(0, Array.IndexOf(s_replayKeepDays, p.KeepDays));
        CmbReplayMax.ItemsSource = s_replayMaxGb.Select(g => Loc.Format("replays_gb", g)).ToList();
        CmbReplayMax.SelectedIndex = Math.Max(0, Array.IndexOf(s_replayMaxGb, p.MaxGb));
        _replaySettingsLoading = false;
        PopReplaysSettings.IsOpen = !PopReplaysSettings.IsOpen;
    }

    private void OnReplaySettingChanged(object sender, RoutedEventArgs e)
    {
        if (_replaySettingsLoading || ChkReplayRecord is null)
            return;
        var p = ReplayPrefs;
        p.AutoRecord = ChkReplayRecord.IsChecked == true;
        if (CmbReplayKeep.SelectedIndex >= 0)
            p.KeepDays = s_replayKeepDays[CmbReplayKeep.SelectedIndex];
        if (CmbReplayMax.SelectedIndex >= 0)
            p.MaxGb = s_replayMaxGb[CmbReplayMax.SelectedIndex];
        p.Save();
        RefreshArgPreviews();
    }

    /// <summary>Recording options for every client launch.</summary>
    private IReadOnlyList<string> ReplayClientTokens() => ReplayPrefs.ClientTokens();
}
