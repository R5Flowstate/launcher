using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux.ViewModels;

/// <summary>
/// The Patch Notes tab: the game changelog (NOTES.json, served next to the
/// channel manifest) and the launcher changelog (LAUNCHER_NOTES.json, bundled
/// because the launcher feed is not published).
///
/// Ported from MainWindow.xaml.cs (BindNotesLocal / RefreshNotesAsync /
/// ApplyNotesPage / NotesStamp / SyncNotesUnreadDot). The loader itself is
/// upstream's <see cref="NotesSource"/>, unchanged, which is why the tab shows
/// the same notes the Windows launcher shows.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _notesUnread;
    [ObservableProperty] private bool _notesShowLauncher;

    /// <summary>Whether the Notes tab is the one on screen. The unread dot is only
    /// cleared while the player is actually looking at the notes, which is why
    /// upstream passes <c>markSeen: _simpleTab == SimpleTab.Notes</c>.</summary>
    bool _notesTabActive;

    List<PatchNoteEntryVM> _notesGame = new();
    List<PatchNoteEntryVM> _notesLauncher = new();

    /// <summary>Where NotesSource keeps its last successfully fetched copy. This
    /// is the same root the channel manifest is cached under, so the notes and
    /// the manifest that names them are fetched, cached and reused together.</summary>
    static string NotesCacheDir => ChannelCacheDir;

    /// <summary>Bind the cached/bundled notes. Never touches the network, so it
    /// is safe from the constructor.</summary>
    public void BindNotesLocal()
    {
        try
        {
            _notesGame = ToNotesRows(NotesSource.LoadGameCachedOrBundled(NotesCacheDir));
            _notesLauncher = ToNotesRows(NotesSource.LoadLauncherCachedOrBundled(NotesCacheDir));
        }
        catch (Exception ex)
        {
            AppendLog("Notes load failed: " + ex.Message);
        }

        PreferLauncherNotesIfCurrentUnseen();
        ApplyNotesPage();
    }

    /// <summary>Fetch fresh notes off the UI thread and rebind on arrival.</summary>
    public async Task RefreshNotesAsync()
    {
        try
        {
            var channel = _channel;
            var channelUrl = EffectiveChannelUrl;
            var cache = NotesCacheDir;

            var (game, launcher) = await Task.Run(() =>
            {
                using var fetcher = new FileSystemFetcher(TimeSpan.FromSeconds(15));
                var g = NotesSource.LoadAsync(channel, channelUrl, fetcher, cache)
                    .GetAwaiter().GetResult();
                var l = NotesSource.LoadLauncherAsync(fetcher, cache)
                    .GetAwaiter().GetResult();
                return (g, l);
            }).ConfigureAwait(false);

            Post(() =>
            {
                _notesGame = ToNotesRows(game);
                _notesLauncher = ToNotesRows(launcher);
                PreferLauncherNotesIfCurrentUnseen();
                ApplyNotesPage();
            });
        }
        catch (Exception ex)
        {
            AppendLog("Notes refresh failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public void ShowNotesGame()
    {
        NotesShowLauncher = false;
        ApplyNotesPage();
    }

    [RelayCommand]
    public void ShowNotesLauncher()
    {
        NotesShowLauncher = true;
        ApplyNotesPage();
    }

    /// <summary>Called when the Notes tab is opened. Clears the unread dot, since
    /// the player is now reading the notes. Windows parity: the
    /// <c>markSeen: _simpleTab == SimpleTab.Notes</c> argument.</summary>
    public void EnterNotesTab()
    {
        _notesTabActive = true;
        ApplyNotesPage();
    }

    void ApplyNotesPage()
    {
        var active = NotesShowLauncher ? _notesLauncher : _notesGame;

        if (active.Count == 0)
        {
            active = new List<PatchNoteEntryVM>
            {
                new()
                {
                    Date = "",
                    Title = NotesShowLauncher ? "Launcher notes" : "Patch notes",
                    Items =
                    [
                        new PatchNoteLineVM
                        {
                            Text = NotesShowLauncher
                                ? "The launcher changelog could not be read from the bundled copy."
                                : "No patch notes cached yet — they are fetched from the update server.",
                            IsHeader = false,
                        },
                    ],
                },
            };
        }

        NotesEntries.Clear();
        foreach (var row in active)
            NotesEntries.Add(row);

        SyncNotesUnread(markSeen: _notesTabActive);
    }

    /// <summary>
    /// Both mechanisms upstream shares this one setting with: the fingerprint
    /// stamp (the unread dot) and the older "is the launcher's own changelog for
    /// this build unseen?" check that decides which page opens first.
    /// </summary>
    void PreferLauncherNotesIfCurrentUnseen()
    {
        var needle = "Launcher " + Version;
        if (_notesLauncher.Count == 0)
            return;
        if (_notesLauncher.All(e =>
                e.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0))
            return;
        if ((_s.NotesSeenStamp ?? string.Empty)
                .IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            return;
        NotesShowLauncher = true;
    }

    internal static string NotesStamp(
        IReadOnlyList<PatchNoteEntryVM> game,
        IReadOnlyList<PatchNoteEntryVM> launcher)
    {
        var sb = new StringBuilder();
        AppendNotesStamp(sb, "g", game);
        AppendNotesStamp(sb, "l", launcher);
        return sb.ToString();
    }

    /// <summary>Fingerprint of the notes currently on screen. Self-test seam.</summary>
    internal string CurrentNotesStamp() => NotesStamp(_notesGame, _notesLauncher);

    /// <summary>The stamp of the last notes the player read. Self-test seam.</summary>
    internal string NotesSeenStamp => _s.NotesSeenStamp ?? string.Empty;

    /// <summary>The unread decision, split out so it can be checked without
    /// writing the player's settings: any fingerprint that differs from the one
    /// they last read — and is not empty — leaves the dot lit.</summary>
    internal static bool IsNotesUnread(string? seenStamp, string stamp) =>
        stamp.Length > 0 && !string.Equals(seenStamp ?? string.Empty, stamp, StringComparison.Ordinal);

    static void AppendNotesStamp(
        StringBuilder sb, string tag, IReadOnlyList<PatchNoteEntryVM> rows)
    {
        foreach (var e in rows)
        {
            sb.Append(tag);
            sb.Append('\t');
            sb.Append(e.Date);
            sb.Append('\t');
            sb.Append(e.Title);
            sb.Append('\n');
        }
    }

    void SyncNotesUnread(bool markSeen)
    {
        var stamp = NotesStamp(_notesGame, _notesLauncher);
        if (markSeen && !string.Equals(_s.NotesSeenStamp, stamp, StringComparison.Ordinal))
        {
            _s.NotesSeenStamp = stamp;
            try { _s.Save(); }
            catch (Exception ex) { AppendLog("Settings save failed: " + ex.Message); }
        }

        NotesUnread = IsNotesUnread(_s.NotesSeenStamp, stamp);
    }

    static List<PatchNoteEntryVM> ToNotesRows(IEnumerable<NotesEntry> entries) =>
        entries.Select(e => new PatchNoteEntryVM
        {
            Date = string.IsNullOrWhiteSpace(e.Date) ? "—" : e.Date,
            Title = string.IsNullOrWhiteSpace(e.Title) ? "Notes" : e.Title,
            Items = (e.Items ?? []).Select(PatchNoteLineVM.Parse).ToList(),
        }).ToList();

    /// <summary>The VM may be driven from a background thread (the notes fetch),
    /// and the grid is bound to <see cref="NotesEntries"/>.</summary>
    static void Post(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Post(action);
        }
        catch
        {
            action();
        }
    }
}
