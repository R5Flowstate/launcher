using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace R5Flowstate.Shell;

public sealed class ReplayEvent
{
    public string Type { get; set; } = "";
    public double T { get; set; }
}

/// <summary>What the list shows for one replay file, read once and cached.</summary>
public sealed class ReplaySummary
{
    public string Mode { get; set; } = "";
    public string ModeTitle { get; set; } = "";
    public string Map { get; set; } = "";
    public string Player { get; set; } = "";
    public ulong StartMs { get; set; }
    public int Seconds { get; set; }
    public double ClipStart { get; set; } = -1.0;
    public List<ReplayEvent> Events { get; set; } = new();
}

public sealed class ReplayChapter
{
    public double Start { get; init; }
    public double End { get; init; }
    public int Result { get; init; }   // 1 won, -1 lost, 0 unfinished
    public bool IsRound { get; init; }
    public int Number { get; init; }
}

/// <summary>
/// Reads the .r5dem chunk stream: META for mode / map / player / clip start,
/// EVENT chunks for the moments, PACKET wall clocks for the length. Moment
/// times use the same wall clock as the length, so they line up with it.
/// </summary>
public static class ReplayReader
{
    const int HeaderSize = 128;
    const int ChunkHeaderSize = 24;
    const byte ChunkPacket = 0x02;
    const byte ChunkMeta = 0x05;
    const byte ChunkEvent = 0x07;
    const byte KindPrelude = 0x02;
    const int MaxChunk = 16 * 1024 * 1024;
    const int MaxEvents = 5000;

    static readonly Regex s_povName = new("\"name\"\\s*:\\s*\"([^\"]{1,64})\"", RegexOptions.Compiled);
    static readonly Regex s_mode = new("\"mode\"\\s*:\\s*\"([A-Za-z0-9_]{1,63})\"", RegexOptions.Compiled);
    static readonly Regex s_modeTitle = new("\"modeTitle\"\\s*:\\s*\"([^\"]{1,80})\"", RegexOptions.Compiled);
    static readonly Regex s_clipStart = new("\"clipStart\"\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)", RegexOptions.Compiled);
    static readonly Regex s_eventType = new("\"t\"\\s*:\\s*\"([A-Za-z0-9_-]{1,15})\"", RegexOptions.Compiled);

    public static ReplaySummary? Read(FileInfo file, Action<long>? onRead = null)
    {
        try
        {
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[HeaderSize];
            if (fs.Read(head, 0, head.Length) != head.Length || Encoding.ASCII.GetString(head, 0, 6) != "R5DEMO")
                return null;

            var s = new ReplaySummary
            {
                StartMs = BitConverter.ToUInt64(head, 32),
                Map = CString(head, 40, 32),
            };

            uint firstWall = uint.MaxValue, lastWall = 0;
            var packets = 0;
            var eventWalls = new List<(string type, uint wall)>();
            var ch = new byte[ChunkHeaderSize];
            while (fs.Position + ChunkHeaderSize <= fs.Length)
            {
                if (fs.Read(ch, 0, ch.Length) != ch.Length)
                    break;
                var type = ch[0];
                var kind = ch[3];
                var len = BitConverter.ToUInt32(ch, 4);
                var wall = BitConverter.ToUInt32(ch, 16);
                if (len > MaxChunk || fs.Position + len > fs.Length)
                    break;
                onRead?.Invoke(fs.Position);

                if (type == ChunkPacket && (kind & KindPrelude) == 0)
                {
                    packets++;
                    if (firstWall == uint.MaxValue)
                        firstWall = wall;
                    lastWall = wall;
                }
                if ((type == ChunkMeta || type == ChunkEvent) && len > 0 && len <= 64 * 1024)
                {
                    var payload = new byte[len];
                    if (fs.Read(payload, 0, (int)len) != len)
                        break;
                    var json = Encoding.UTF8.GetString(payload);
                    if (type == ChunkMeta)
                        ApplyMeta(s, json);
                    else if (eventWalls.Count < MaxEvents)
                    {
                        var m = s_eventType.Match(json);
                        if (m.Success)
                            eventWalls.Add((m.Groups[1].Value, wall));
                    }
                    continue;
                }
                fs.Seek(len, SeekOrigin.Current);
            }
            if (packets == 0)
                return null;

            s.Seconds = lastWall >= firstWall ? (int)((lastWall - firstWall) / 1000) : 0;
            foreach (var (t, w) in eventWalls)
                s.Events.Add(new ReplayEvent { Type = t, T = w > firstWall ? (w - firstWall) / 1000.0 : 0.0 });
            return s;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    static void ApplyMeta(ReplaySummary s, string json)
    {
        var m = s_povName.Match(json);
        if (m.Success)
            s.Player = m.Groups[1].Value;
        m = s_mode.Match(json);
        if (m.Success)
            s.Mode = m.Groups[1].Value;
        m = s_modeTitle.Match(json);
        if (m.Success)
            s.ModeTitle = m.Groups[1].Value;
        m = s_clipStart.Match(json);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cs))
            s.ClipStart = cs;
    }

    static string CString(byte[] buf, int offset, int max)
    {
        var end = offset;
        while (end < offset + max && buf[end] != 0)
            end++;
        return Encoding.ASCII.GetString(buf, offset, end - offset);
    }

    /// <summary>Bookmark times from the replay's .marks file (one number per line).</summary>
    public static List<double> ReadBookmarks(string replayPath)
    {
        var list = new List<double>();
        try
        {
            var path = replayPath + ".marks";
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024)
                return list;
            foreach (var line in File.ReadAllLines(path))
            {
                if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                    && t >= 0 && t < 86400 && list.Count < 500)
                    list.Add(t);
            }
            list.Sort();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return list;
    }

    /// <summary>Fights / rounds from the moment stream, the same rules the replay bar uses.</summary>
    public static List<ReplayChapter> Chapters(ReplaySummary s)
    {
        var list = new List<ReplayChapter>();
        double? openStart = null;
        var openRound = false;
        var n = 0;
        foreach (var e in s.Events.OrderBy(e => e.T))
        {
            switch (e.Type)
            {
                case "fight":
                case "round":
                case "round_start":
                    if (openStart is double prev)
                        list.Add(new ReplayChapter { Start = prev, End = e.T, IsRound = openRound, Number = n });
                    n++;
                    openStart = e.T;
                    openRound = e.Type != "fight";
                    break;
                case "fight_won":
                case "fight_lost":
                case "round_won":
                case "round_lost":
                case "round_end":
                    if (openStart is double start)
                    {
                        list.Add(new ReplayChapter
                        {
                            Start = start, End = e.T, IsRound = openRound, Number = n,
                            Result = e.Type.EndsWith("_won", StringComparison.Ordinal) ? 1
                                : e.Type.EndsWith("_lost", StringComparison.Ordinal) ? -1 : 0,
                        });
                        openStart = null;
                    }
                    break;
            }
        }
        if (openStart is double last)
            list.Add(new ReplayChapter { Start = last, End = Math.Max(last + 1, s.Seconds), IsRound = openRound, Number = n });
        return list;
    }
}

/// <summary>
/// Summaries keyed by path + size + write time, so an unchanged replay is never
/// walked twice and the list opens at once.
/// </summary>
public sealed class ReplayIndexCache
{
    sealed class Entry
    {
        public long Size { get; set; }
        public long WriteTicks { get; set; }
        public ReplaySummary Summary { get; set; } = new();
    }

    static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "R5Flowstate", "replay_index.json");

    Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    bool _dirty;

    public static ReplayIndexCache Load()
    {
        var c = new ReplayIndexCache();
        try
        {
            if (File.Exists(CachePath) && new FileInfo(CachePath).Length < 32 * 1024 * 1024)
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath), s_json);
                if (d is not null)
                    c._entries = new Dictionary<string, Entry>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            c._entries.Clear();
        }
        return c;
    }

    public bool TryGet(FileInfo f, out ReplaySummary summary)
    {
        summary = null!;
        if (!_entries.TryGetValue(f.FullName, out var e) || e.Size != f.Length || e.WriteTicks != f.LastWriteTimeUtc.Ticks)
            return false;
        summary = e.Summary;
        return true;
    }

    public void Put(FileInfo f, ReplaySummary summary)
    {
        _entries[f.FullName] = new Entry { Size = f.Length, WriteTicks = f.LastWriteTimeUtc.Ticks, Summary = summary };
        _dirty = true;
    }

    /// <summary>Drops entries for files that no longer exist, then writes if anything changed.</summary>
    public void Save(IEnumerable<string> livePaths)
    {
        var live = new HashSet<string>(livePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var k in _entries.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _entries.Remove(k);
            _dirty = true;
        }
        if (!_dirty)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, s_json));
            File.Move(tmp, CachePath, overwrite: true);
            _dirty = false;
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>Pinned replay names, in the file the game's prune step reads.</summary>
public static class ReplayPins
{
    static readonly Regex s_name = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    static string PathIn(string dir) => Path.Combine(dir, "pinned.txt");

    public static HashSet<string> Load(string dir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var p = PathIn(dir);
            if (File.Exists(p) && new FileInfo(p).Length < 64 * 1024)
            {
                foreach (var line in File.ReadAllLines(p))
                {
                    var n = line.Trim();
                    if (s_name.IsMatch(n))
                        set.Add(n);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return set;
    }

    public static bool Save(string dir, HashSet<string> names)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(PathIn(dir), names.Where(n => s_name.IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Recording options; they reach the game as client launch args.</summary>
public sealed class ReplaySettings
{
    public const int DefaultKeepDays = 14;
    public const int DefaultMaxGb = 4;

    public bool AutoRecord { get; set; } = true;
    public int KeepDays { get; set; } = DefaultKeepDays;
    public int MaxGb { get; set; } = DefaultMaxGb;

    static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "R5Flowstate", "replay_settings.json");

    public static ReplaySettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var s = JsonSerializer.Deserialize<ReplaySettings>(File.ReadAllText(StorePath));
                if (s is not null)
                {
                    s.KeepDays = Math.Clamp(s.KeepDays, 0, 3650);
                    s.MaxGb = Math.Clamp(s.MaxGb, 1, 500);
                    return s;
                }
            }
        }
        catch (Exception)
        {
        }
        return new ReplaySettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Only what differs from the game's own defaults.</summary>
    public IReadOnlyList<string> ClientTokens()
    {
        var t = new List<string>();
        if (!AutoRecord)
            t.AddRange(new[] { "+demo_auto_record", "0" });
        if (KeepDays != DefaultKeepDays)
            t.AddRange(new[] { "+demo_keep_days", KeepDays.ToString(CultureInfo.InvariantCulture) });
        if (MaxGb != DefaultMaxGb)
            t.AddRange(new[] { "+demo_max_mb", (MaxGb * 1024).ToString(CultureInfo.InvariantCulture) });
        return t;
    }
}
