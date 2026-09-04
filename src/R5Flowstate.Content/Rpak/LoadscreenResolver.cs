using System.Runtime;

namespace R5Flowstate.Content.Rpak;

public sealed class LoadscreenPixels
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Bgra { get; init; }
    public required string SourcePath { get; init; }
    public int SourceWidth { get; init; }
}

/// <summary>Find and decode &lt;map&gt;_loadscreen.rpak for a map stem.</summary>
public static class LoadscreenResolver
{
    private static readonly string[] s_dropSuffixes =
    {
        "_mu1", "_mu2", "_mu3", "_mu4",
        "_hu", "_night", "_holiday", "_staging",
    };

    /// <summary>
    /// Below this the decode produced a streaming placeholder, not the picture.
    /// Loadscreens ship at 1080p; the inline mip of a starpak-backed asset is
    /// tens of pixels wide.
    /// </summary>
    public const int MinUsableWidth = 640;

    /// <summary>
    /// Hero pane is ~1000px at the default window. Full 1080p BGRA is never shown 1:1.
    /// </summary>
    public const int DisplayMaxWidth = 960;

    public static string? FindPak(string installRoot, string mapStem) =>
        CandidatePaks(installRoot, mapStem).FirstOrDefault();

    /// <summary>
    /// Ordered best-first: the map's own pak, then a shipped art loadscreen naming
    /// the same location, then the parent map's pak. The parent is last because a
    /// map update keeps its predecessor's pak, and some of those hold greybox art.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaks(string installRoot, string mapStem)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(mapStem))
            return Array.Empty<string>();

        var dir = Path.Combine(installRoot, "paks", "Win64");
        if (!Directory.Exists(dir))
            return Array.Empty<string>();

        var stems = CandidateStems(mapStem.Trim()).ToList();
        var ordered = new List<string>();

        void Add(string path)
        {
            if (File.Exists(path) && !ordered.Contains(path, StringComparer.OrdinalIgnoreCase))
                ordered.Add(path);
        }

        if (stems.Count > 0)
            Add(Path.Combine(dir, stems[0] + "_loadscreen.rpak"));

        var artPaks = ListArtPaks(installRoot);
        foreach (var token in stems.Select(LocationToken).Where(t => t.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var art in artPaks)
            {
                if (Path.GetFileNameWithoutExtension(art)
                        .Contains(token, StringComparison.OrdinalIgnoreCase))
                    Add(art);
            }
        }

        foreach (var stem in stems.Skip(1))
            Add(Path.Combine(dir, stem + "_loadscreen.rpak"));

        return ordered;
    }

    /// <summary>"mp_rr_divided_moon_mu1" -&gt; "divided_moon_mu1".</summary>
    private static string LocationToken(string stem)
    {
        var s = stem.Trim();
        if (s.StartsWith("mp_rr_", StringComparison.OrdinalIgnoreCase))
            s = s[6..];
        else if (s.StartsWith("mp_", StringComparison.OrdinalIgnoreCase))
            s = s[3..];
        return s;
    }

    public static void PrepareInstall(string installRoot)
    {
        OodleNative.EnsureLoaded();
    }

    /// <summary>
    /// Standalone art loadscreens (battlepass, quest, community, lore). Named
    /// "loadscreen_*.rpak", so the "&lt;map&gt;_loadscreen.rpak" set is excluded by
    /// the prefix alone. Some ship as sub-4 KB stubs with no image.
    /// </summary>
    private static readonly object s_artGate = new();
    private static string? s_artRoot;
    private static IReadOnlyList<string>? s_artPaks;

    public static IReadOnlyList<string> ListArtPaks(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return Array.Empty<string>();

        lock (s_artGate)
        {
            if (s_artPaks is not null &&
                string.Equals(s_artRoot, installRoot, StringComparison.OrdinalIgnoreCase))
                return s_artPaks;
        }

        var dir = Path.Combine(installRoot, "paks", "Win64");
        if (!Directory.Exists(dir))
            return Array.Empty<string>();

        IReadOnlyList<string> list;
        try
        {
            list = Directory.EnumerateFiles(dir, "loadscreen_*.rpak")
                .Where(p => new FileInfo(p).Length >= 4096)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }

        lock (s_artGate)
        {
            s_artRoot = installRoot;
            s_artPaks = list;
        }
        return list;
    }

    public static bool TryDecode(
        string installRoot,
        string mapStem,
        out LoadscreenPixels? pixels,
        out string? error,
        int maxWidth = DisplayMaxWidth)
    {
        pixels = null;
        error = null;
        PrepareInstall(installRoot);

        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(mapStem))
        {
            error = "no loadscreen pak";
            return false;
        }

        var dir = Path.Combine(installRoot, "paks", "Win64");
        if (!Directory.Exists(dir))
        {
            error = "no loadscreen pak";
            return false;
        }

        var stems = CandidateStems(mapStem.Trim()).ToList();
        if (stems.Count == 0)
        {
            error = "no loadscreen pak";
            return false;
        }

        string? firstError = null;
        LoadscreenPixels? found = null;

        bool Take(string path)
        {
            if (!TryDecodePak(path, out var got, out var why, maxWidth))
            {
                firstError ??= RpakFile.DescribeError(path, why);
                return false;
            }

            var srcW = got?.SourceWidth > 0 ? got.SourceWidth : got?.Width ?? 0;
            if (got is null || srcW < MinUsableWidth)
            {
                firstError ??= RpakFile.DescribeError(path, $"placeholder {srcW}px");
                return false;
            }

            found = got;
            return true;
        }

        var own = Path.Combine(dir, stems[0] + "_loadscreen.rpak");
        if (File.Exists(own) && Take(own))
        {
            pixels = found;
            return true;
        }

        var tokens = stems
            .Select(LocationToken)
            .Where(t => t.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tokens.Count > 0)
        {
            foreach (var art in ListArtPaks(installRoot))
            {
                var name = Path.GetFileNameWithoutExtension(art);
                var hit = false;
                foreach (var token in tokens)
                {
                    if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit && Take(art))
                {
                    pixels = found;
                    return true;
                }
            }
        }

        foreach (var stem in stems.Skip(1))
        {
            var path = Path.Combine(dir, stem + "_loadscreen.rpak");
            if (File.Exists(path) && Take(path))
            {
                pixels = found;
                return true;
            }
        }

        error = firstError;
        return false;
    }

    public static bool TryDecodePak(
        string pakPath,
        out LoadscreenPixels? pixels,
        out string? error,
        int maxWidth = DisplayMaxWidth)
    {
        pixels = null;
        error = null;

        if (string.IsNullOrWhiteSpace(pakPath) || !File.Exists(pakPath))
        {
            error = "no loadscreen pak";
            return false;
        }

        if (!RpakFile.TryExtractUiia(pakPath, out var asset, out error))
            return false;

        if (!UiiaDecoder.TryDecode(asset, maxWidth, out var w, out var h, out var srcW, out var bgra, out error))
            return false;

        pixels = new LoadscreenPixels
        {
            Width = w,
            Height = h,
            Bgra = bgra,
            SourcePath = pakPath,
            SourceWidth = srcW,
        };
        return true;
    }

    public static void TrimDecodeHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    public static IEnumerable<string> CandidateStems(string stem)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(stem))
            yield return stem;

        var walk = stem;
        var safety = 0;
        while (safety++ < 8)
        {
            var dropped = false;
            foreach (var suf in s_dropSuffixes)
            {
                if (walk.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                {
                    walk = walk[..^suf.Length];
                    dropped = true;
                    break;
                }
            }
            if (!dropped)
                break;
            if (seen.Add(walk))
                yield return walk;
        }
    }
}
