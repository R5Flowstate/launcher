using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

public sealed class HostPreset
{
    public string Name { get; set; } = string.Empty;
    public string Playlist { get; set; } = string.Empty;
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>
/// Host settings the player picked per playlist, plus named presets. Stored as
/// JSON next to the launcher log; presets also travel as .r5fpreset files.
/// </summary>
public sealed class HostSettingsStore
{
    public const string PresetExtension = ".r5fpreset";
    const int PresetFileFormat = 1;
    const long MaxPresetFileBytes = 64 * 1024;
    const int MaxPresetNameLength = 48;

    static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "R5Flowstate", "host_settings.json");

    sealed class StoreFile
    {
        public Dictionary<string, Dictionary<string, string>> Current { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<HostPreset> Presets { get; set; } = new();
    }

    sealed class PresetFile
    {
        public int Format { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Playlist { get; set; } = string.Empty;
        public Dictionary<string, string> Values { get; set; } = new();
    }

    StoreFile _data = new();

    public static Action<string>? Logger { get; set; }

    public static HostSettingsStore Load()
    {
        var store = new HostSettingsStore();
        try
        {
            if (File.Exists(StorePath))
            {
                var data = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(StorePath), s_json);
                if (data is not null)
                {
                    store._data = new StoreFile
                    {
                        Current = new Dictionary<string, Dictionary<string, string>>(
                            data.Current ?? new(), StringComparer.OrdinalIgnoreCase),
                        Presets = data.Presets ?? new(),
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Logger?.Invoke("host settings: store unreadable, starting empty: " + ex.Message);
        }
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, s_json));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger?.Invoke("host settings: save failed: " + ex.Message);
        }
    }

    /// <summary>The values in effect for a playlist: stored choices over the playlist defaults.</summary>
    public Dictionary<string, string> ValuesFor(string playlist, IReadOnlyList<PlaylistSetting> settings)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _data.Current.TryGetValue(playlist, out var stored);
        foreach (var s in settings)
        {
            if (stored is not null && stored.TryGetValue(s.Var, out var v) && s.Validate(v, out _))
                values[s.Var] = v;
            else
                values[s.Var] = s.Default;
        }
        return values;
    }

    public void SetValues(string playlist, IReadOnlyList<PlaylistSetting> settings, IReadOnlyDictionary<string, string> values)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in settings)
        {
            if (values.TryGetValue(s.Var, out var v) && !s.IsDefault(v) && s.Validate(v, out _))
                kept[s.Var] = v.Trim();
        }
        if (kept.Count == 0)
            _data.Current.Remove(playlist);
        else
            _data.Current[playlist] = kept;
        Save();
    }

    public IReadOnlyList<HostPreset> PresetsFor(string playlist) =>
        _data.Presets
            .Where(p => string.Equals(p.Playlist, playlist, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static string CleanPresetName(string? name)
    {
        var chars = (name ?? string.Empty).Trim()
            .Where(c => !char.IsControl(c) && Path.GetInvalidFileNameChars().Contains(c) == false)
            .Take(MaxPresetNameLength)
            .ToArray();
        return new string(chars).Trim();
    }

    public HostPreset SavePreset(string name, string playlist, IReadOnlyList<PlaylistSetting> settings,
        IReadOnlyDictionary<string, string> values)
    {
        var preset = new HostPreset { Name = CleanPresetName(name), Playlist = playlist };
        foreach (var s in settings)
        {
            if (values.TryGetValue(s.Var, out var v) && !s.IsDefault(v) && s.Validate(v, out _))
                preset.Values[s.Var] = v.Trim();
        }
        _data.Presets.RemoveAll(p =>
            string.Equals(p.Playlist, playlist, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Name, preset.Name, StringComparison.CurrentCultureIgnoreCase));
        _data.Presets.Add(preset);
        Save();
        return preset;
    }

    public void DeletePreset(HostPreset preset)
    {
        _data.Presets.Remove(preset);
        Save();
    }

    public static void ExportPreset(string path, HostPreset preset)
    {
        var file = new PresetFile
        {
            Format = PresetFileFormat,
            Name = preset.Name,
            Playlist = preset.Playlist,
            Values = new Dictionary<string, string>(preset.Values),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, s_json));
    }

    /// <summary>
    /// Reads a shared preset. Only vars the playlist declares, with values its
    /// declarations accept, survive; everything else in the file is dropped.
    /// </summary>
    public static HostPreset? ImportPreset(string path, Func<string, IReadOnlyList<PlaylistSetting>?> settingsFor,
        out string error)
    {
        error = string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxPresetFileBytes)
            {
                error = "not a preset file";
                return null;
            }
            var file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(path), s_json);
            if (file is null || file.Format != PresetFileFormat || string.IsNullOrWhiteSpace(file.Playlist))
            {
                error = "not a preset file";
                return null;
            }
            var settings = settingsFor(file.Playlist.Trim());
            if (settings is null || settings.Count == 0)
            {
                error = "this preset is for a mode you do not have: " + file.Playlist;
                return null;
            }
            var preset = new HostPreset
            {
                Name = CleanPresetName(string.IsNullOrWhiteSpace(file.Name)
                    ? Path.GetFileNameWithoutExtension(path)
                    : file.Name),
                Playlist = file.Playlist.Trim(),
            };
            foreach (var s in settings)
            {
                if (file.Values is not null && file.Values.TryGetValue(s.Var, out var v) && s.Validate(v, out _))
                    preset.Values[s.Var] = v.Trim();
            }
            return preset;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public HostPreset AddImported(HostPreset preset, IReadOnlyList<PlaylistSetting> settings)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in settings)
            values[s.Var] = preset.Values.TryGetValue(s.Var, out var v) ? v : s.Default;
        return SavePreset(preset.Name, preset.Playlist, settings, values);
    }
}
