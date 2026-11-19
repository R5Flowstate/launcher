// Ported verbatim from src/R5Flowstate.Shell/ModeCardViewModel.cs (namespace and
// using swapped; the two map-label helpers delegate to the copy that already
// lives in Linux.Core). Windows' file is net8.0-windows only because it used to
// live beside the WPF window; nothing in it is WPF, so the whole card model --
// families, variant maps, remembered stem, pinned modes -- comes across as it
// stands. Keep it byte-identical to upstream apart from this header, the
// namespace/using lines and the two delegations, so a later diff is a short one.
//
// The three usings below are the Windows project's ImplicitUsings, spelled out:
// this project does not enable them (see the csproj), so upstream's file needs
// them named before it will compile here.

using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux.ViewModels;

/// <summary>One mode in the Simple-mode rail. Backed by a family, so the maps it
/// offers may come from one playlist or from a dozen -- picking one resolves the
/// id to launch.</summary>
public sealed class ModeCardViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private MapOption? _selectedMap;

    public ModeCardViewModel(
        string id,
        string group,
        string title,
        string blurb,
        IReadOnlyList<MapOption> maps,
        MapOption? selectedMap,
        bool mapIsPinned = false,
        string? fallbackPlaylistId = null,
        bool titleIsAuthored = false)
    {
        Id = id;
        Group = ModeGroups.Normalize(group);
        Title = titleIsAuthored ? title.Trim() : DisplayTitle(title);
        Blurb = blurb;
        Maps = maps;
        _selectedMap = selectedMap;
        MapIsPinned = mapIsPinned;
        FallbackPlaylistId = fallbackPlaylistId ?? id;
    }

    public const string LobbyPlaylistId = "lobby";
    public const string LobbyMapStem = "mp_lobby";
    // UI card id is not a +launchplaylist. Host it like every other local play.
    public const string LobbyLaunchPlaylist = "survival_dev";

    public static ModeCardViewModel FromFamily(
        ModeFamily family,
        IReadOnlyDictionary<string, string>? mapNames,
        string? rememberedStem)
    {
        var maps = family.Variants
            .Select(v => MapOption.FromVariant(v, mapNames))
            .ToList();

        MapOption? selected = null;
        if (!string.IsNullOrWhiteSpace(rememberedStem))
        {
            selected = maps.FirstOrDefault(m =>
                string.Equals(m.Stem, rememberedStem, StringComparison.OrdinalIgnoreCase));
        }
        selected ??= maps.FirstOrDefault();

        var locTitle = LocalizedTitleOrNull(family.Id);
        return new ModeCardViewModel(
            family.Id,
            family.Group,
            locTitle ?? family.Title,
            LocalizedBlurb(family.Id, family.Blurb),
            maps,
            selected,
            family.MapIsPinned,
            maps.FirstOrDefault()?.PlaylistId,
            locTitle is not null || family.TitleIsAuthored);
    }

    public static string LocalizedTitle(string id, string fallback) =>
        LocalizedTitleOrNull(id) ?? fallback;

    public static string LocalizedBlurb(string id, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(id) && Loc.TryGet("mode_blurb." + id, out var s))
            return s;
        return fallback;
    }

    public static string LocalizedMap(
        string stem,
        string? mapTitle,
        IReadOnlyDictionary<string, string>? names)
    {
        if (!string.IsNullOrWhiteSpace(mapTitle))
        {
            var slug = PlaylistCatalogLoader.SlugMapTitle(mapTitle);
            if (slug.Length > 0 && Loc.TryGet("map_title." + slug, out var byTitle))
                return byTitle;
            return mapTitle.Trim();
        }
        if (!string.IsNullOrWhiteSpace(stem) && Loc.TryGet("map_title." + stem, out var byStem))
            return byStem;
        return MapOption.PlayerName(stem, names);
    }

    static string? LocalizedTitleOrNull(string id) =>
        !string.IsNullOrWhiteSpace(id) && Loc.TryGet("mode_title." + id, out var s)
            ? s
            : null;

    /// <summary>Playlist labels come out of localization shouted (#PL_FREE_ROAM
    /// resolves to "FREE ROAM"). A title that already has lowercase is left alone,
    /// so "1v1" and hand-written names survive.</summary>
    public static string DisplayTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;
        var t = title.Trim();
        var sawLatin = false;
        foreach (var ch in t)
        {
            if (ch is >= 'a' and <= 'z')
                return t;
            if (ch is >= 'A' and <= 'Z')
                sawLatin = true;
        }
        if (!sawLatin)
            return t;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.ToLowerInvariant());
    }

    public static bool IsLobbyPlaylist(string? id) =>
        string.Equals(id, LobbyPlaylistId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Family id. The settings key and the rail's selection identity --
    /// never a +launchplaylist, because a family spans several.</summary>
    public string Id { get; }

    public string Group { get; }

    public bool IsApex => string.Equals(Group, ModeGroups.Apex, StringComparison.Ordinal);

    /// <summary>Sort key that keeps the rail's two sections contiguous.</summary>
    public int GroupRank => IsApex ? 0 : 1;

    public string GroupTitle => Loc.Get(IsApex ? "group_apex" : "group_flowstate");

    public string Title { get; }
    public string Blurb { get; }
    public IReadOnlyList<MapOption> Maps { get; }

    /// <summary>
    /// What a combo box with no item template shows, and the reason this override
    /// exists at all.
    ///
    /// <para>Upstream sets <c>DisplayMemberPath="Title"</c> on the console tab's
    /// mode combo — which is a WPF property: Avalonia 11's
    /// <c>ItemsControl</c>/<c>ComboBox</c> has no such member (it fails the XAML
    /// compile with AVLN2000, which is how this was found). Avalonia's equivalent
    /// for an untemplated item is <c>ToString()</c>, which falls back to the type
    /// name — so the port's combo showed
    /// <c>R5Flowstate.Shell.Linux.ViewModels.ModeCardViewModel</c> clipped to the
    /// box width, which is what the player reported as "that first button on the
    /// left which was defaulted at R5Flowstate.Shell".</para>
    ///
    /// <para><see cref="MapOption"/> has done exactly this for its own
    /// <c>DisplayName</c> since the port was written, so this is the codebase's own
    /// idiom rather than a new one — the mode combo was simply never given it.</para>
    /// </summary>
    public override string ToString() => Title;

    /// <summary>Used when the family has no variants left to resolve against.</summary>
    public string FallbackPlaylistId { get; }

    /// <summary>The +launchplaylist for the map currently chosen.</summary>
    public string PlaylistId => SelectedMap?.PlaylistId is { Length: > 0 } id
        ? id
        : FallbackPlaylistId;

    /// <summary>The mode declares its own map, so the card shows it as a label.</summary>
    public bool MapIsPinned { get; }

    public string FixedMapLabel => Maps.Count > 0 ? Maps[0].DisplayName : string.Empty;

    /// <summary>Rail row trailing count. A pinned mode plays one map, so it shows
    /// a dot rather than a meaningless "1".</summary>
    public string MapCountLabel => MapIsPinned || Maps.Count <= 1
        ? "·"
        : Maps.Count.ToString(CultureInfo.InvariantCulture);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public MapOption? SelectedMap
    {
        get => _selectedMap;
        set
        {
            if (ReferenceEquals(_selectedMap, value))
                return;
            if (_selectedMap is not null && value is not null &&
                string.Equals(_selectedMap.Stem, value.Stem, StringComparison.OrdinalIgnoreCase))
            {
                _selectedMap = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlaylistId));
                return;
            }
            _selectedMap = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaylistId));
        }
    }

    public string SelectedMapStem =>
        SelectedMap?.Stem ?? (Maps.Count > 0 ? Maps[0].Stem : string.Empty);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Map stem with a player-facing display label, and the playlist that
/// runs the owning mode on it.</summary>
public sealed class MapOption
{
    public MapOption(string stem, string displayName, string playlistId = "")
    {
        Stem = stem;
        DisplayName = displayName;
        PlaylistId = playlistId;
    }

    public string Stem { get; }
    public string DisplayName { get; }

    /// <summary>+launchplaylist for this mode on this map; empty for a bare stem.</summary>
    public string PlaylistId { get; }

    public override string ToString() => DisplayName;

    /// <summary>
    /// "mp_rr_desertlands_hu (World's Edge)" when the install names the stem,
    /// otherwise the stem alone. The stem stays visible either way -- it is what
    /// gets passed to +map and what a bug report needs to quote.
    /// </summary>
    public static string Label(string stem, IReadOnlyDictionary<string, string>? names)
    {
        // The body is MapLabels.Label's, in Linux.Core: the browser, the boards
        // and the console header all drew this label before the card model was
        // reachable, so that copy is the one owner. Delegating keeps a single
        // rule without changing this method's upstream surface.
        return MapLabels.Label(stem, names);
    }

    /// <summary>Player-facing name: loc string, else a prettified stem.</summary>
    public static string PlayerName(string stem, IReadOnlyDictionary<string, string>? names)
    {
        // MapLabels.PlayerName again -- same reason as Label above.
        return MapLabels.PlayerName(stem, names);
    }

    public static MapOption FromStem(string stem, IReadOnlyDictionary<string, string>? names = null) =>
        new(stem, PlayerName(stem, names));

    /// <summary>
    /// The variant's own r5f_mode_map_title wins over the map's install name:
    /// Apex names the arena for the mode ("Fragment", "Estates"), and that is
    /// what the mode's players call it.
    /// </summary>
    public static MapOption FromVariant(ModeVariant v, IReadOnlyDictionary<string, string>? names) =>
        new(v.MapStem,
            ModeCardViewModel.LocalizedMap(v.MapStem, v.MapTitle, names),
            v.PlaylistId);
}
