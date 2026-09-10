using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using R5Flowstate.Content;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    private HostedConsoleTap? _consoleServer;
    private HostedConsoleTap? _consoleClient;

    /// <summary>
    /// Boot alone is tens of thousands of lines. Appending each one as it
    /// arrives relayouts the document per line and locks the UI thread, so
    /// lines queue and land in timed batches instead.
    /// </summary>
    private const int ConsoleMaxLines = 4000;
    private const int ConsoleTrimLines = 1000;
    private const int ConsoleBatchLimit = 2000;

    private readonly ConcurrentQueue<string> _pendingServer = new();
    private readonly ConcurrentQueue<string> _pendingClient = new();
    private readonly Dictionary<RichTextBox, int> _consoleBreaks = new();
    private DispatcherTimer? _consoleTimer;
    private bool _followServer = true;
    private bool _followClient = true;
    private bool _consoleScrollHooked;


    private static readonly Dictionary<int, SolidColorBrush> s_ansiBrushes = new();

    private void OnSimpleTabConsole(object sender, RoutedEventArgs e)
    {
        if (TabBlockedBySetup())
            return;

        if (!_settings.SimpleMode && _simpleTab == SimpleTab.Console)
        {
            ApplySimpleTab(SimpleTab.Play);
            return;
        }

        ApplySimpleTab(SimpleTab.Console);
    }

    private void OnUseDx12Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        var on = sender is CheckBox box && box.IsChecked == true;
        _settings.UseDx12 = on;
        _suppressArgsPersist = true;
        try
        {
            if (ChkUseDx12 is not null)
                ChkUseDx12.IsChecked = on;
            if (ChkClientDx12 is not null)
                ChkClientDx12.IsChecked = on;
            if (ChkServersDx12 is not null)
                ChkServersDx12.IsChecked = on;
        }
        finally
        {
            _suppressArgsPersist = false;
        }
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        RefreshDx12Chrome();
        // The renderer is the EXE, so a live game keeps the old one until it is
        // relaunched -- repaint so the button offers that restart right away.
        RefreshSimplePlayButton();
    }

    sealed record DownloadLimitOption(int Mbps, string Label)
    {
        public override string ToString() => Label;
    }

    static DownloadLimitOption[] BuildDownloadLimits() =>
    [
        new(0, Loc.Get("unlimited")),
        new(200, Loc.Format("mbps_fmt", 200)),
        new(100, Loc.Format("mbps_fmt", 100)),
        new(50, Loc.Format("mbps_fmt", 50)),
        new(25, Loc.Format("mbps_fmt", 25)),
        new(10, Loc.Format("mbps_fmt", 10)),
        new(5, Loc.Format("mbps_fmt", 5)),
    ];

    bool _suppressLimitSync;

    void InitDownloadLimit()
    {
        // The same setting is offered in Settings and on the install card; both
        // are filled from one list so a change on either reads back on the other.
        _suppressLimitSync = true;
        try
        {
            foreach (var box in new[] { CmbDownloadLimit, CmbDownloadLimitInstall })
            {
                if (box is null)
                    continue;
                var limits = BuildDownloadLimits();
                box.ItemsSource = limits;
                box.SelectedItem =
                    limits.FirstOrDefault(o => o.Mbps == _settings.DownloadLimitMbps)
                    ?? limits[0];
            }
        }
        finally
        {
            _suppressLimitSync = false;
        }
        ApplyDownloadLimit();
    }

    void SyncDownloadLimitBoxes(int mbps)
    {
        _suppressLimitSync = true;
        try
        {
            foreach (var box in new[] { CmbDownloadLimit, CmbDownloadLimitInstall })
            {
                if (box?.ItemsSource is not IEnumerable<DownloadLimitOption> opts)
                    continue;
                var match = opts.FirstOrDefault(o => o.Mbps == mbps);
                if (match is not null && !ReferenceEquals(box.SelectedItem, match))
                    box.SelectedItem = match;
            }
        }
        finally
        {
            _suppressLimitSync = false;
        }
    }

    void ApplyDownloadLimit()
    {
        FileSystemFetcher.GlobalMaxBytesPerSecond = _settings.DownloadLimitMbps * 125_000L;
        ContentExecutor.GlobalConcurrency = _settings.DownloadConcurrency;
    }

    private void OnDownloadLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist || _suppressLimitSync)
            return;
        if (sender is not ComboBox box || box.SelectedItem is not DownloadLimitOption opt)
            return;
        _settings.DownloadLimitMbps = opt.Mbps;
        SyncDownloadLimitBoxes(opt.Mbps);
        ApplyDownloadLimit();
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        Log(opt.Mbps > 0
            ? $"Download limit: {opt.Mbps} Mbps"
            : "Download limit: unlimited");
    }

    /// <summary>
    /// Probing the install root is a disk hit, so the answer is cached here and
    /// the per-beat painters read the flag instead.
    /// </summary>
    private void RefreshDx12Chrome()
    {
        _dx12Available = ProcessSpawner.Dx12Available(ReadInstallPathBox());
        PaintDx12Tooltip();
    }

    private void PaintDx12Tooltip()
    {
        var tip = !_dx12Available
            ? Loc.Get("tip_dx12_missing")
            : _simplePlayKind == SimplePlayKind.RestartClient
                ? Loc.Get("tip_dx12_needs_restart")
                : Loc.Get("tip_dx12");
        if (ChkUseDx12 is not null)
            ChkUseDx12.ToolTip = tip;
        if (ChkClientDx12 is not null)
            ChkClientDx12.ToolTip = tip;
        if (ChkServersDx12 is not null)
            ChkServersDx12.ToolTip = tip;
    }

    private void OnToggleServersClientOptions(object sender, RoutedEventArgs e)
    {
        if (PanelServersClientOptions is null)
            return;

        var open = PanelServersClientOptions.Visibility != Visibility.Visible;
        PanelServersClientOptions.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
            SyncServersClientOptions();
    }

    /// <summary>The browser's copy of the client bar shares one stored setting with it.</summary>
    private void SyncServersClientOptions()
    {
        var restore = _suppressArgsPersist;
        _suppressArgsPersist = true;
        try
        {
            if (ChkServersDx12 is not null)
                ChkServersDx12.IsChecked = _settings.UseDx12;
            ApplyResolutionFields();
        }
        finally
        {
            _suppressArgsPersist = restore;
        }
    }

    private void OnOpenConsoleOnLaunchChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        _settings.OpenConsoleOnLaunch = ChkOpenConsoleOnLaunch?.IsChecked == true;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
    }

    private void MaybeOpenConsoleForLaunch()
    {
        if (_settings.OpenConsoleOnLaunch)
            ApplySimpleTab(SimpleTab.Console);
    }

    /// <summary>
    /// A browser join runs no dedi of ours, so the server pane would sit there
    /// empty with a command box that reaches nothing.
    /// </summary>
    private bool ConsoleServerPaneOpen() =>
        _joinedServer is null || LocalDediAlive();

    private void ApplyConsoleView()
    {
        if (RowConsoleServer is null || RowConsoleClient is null)
            return;

        var server = ConsoleServerPaneOpen();
        if (PaneConsoleServer is not null)
            PaneConsoleServer.Visibility = server ? Visibility.Visible : Visibility.Collapsed;

        RowConsoleServer.Height = server
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        RowConsoleClient.Height = new GridLength(1, GridUnitType.Star);
        if (RowConsoleSplit is not null)
            RowConsoleSplit.Height = new GridLength(server ? 10 : 0);

        RefreshConsoleMapChrome();
    }

    private void RefreshConsoleChrome()
    {
        ApplyConsoleView();
        if (BtnConsoleAction is not null)
            BtnConsoleAction.Visibility =
                _simpleTab == SimpleTab.Console
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        BindConsoleMapPicker();
    }

    private bool ConsoleHostControlsOpen()
    {
        return _simpleTab == SimpleTab.Console
            && SessionLive
            && _localRcon is not null
            && LocalDediAlive();
    }

    /// <summary>
    /// The mode/map pickers are the Play tab's rail in combo form: they steer a
    /// dedi of ours, so a browser join has nothing for them to point at.
    /// </summary>
    private bool ConsolePickersOpen()
    {
        return _simpleTab == SimpleTab.Console
            && ConsoleServerPaneOpen()
            && _simplePlayKind is not (SimplePlayKind.Install
                or SimplePlayKind.Repair
                or SimplePlayKind.Update
                or SimplePlayKind.SetUp
                or SimplePlayKind.Installing);
    }

    private bool ConsoleMapPickerOpen()
    {
        var card = _selectedMode;
        return ConsolePickersOpen()
            && card is { MapIsPinned: false }
            && card.Maps.Count > 1;
    }

    private void RefreshConsoleMapChrome()
    {
        var pickers = ConsolePickersOpen();
        var host = ConsoleHostControlsOpen();
        if (CmbConsoleMode is not null)
        {
            CmbConsoleMode.Visibility = pickers && _modeCards.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            CmbConsoleMode.IsEnabled = !_changeMapBusy;
        }
        if (CmbConsoleMap is not null)
        {
            CmbConsoleMap.Visibility = ConsoleMapPickerOpen()
                ? Visibility.Visible
                : Visibility.Collapsed;
            CmbConsoleMap.IsEnabled = !_changeMapBusy;
        }
        if (BtnReloadLevel is not null)
        {
            BtnReloadLevel.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
            BtnReloadLevel.IsEnabled = host && !_changeMapBusy;
        }
    }

    private void BindConsoleMapPicker()
    {
        RefreshConsoleMapChrome();
        BindConsoleModePicker();
        if (CmbConsoleMap is null)
            return;

        var card = _selectedMode;
        if (card is null)
            return;

        if (ReferenceEquals(CmbConsoleMap.ItemsSource, card.Maps) &&
            ReferenceEquals(CmbConsoleMap.SelectedItem, card.SelectedMap))
            return;

        _suppressSimpleMap = true;
        try
        {
            if (!ReferenceEquals(CmbConsoleMap.ItemsSource, card.Maps))
                CmbConsoleMap.ItemsSource = card.Maps;
            CmbConsoleMap.SelectedItem = card.SelectedMap;
        }
        finally
        {
            _suppressSimpleMap = false;
        }
    }

    private void BindConsoleModePicker()
    {
        if (CmbConsoleMode is null)
            return;

        if (ReferenceEquals(CmbConsoleMode.ItemsSource, _modeCards) &&
            CmbConsoleMode.Items.Count == _modeCards.Count &&
            ReferenceEquals(CmbConsoleMode.SelectedItem, _selectedMode))
            return;

        _suppressSimpleMap = true;
        try
        {
            CmbConsoleMode.ItemsSource = null;
            CmbConsoleMode.ItemsSource = _modeCards;
            CmbConsoleMode.SelectedItem = _selectedMode;
        }
        finally
        {
            _suppressSimpleMap = false;
        }
    }

    private void OnConsoleModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist || _suppressSimpleMap)
            return;
        if (CmbConsoleMode?.SelectedItem is ModeCardViewModel card)
            SelectModeCard(card, persist: true);
    }

    private void OnConsoleMapSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist || _suppressSimpleMap)
            return;
        if (_selectedMode is null)
            return;
        if (CmbConsoleMap?.SelectedItem is MapOption opt)
            _selectedMode.SelectedMap = opt;

        PersistSettingsFromUi();
        RefreshHeroChrome();
        QueueLoadscreen();
        RefreshChangeMapButton();
    }

    private void OnConsoleAction(object sender, RoutedEventArgs e) => OnPlay(sender, e);

    private void OnConsoleClear(object sender, RoutedEventArgs e)
    {
        ClearConsoleBox(TxtConsoleServer, _pendingServer);
        ClearConsoleBox(TxtConsoleClient, _pendingClient);
    }

    private void BindConsoleTaps()
    {
        ApplyConsoleView();
        ResetConsolePane(TxtConsoleServer);
        ResetConsolePane(TxtConsoleClient);
        HookConsoleFollow();
        InitConsoleFind();

        _consoleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _consoleTimer.Tick += (_, _) =>
        {
            DrainConsole(_pendingServer, TxtConsoleServer);
            DrainConsole(_pendingClient, TxtConsoleClient);
            ConsoleFindTick();
            if (_pendingServer.IsEmpty && _pendingClient.IsEmpty
                && !_consoleFind.Any(s => s.Open))
            {
                _consoleTimer.Stop();
                // A line enqueued between the drain and the stop saw the timer
                // still enabled and did not restart it.
                if (!_pendingServer.IsEmpty || !_pendingClient.IsEmpty)
                    _consoleTimer.Start();
            }
        };
    }

    private static void ResetConsolePane(RichTextBox? box)
    {
        if (box is null)
            return;
        box.Document = new FlowDocument(new Paragraph { Margin = new Thickness(0) })
        {
            PageWidth = 4000,
            PagePadding = new Thickness(0),
        };
    }

    private void HookConsoleFollow()
    {
        if (_consoleScrollHooked)
            return;
        _consoleScrollHooked = true;
        TxtConsoleServer?.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnConsoleScrollChanged), true);
        TxtConsoleClient?.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnConsoleScrollChanged), true);
    }

    /// <summary>
    /// Follow the tail unless the user has scrolled away from it. A collapsed
    /// pane has ViewportHeight 0, so that measurement is ignored.
    /// </summary>
    private void OnConsoleScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not RichTextBox box)
            return;
        if (e.ViewportHeight < 8)
            return;
        if (e.ExtentHeightChange != 0)
            return;

        var atEnd = e.ExtentHeight <= e.ViewportHeight + 8
            || e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 8;
        if (atEnd)
            SetConsoleFollow(box, true);
        else if (e.VerticalChange < 0)
            SetConsoleFollow(box, false);
    }

    private void SetConsoleFollow(RichTextBox box, bool follow)
    {
        if (ReferenceEquals(box, TxtConsoleServer))
            _followServer = follow;
        else if (ReferenceEquals(box, TxtConsoleClient))
            _followClient = follow;
    }

    private bool ConsoleFollows(RichTextBox box)
    {
        if (ConsoleFindHolds(box))
            return false;
        if (ReferenceEquals(box, TxtConsoleServer))
            return _followServer;
        if (ReferenceEquals(box, TxtConsoleClient))
            return _followClient;
        return true;
    }

    /// <summary>Collapsed panes do not keep a real offset -- pin when the tab opens.</summary>
    private void PinFollowedConsoles()
    {
        if (TxtConsoleServer is { } server && ConsoleFollows(server))
            ScrollConsoleToEnd(server);
        if (TxtConsoleClient is { } client && ConsoleFollows(client))
            ScrollConsoleToEnd(client);
    }

    private static void ScrollConsoleToEnd(RichTextBox? box)
    {
        if (box is null)
            return;
        box.CaretPosition = box.Document.ContentEnd;
        box.ScrollToEnd();
    }

    private static Paragraph? PaneBody(RichTextBox? box) =>
        box?.Document.Blocks.FirstBlock as Paragraph;

    private void DrainConsole(ConcurrentQueue<string> queue, RichTextBox? box)
    {
        if (box is null || queue.IsEmpty)
            return;

        var body = PaneBody(box);
        if (body is null)
            return;

        var follow = ConsoleFollows(box);
        var breaks = _consoleBreaks.TryGetValue(box, out var known) ? known : 0;

        var taken = 0;
        while (taken < ConsoleBatchLimit && queue.TryDequeue(out var line))
        {
            taken++;
            if (AppendAnsiLine(body, line))
                breaks++;
        }

        if (taken == 0)
            return;

        TrimPane(body, box, breaks);
        BumpConsoleRev(box);

        if (!follow)
            return;

        ScrollConsoleToEnd(box);
        box.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ConsoleFollows(box))
                ScrollConsoleToEnd(box);
        });
    }

    /// <summary>True when the line opened with a break (i.e. it was not the first).</summary>
    private static bool AppendAnsiLine(Paragraph body, string line)
    {
        var inlines = body.Inlines;
        if (inlines.Count > 0)
        {
            inlines.Add(new LineBreak());
            AppendAnsiLineSegments(inlines, line);
            return true;
        }

        AppendAnsiLineSegments(inlines, line);
        return false;
    }

    private static void AppendAnsiLineSegments(InlineCollection inlines, string line)
    {
        Brush? colour = null;
        var i = 0;
        var run = 0;
        while (i < line.Length)
        {
            if (line[i] != '\u001b')
            {
                i++;
                continue;
            }

            if (i > run)
                inlines.Add(MakeRun(line[run..i], colour));

            var end = i + 1;
            if (end < line.Length && line[end] == '[')
            {
                end++;
                while (end < line.Length && !char.IsLetter(line[end]))
                    end++;
                if (end < line.Length && line[end] == 'm')
                    colour = ApplySgr(line[(i + 2)..end], colour);
                if (end < line.Length)
                    end++;
            }
            else if (end < line.Length)
            {
                end++;
            }

            i = end;
            run = end;
        }

        if (run < line.Length)
            inlines.Add(MakeRun(line[run..], colour));
    }

    private static Run MakeRun(string text, Brush? colour)
    {
        var r = new Run(text);
        if (colour is not null)
            r.Foreground = colour;
        return r;
    }

    /// <summary>
    /// The SDK emits truecolor only: 38;2;r;g;b to set, 0 to reset. Anything
    /// else (cursor moves, erase-to-EOL) leaves the current colour alone.
    /// </summary>
    private static Brush? ApplySgr(string body, Brush? current)
    {
        if (body.Length == 0 || body == "0")
            return null;

        var parts = body.Split(';');
        if (parts.Length >= 5 && parts[0] == "38" && parts[1] == "2"
            && int.TryParse(parts[2], out var r)
            && int.TryParse(parts[3], out var g)
            && int.TryParse(parts[4], out var b))
        {
            var key = (r << 16) | (g << 8) | b;
            if (!s_ansiBrushes.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
                brush.Freeze();
                s_ansiBrushes[key] = brush;
            }

            return brush;
        }

        return current;
    }

    private void TrimPane(Paragraph body, RichTextBox box, int breaks)
    {
        if (breaks <= ConsoleMaxLines)
        {
            _consoleBreaks[box] = breaks;
            return;
        }

        var target = ConsoleMaxLines - ConsoleTrimLines;
        var drop = breaks - target;
        while (drop > 0 && body.Inlines.FirstInline is { } first)
        {
            body.Inlines.Remove(first);
            if (first is LineBreak)
                drop--;
        }

        _consoleBreaks[box] = target;
    }

    private void ResetClientConsoleForNewSession()
    {
        _consoleClient?.Dispose();
        _consoleClient = null;
        ClearConsoleBox(TxtConsoleClient, _pendingClient);
        ApplyConsoleView();
    }

    private IReadOnlyDictionary<string, string>? BeginHostedConsole(LaunchRole role)
    {
        if (role == LaunchRole.Dedicated)
        {
            _consoleServer?.Dispose();
            _consoleServer = null;
            ClearConsoleBox(TxtConsoleServer, _pendingServer);
        }
        else
        {
            _consoleClient?.Dispose();
            _consoleClient = null;
            ClearConsoleBox(TxtConsoleClient, _pendingClient);
        }

        var tap = HostedConsoleTap.Create(role);
        tap.LineReceived += line => AppendConsoleLine(role, line);
        if (role == LaunchRole.Dedicated)
            tap.LineReceived += OnHostedDediLine;
        else
            tap.LineReceived += OnHostedClientLine;
        tap.Start();

        if (role == LaunchRole.Dedicated)
        {
            _consoleServer = tap;
            AppendConsoleLine(role, "(server console)");
        }
        else
        {
            _consoleClient = tap;
            AppendConsoleLine(role, "(client console)");
            // Tab must be up before Process.Start -- the child prints as soon
            // as SDK_SetupConsole runs, and a closed pane trims those lines.
            ApplySimpleTab(SimpleTab.Console);
        }

        ApplyConsoleView();
        return tap.ToEnvironment();
    }

    /// <summary>
    /// Map load can fail without the dedi process exiting (Level not valid).
    /// An uncaught SCRIPT ERROR schedules host shutdown the same way.
    /// </summary>
    private void OnHostedDediLine(string line)
    {
        if (HostReadyGate.IsReadyLine(line, _liveMap))
        {
            _hostReady?.SignalFromConsole();
            NoteHostLevelReady();
        }

        var pending = _pendingChangeMap;
        if (pending is not null && HostReadyGate.IsMapReadyLine(line, pending))
            NoteMapChangeReady();

        if (HostReadyGate.IsLevelLoadLine(line))
            NoteHostLevelLoad();

        var scriptError = HostReadyGate.ScriptErrorExcerpt(line);
        if (scriptError is not null)
        {
            _lastScriptError = scriptError;
            _lastScriptErrorUtc = DateTime.UtcNow;
            return;
        }

        var fatal = HostReadyGate.IsFatalLine(line);
        if (!fatal && HostReadyGate.IsHostShutdownLine(line))
        {
            // Teardown right after an uncaught script error is the error
            // ending the match; every other teardown is boot or changelevel.
            fatal = _lastScriptError is not null
                && DateTime.UtcNow - _lastScriptErrorUtc < ScriptErrorShutdownWindow
                && _hostHeartbeatGraceUntilUtc != DateTime.MaxValue;
            if (fatal)
                NoteHostLevelLoad();
        }
        if (!fatal)
            return;

        _hostReady?.SignalFatal();
        _pendingFault = HostedServerFault.Crashed;
        _pendingCrashExcerpt = RecentScriptError();
        QueueHostedServerCrash();
    }

    private void OnHostedClientLine(string line)
    {
        var text = HostReadyGate.ClientErrorDialogText(line);
        if (text is null)
            return;
        Dispatcher.BeginInvoke(() => OnHostedClientLostServer(text));
    }

    private const int ConsoleHistoryCap = 50;

    /// <summary>Up/Down walk position and the in-progress line it returns to.</summary>
    private sealed class ConsoleHistoryNav
    {
        public int Index = -1;
        public string Draft = string.Empty;
    }

    private readonly ConsoleHistoryNav _historyNavServer = new();
    private readonly ConsoleHistoryNav _historyNavClient = new();

    private void OnConsoleServerCmdKeyDown(object sender, KeyEventArgs e) =>
        HandleConsoleCmdKey(e, TxtConsoleServerCmd, LaunchRole.Dedicated,
            _historyNavServer, _settings.ConsoleHistoryServer);

    private void OnConsoleClientCmdKeyDown(object sender, KeyEventArgs e) =>
        HandleConsoleCmdKey(e, TxtConsoleClientCmd, LaunchRole.Client,
            _historyNavClient, _settings.ConsoleHistoryClient);

    private void HandleConsoleCmdKey(
        KeyEventArgs e, TextBox? box, LaunchRole role,
        ConsoleHistoryNav nav, List<string> history)
    {
        if (box is null)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                RecordConsoleHistory(history, box.Text.Trim());
                nav.Index = -1;
                nav.Draft = string.Empty;
                SendConsoleCommand(role, box);
                e.Handled = true;
                break;

            case Key.Up:
                if (history.Count == 0)
                    return;
                if (nav.Index < 0)
                {
                    nav.Draft = box.Text;
                    nav.Index = history.Count - 1;
                }
                else if (nav.Index > 0)
                {
                    nav.Index--;
                }
                box.Text = history[nav.Index];
                box.CaretIndex = box.Text.Length;
                e.Handled = true;
                break;

            case Key.Down:
                if (nav.Index < 0)
                    return;
                nav.Index++;
                if (nav.Index >= history.Count)
                {
                    nav.Index = -1;
                    box.Text = nav.Draft;
                }
                else
                {
                    box.Text = history[nav.Index];
                }
                box.CaretIndex = box.Text.Length;
                e.Handled = true;
                break;
        }
    }

    private void RecordConsoleHistory(List<string> history, string line)
    {
        if (line.Length == 0)
            return;
        if (history.Count > 0 && string.Equals(history[^1], line, StringComparison.Ordinal))
            return;
        history.Add(line);
        while (history.Count > ConsoleHistoryCap)
            history.RemoveAt(0);
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
    }

    private void SendConsoleCommand(LaunchRole role, TextBox? box)
    {
        if (box is null)
            return;
        var line = box.Text.Trim();
        if (line.Length == 0)
            return;

        var tap = role == LaunchRole.Dedicated ? _consoleServer : _consoleClient;
        if (tap is null)
        {
            AppendConsoleLine(role, Loc.Get("console_not_connected"));
            return;
        }

        if (tap.Role != role)
        {
            AppendConsoleLine(role, Loc.Get("console_not_delivered"));
            return;
        }

        if (!tap.TryWriteCommand(line))
        {
            AppendConsoleLine(role, Loc.Get("console_not_delivered"));
            return;
        }

        AppendConsoleLine(role, "] " + line);
        box.Clear();
    }

    private void DropHostedConsoles()
    {
        _consoleServer?.Dispose();
        _consoleServer = null;
        _consoleClient?.Dispose();
        _consoleClient = null;
        ApplyConsoleView();
    }

    private void ClearConsoleBox(RichTextBox? box, ConcurrentQueue<string> queue)
    {
        while (queue.TryDequeue(out _))
        {
        }

        ResetConsolePane(box);
        _consoleBreaks.Remove(box!);
        ResetConsoleFind(box);
        if (ReferenceEquals(box, TxtConsoleServer))
            _followServer = true;
        else if (ReferenceEquals(box, TxtConsoleClient))
            _followClient = true;
    }

    /// <summary>Queued, not written: the timer owns every document edit.</summary>
    private void AppendConsoleLine(LaunchRole role, string line)
    {
        var queue = role == LaunchRole.Dedicated ? _pendingServer : _pendingClient;
        queue.Enqueue(line);
        if (_consoleTimer is { IsEnabled: false })
            _consoleTimer.Start();
    }

    private static Dictionary<string, string> MergeEnv(
        IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string>? b)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (a is not null)
        {
            foreach (var kv in a)
                d[kv.Key] = kv.Value;
        }

        if (b is not null)
        {
            foreach (var kv in b)
                d[kv.Key] = kv.Value;
        }

        return d;
    }
}
