using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    /// <summary>
    /// One console pane's find bar. Matches are TextRanges into the live
    /// document, rebuilt whenever the pane's revision moves.
    /// </summary>
    private sealed class ConsoleFindState
    {
        public RichTextBox Box = null!;
        public FrameworkElement Bar = null!;
        public TextBox Input = null!;
        public TextBlock Counter = null!;
        public CheckBox CaseToggle = null!;
        public bool Open;
        public int Rev = -1;
        public bool Capped;
        public readonly List<TextRange> Matches = new();
        public int Current = -1;
        public TextRange? Paint;
    }

    private const int ConsoleFindMaxMatches = 10000;

    /// <summary>
    /// The match is painted into the document, not selected: a read-only pane
    /// that never holds focus does not render its selection reliably while the
    /// drain timer keeps editing the document underneath it.
    /// </summary>
    private static readonly Brush s_findPaint = Freeze(Color.FromRgb(0x7A, 0x53, 0x00));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private readonly List<ConsoleFindState> _consoleFind = new();
    private readonly Dictionary<RichTextBox, int> _consoleRev = new();
    private ConsoleFindState? _findTarget;
    private ConsoleFindState? _findPendingQuery;
    private DispatcherTimer? _findDebounce;
    private int _findTick;

    private void InitConsoleFind()
    {
        if (_consoleFind.Count > 0)
            return;

        AddConsoleFind(TxtConsoleServer, BarFindServer, TxtFindServer,
            TxtFindServerCount, ChkFindServerCase, TxtConsoleServerCmd);
        AddConsoleFind(TxtConsoleClient, BarFindClient, TxtFindClient,
            TxtFindClientCount, ChkFindClientCase, TxtConsoleClientCmd);
        _findTarget = _consoleFind.FirstOrDefault();

        _findDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce!.Stop();
            var st = _findPendingQuery;
            _findPendingQuery = null;
            if (st is not null)
                RebuildConsoleFind(st, jump: true);
        };
    }

    private void AddConsoleFind(
        RichTextBox? box, FrameworkElement? bar, TextBox? input,
        TextBlock? counter, CheckBox? caseToggle, TextBox? cmd)
    {
        if (box is null || bar is null || input is null || counter is null || caseToggle is null)
            return;

        var st = new ConsoleFindState
        {
            Box = box,
            Bar = bar,
            Input = input,
            Counter = counter,
            CaseToggle = caseToggle,
        };
        _consoleFind.Add(st);

        box.GotKeyboardFocus += (_, _) => _findTarget = st;
        box.PreviewMouseDown += (_, _) => _findTarget = st;
        input.GotKeyboardFocus += (_, _) => _findTarget = st;
        if (cmd is not null)
            cmd.GotKeyboardFocus += (_, _) => _findTarget = st;
    }

    /// <summary>Every find control carries its own pane in Tag.</summary>
    private ConsoleFindState? FindStateOf(object sender) =>
        sender is FrameworkElement fe && fe.Tag is RichTextBox box
            ? _consoleFind.FirstOrDefault(s => ReferenceEquals(s.Box, box))
            : null;

    private ConsoleFindState? ActiveConsoleFind() =>
        _findTarget is { Open: true } ? _findTarget : _consoleFind.FirstOrDefault(s => s.Open);

    private bool ConsoleFindHolds(RichTextBox box) =>
        _consoleFind.Any(s => s.Open && ReferenceEquals(s.Box, box));

    private int ConsoleRev(RichTextBox box) =>
        _consoleRev.TryGetValue(box, out var v) ? v : 0;

    private void BumpConsoleRev(RichTextBox box) =>
        _consoleRev[box] = ConsoleRev(box) + 1;

    /// <summary>A cleared pane gets a new document, so old pointers are dead.</summary>
    private void ResetConsoleFind(RichTextBox? box)
    {
        if (box is null)
            return;

        foreach (var st in _consoleFind)
        {
            if (!ReferenceEquals(st.Box, box))
                continue;
            st.Paint = null;
            st.Matches.Clear();
            st.Current = -1;
            st.Rev = -1;
            st.Capped = false;
            UpdateConsoleFindCounter(st);
        }

        BumpConsoleRev(box);
    }

    /// <summary>Keeps an open bar's count honest while the log streams.</summary>
    private void ConsoleFindTick()
    {
        if (++_findTick % 4 != 0)
            return;

        foreach (var st in _consoleFind)
        {
            if (st.Open && st.Rev != ConsoleRev(st.Box))
                RebuildConsoleFind(st, jump: false);
        }
    }

    private void OnConsoleFind(object sender, RoutedEventArgs e) => OpenConsoleFind(null);

    private void OpenConsoleFind(ConsoleFindState? st)
    {
        st ??= _findTarget ?? _consoleFind.FirstOrDefault();
        if (st is null)
            return;

        _findTarget = st;
        st.Open = true;
        st.Bar.Visibility = Visibility.Visible;

        var seed = SelectedConsoleWord(st.Box);
        if (seed.Length > 0)
            st.Input.Text = seed;

        st.Input.Focus();
        st.Input.SelectAll();
        RebuildConsoleFind(st, jump: true);
    }

    private static string SelectedConsoleWord(RichTextBox box)
    {
        var text = box.Selection.Text;
        if (text.Length is 0 or > 120)
            return string.Empty;
        return text.Contains('\n') || text.Contains('\r') ? string.Empty : text;
    }

    private void CloseConsoleFind(ConsoleFindState st)
    {
        ClearMatchPaint(st);
        st.Open = false;
        st.Bar.Visibility = Visibility.Collapsed;
        st.Matches.Clear();
        st.Current = -1;
        st.Rev = -1;
        st.Capped = false;
        st.Counter.Text = string.Empty;
        st.Box.Focus();
    }

    private void OnConsoleFindClose(object sender, RoutedEventArgs e)
    {
        if (FindStateOf(sender) is { } st)
            CloseConsoleFind(st);
    }

    private void OnConsoleFindPrev(object sender, RoutedEventArgs e)
    {
        if (FindStateOf(sender) is { } st)
            StepConsoleFind(st, -1);
    }

    private void OnConsoleFindNext(object sender, RoutedEventArgs e)
    {
        if (FindStateOf(sender) is { } st)
            StepConsoleFind(st, 1);
    }

    private void OnConsoleFindCaseChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        if (FindStateOf(sender) is { } st && st.Open)
            RebuildConsoleFind(st, jump: true);
    }

    private void OnConsoleFindTextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindStateOf(sender) is not { } st)
            return;

        if (_findPendingQuery is not null && !ReferenceEquals(_findPendingQuery, st))
            RebuildConsoleFind(_findPendingQuery, jump: true);

        _findPendingQuery = st;
        _findDebounce?.Stop();
        _findDebounce?.Start();
    }

    private void OnConsoleFindKeyDown(object sender, KeyEventArgs e)
    {
        if (FindStateOf(sender) is not { } st)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                FlushConsoleFindQuery(st);
                StepConsoleFind(st, Shifted() ? -1 : 1);
                e.Handled = true;
                break;

            case Key.Escape:
                CloseConsoleFind(st);
                e.Handled = true;
                break;
        }
    }

    private static bool Shifted() => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private void FlushConsoleFindQuery(ConsoleFindState st)
    {
        if (!ReferenceEquals(_findPendingQuery, st))
            return;
        _findPendingQuery = null;
        _findDebounce?.Stop();
        RebuildConsoleFind(st, jump: false);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _simpleTab == SimpleTab.Leaderboards)
        {
            if (HandleLeaderboardEscape())
            {
                e.Handled = true;
                return;
            }
        }

        if (_simpleTab != SimpleTab.Console || _consoleFind.Count == 0)
            return;

        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            OpenConsoleFind(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            if (ActiveConsoleFind() is { } open)
            {
                StepConsoleFind(open, Shifted() ? -1 : 1);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Escape && ActiveConsoleFind() is { } closing)
        {
            CloseConsoleFind(closing);
            e.Handled = true;
        }
    }

    private void StepConsoleFind(ConsoleFindState st, int delta)
    {
        if (!st.Open)
            return;
        if (st.Rev != ConsoleRev(st.Box))
            RebuildConsoleFind(st, jump: false);

        if (st.Matches.Count == 0)
        {
            UpdateConsoleFindCounter(st);
            return;
        }

        var n = st.Matches.Count;
        st.Current = st.Current < 0
            ? (delta >= 0 ? 0 : n - 1)
            : (((st.Current + delta) % n) + n) % n;

        ShowConsoleMatch(st);
        UpdateConsoleFindCounter(st);
    }

    private void RebuildConsoleFind(ConsoleFindState st, bool jump)
    {
        var anchor = CurrentMatchStart(st) ?? ViewportAnchor(st.Box);

        ClearMatchPaint(st);
        st.Matches.Clear();
        st.Current = -1;
        st.Capped = false;
        st.Rev = ConsoleRev(st.Box);

        var query = st.Input.Text;
        if (query.Length > 0)
            st.Capped = CollectConsoleMatches(
                st.Box, query, st.CaseToggle.IsChecked == true, st.Matches);

        if (st.Matches.Count > 0)
        {
            st.Current = MatchIndexAtOrAfter(st.Matches, anchor);
            ShowConsoleMatch(st, scroll: jump);
        }

        UpdateConsoleFindCounter(st);
    }

    private static void ClearMatchPaint(ConsoleFindState st)
    {
        if (st.Paint is null)
            return;
        try
        {
            st.Paint.ApplyPropertyValue(TextElement.BackgroundProperty, null);
        }
        catch
        {
        }

        st.Paint = null;
    }

    private static TextPointer? CurrentMatchStart(ConsoleFindState st) =>
        st.Current >= 0 && st.Current < st.Matches.Count ? st.Matches[st.Current].Start : null;

    private static TextPointer ViewportAnchor(RichTextBox box)
    {
        try
        {
            return box.GetPositionFromPoint(new Point(2, 2), true) ?? box.Document.ContentStart;
        }
        catch
        {
            return box.Document.ContentStart;
        }
    }

    private static int MatchIndexAtOrAfter(List<TextRange> matches, TextPointer anchor)
    {
        try
        {
            for (var i = 0; i < matches.Count; i++)
            {
                if (matches[i].Start.CompareTo(anchor) >= 0)
                    return i;
            }
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private void ShowConsoleMatch(ConsoleFindState st, bool scroll = true)
    {
        if (st.Current < 0 || st.Current >= st.Matches.Count)
            return;

        var m = st.Matches[st.Current];
        ClearMatchPaint(st);
        try
        {
            m.ApplyPropertyValue(TextElement.BackgroundProperty, s_findPaint);
            st.Paint = m;
        }
        catch
        {
            return;
        }

        if (scroll)
            ScrollMatchIntoView(st.Box, m.Start);
    }

    /// <summary>
    /// GetCharacterRect is viewport-relative, so each scroll changes the answer;
    /// the loop settles once the match sits a third of the way down.
    /// </summary>
    private static void ScrollMatchIntoView(RichTextBox box, TextPointer p)
    {
        for (var i = 0; i < 3; i++)
        {
            box.UpdateLayout();
            if (box.ViewportHeight <= 0)
                return;

            var r = p.GetCharacterRect(LogicalDirection.Forward);
            if (r.IsEmpty)
                return;

            var max = Math.Max(0, box.ExtentHeight - box.ViewportHeight);
            var target = Math.Clamp(
                box.VerticalOffset + r.Top - (box.ViewportHeight / 3), 0, max);
            if (Math.Abs(target - box.VerticalOffset) < 1)
                break;

            box.ScrollToVerticalOffset(target);
        }

        var rect = p.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty || box.ViewportWidth <= 0)
            return;
        if (rect.Left >= 0 && rect.Left <= box.ViewportWidth)
            return;

        var wanted = Math.Max(0, box.HorizontalOffset + rect.Left - (box.ViewportWidth / 3));
        box.ScrollToHorizontalOffset(
            Math.Min(wanted, Math.Max(0, box.ExtentWidth - box.ViewportWidth)));
    }

    private void UpdateConsoleFindCounter(ConsoleFindState st)
    {
        if (st.Input.Text.Length == 0)
        {
            st.Counter.Text = string.Empty;
            return;
        }

        if (st.Matches.Count == 0)
        {
            st.Counter.Text = Loc.Get("find_none");
            return;
        }

        var total = st.Matches.Count + (st.Capped ? "+" : string.Empty);
        st.Counter.Text = $"{st.Current + 1}/{total}";
    }

    /// <summary>
    /// Flattens the pane into one buffer with a newline at every line break, so
    /// a match may cross a colour run but never a line. True when the match cap
    /// cut the walk short.
    /// </summary>
    private static bool CollectConsoleMatches(
        RichTextBox box, string query, bool matchCase, List<TextRange> into)
    {
        if (box.Document.Blocks.FirstBlock is not Paragraph body)
            return false;

        var text = new StringBuilder();
        var segStart = new List<TextPointer>();
        var segOffset = new List<int>();
        var segLength = new List<int>();

        var end = body.ContentEnd;
        var p = body.ContentStart;
        while (p is not null && p.CompareTo(end) < 0)
        {
            var ctx = p.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                var run = p.GetTextInRun(LogicalDirection.Forward);
                if (run.Length > 0)
                {
                    segStart.Add(p);
                    segOffset.Add(text.Length);
                    segLength.Add(run.Length);
                    text.Append(run);
                    p = p.GetPositionAtOffset(run.Length) ?? end;
                    continue;
                }
            }
            else if (ctx == TextPointerContext.ElementStart
                     && p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak)
            {
                text.Append('\n');
            }

            p = p.GetNextContextPosition(LogicalDirection.Forward);
        }

        var hay = text.ToString();
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var at = 0;
        while (at <= hay.Length - query.Length)
        {
            var hit = hay.IndexOf(query, at, cmp);
            if (hit < 0)
                break;

            var s = PointerAt(segStart, segOffset, segLength, hit, LogicalDirection.Forward);
            var e = PointerAt(segStart, segOffset, segLength,
                hit + query.Length, LogicalDirection.Backward);
            if (s is not null && e is not null)
                into.Add(new TextRange(s, e));

            at = hit + query.Length;
            if (into.Count >= ConsoleFindMaxMatches)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Backward gravity on the end pointer keeps a tail match from swallowing
    /// the next line the drain timer appends at that exact position.
    /// </summary>
    private static TextPointer? PointerAt(
        List<TextPointer> starts, List<int> offsets, List<int> lengths,
        int offset, LogicalDirection gravity)
    {
        var lo = 0;
        var hi = offsets.Count - 1;
        var seg = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (offsets[mid] <= offset)
            {
                seg = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (seg < 0)
            return null;

        var delta = offset - offsets[seg];
        return delta > lengths[seg] ? null : starts[seg].GetPositionAtOffset(delta, gravity);
    }
}
