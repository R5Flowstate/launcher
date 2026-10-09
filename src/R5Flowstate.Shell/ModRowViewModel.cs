using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R5Flowstate.Content;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public abstract class ModViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed record ModBadge(string Label, bool IsWarning);

public sealed class ModFilterChip : ModViewModelBase
{
    private bool _isOn;
    private string _label;

    public ModFilterChip(string key, string label)
    {
        Key = key;
        _label = label;
    }

    public string Key { get; }

    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public bool IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }
}

static class ModArt
{
    static readonly Dictionary<string, ImageSource?> s_remote = new(StringComparer.Ordinal);

    internal static ImageSource? LoadLocal(string? path, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // Listing icons come from the Thunderstore CDN; any other host or scheme is not fetched.
    // One bitmap per URL for the session, so a catalog refresh does not download them again.
    internal static ImageSource? LoadRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsThunderstoreHost(uri))
            return null;
        if (s_remote.TryGetValue(uri.AbsoluteUri, out var cached))
            return cached;
        ImageSource? img = null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = uri;
            bmp.DecodePixelWidth = 256;
            bmp.EndInit();
            img = bmp;
        }
        catch
        {
            img = null;
        }

        if (s_remote.Count < 512)
            s_remote[uri.AbsoluteUri] = img;
        return img;
    }

    internal static bool IsThunderstoreHost(Uri uri) =>
        uri.Host.Equals("thunderstore.io", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".thunderstore.io", StringComparison.OrdinalIgnoreCase);

    internal static string Initials(string name)
    {
        var parts = (name ?? string.Empty)
            .Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var s = string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return s.Length == 0 ? "?" : s;
    }

    internal static string Ago(DateTimeOffset when)
    {
        if (when == default)
            return Loc.Get("n_a");
        var days = (int)Math.Floor((DateTimeOffset.UtcNow - when).TotalDays);
        if (days <= 0)
            return Loc.Get("mods_ago_today");
        if (days == 1)
            return Loc.Get("mods_ago_yesterday");
        if (days < 60)
            return Loc.Format("mods_ago_days", days);
        return when.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }
}

public sealed class ModRowViewModel : ModViewModelBase
{
    private bool _enabled;
    private bool _busy;
    private string _sizeText = string.Empty;
    private ModPackage? _package;
    private string _latestVersion = string.Empty;

    public ModRowViewModel(InstalledMod mod)
    {
        Mod = mod;
        FolderName = mod.FolderName;
        Id = mod.Id;
        Name = string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name;
        Author = string.IsNullOrWhiteSpace(mod.Author) ? Loc.Get("n_a") : mod.Author;
        Version = string.IsNullOrWhiteSpace(InstalledVersion) ? Loc.Get("n_a") : InstalledVersion;
        _enabled = mod.Enabled;
        IconSource = ModArt.LoadLocal(mod.IconPath, 96);
        Initials = ModArt.Initials(Name);
        IsCatalog = !string.IsNullOrEmpty(mod.ThunderstoreFullName);

        var badges = new List<ModBadge>();
        if (mod.Maps.Count > 0)
            badges.Add(new ModBadge(Loc.Get("mods_badge_map"), false));
        if (IsServerOnly(mod))
            badges.Add(new ModBadge(Loc.Get("mods_badge_server"), false));
        if (!IsCatalog)
            badges.Add(new ModBadge(Loc.Get("mods_badge_local"), false));
        if (mod.Replaces.Count > 0)
            badges.Add(new ModBadge(Loc.Get("mods_badge_replaces"), true));
        if (RealmIsUnknown(mod))
            badges.Add(new ModBadge(Loc.Get("mods_badge_realm"), true));
        if (!ModId.IsValid(mod.Id))
            badges.Add(new ModBadge(Loc.Get("mods_badge_invalid"), true));
        Badges = badges;
    }

    public InstalledMod Mod { get; }
    public string FolderName { get; }
    public string Id { get; }
    public string Name { get; }
    public string Author { get; }
    public string Version { get; }
    public ImageSource? IconSource { get; }
    public bool HasIcon => IconSource is not null;
    public string Initials { get; }
    public bool IsCatalog { get; }
    public IReadOnlyList<ModBadge> Badges { get; }

    /// <summary>The manifest version Thunderstore compares against, else the mod.vdf one.</summary>
    public string InstalledVersion =>
        string.IsNullOrWhiteSpace(Mod.ThunderstoreVersion) ? Mod.Version : Mod.ThunderstoreVersion;

    public ModPackage? Package => _package;

    public string LatestVersion => _latestVersion;

    public bool HasUpdate => ModVersion.IsNewer(_latestVersion, InstalledVersion);

    public string UpdateCaption => Loc.Format("mods_update_to", _latestVersion);

    public long UpdateBytes =>
        _package?.Versions is { Count: > 0 } v ? Math.Max(0, v[0].FileSize) : 0;

    public void SetCatalog(ModPackage? package)
    {
        _package = package;
        _latestVersion = package?.Versions is { Count: > 0 } v ? v[0].VersionNumber : string.Empty;
        OnPropertyChanged(nameof(Package));
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateCaption));
    }

    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (Set(ref _sizeText, value ?? string.Empty))
                OnPropertyChanged(nameof(Detail));
        }
    }

    public string Detail
    {
        get
        {
            var parts = new List<string> { Author, IsCatalog ? "Thunderstore" : Loc.Get("mods_source_file") };
            if (_sizeText.Length > 0)
                parts.Add(_sizeText);
            if (Mod.Maps.Count > 0)
                parts.Add(Loc.Format("mods_adds_map", string.Join(", ", Mod.Maps)));
            if (IsServerOnly(Mod))
                parts.Add(Loc.Get("mods_realm_server"));
            if (Mod.Replaces.Count > 0)
                parts.Add(Loc.Format("mods_replaces", string.Join(", ", Mod.Replaces)));
            return string.Join("  ·  ", parts);
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (Set(ref _busy, value))
                OnPropertyChanged(nameof(ActionsEnabled));
        }
    }

    public bool ActionsEnabled => !_busy;

    // The engine knows client, server and both; anything else loads as both with a warning.
    internal static bool RealmIsUnknown(InstalledMod mod)
    {
        var realm = (mod.Realm ?? string.Empty).Trim();
        return realm.Length != 0
               && !realm.Equals("client", StringComparison.OrdinalIgnoreCase)
               && !realm.Equals("server", StringComparison.OrdinalIgnoreCase)
               && !realm.Equals("both", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsServerOnly(InstalledMod mod) =>
        string.Equals((mod.Realm ?? string.Empty).Trim(), "server", StringComparison.OrdinalIgnoreCase);
}

public sealed class BrowseModRowViewModel : ModViewModelBase
{
    private bool _isSelected;
    private string? _installedVersion;
    private string _queueState = string.Empty;

    public BrowseModRowViewModel(ModPackage package)
    {
        Package = package;
        FullName = package.FullName;
        Name = string.IsNullOrWhiteSpace(package.Name) ? package.FullName : package.Name.Replace('_', ' ');
        Owner = string.IsNullOrWhiteSpace(package.Owner) ? Loc.Get("n_a") : package.Owner;
        Description = package.Description ?? string.Empty;
        var latest = package.Versions is { Count: > 0 } ? package.Versions[0] : null;
        LatestVersion = latest?.VersionNumber ?? Loc.Get("n_a");
        SizeBytes = Math.Max(0, latest?.FileSize ?? 0);
        SizeText = SizeBytes > 0 ? MainWindow.FormatBytes(SizeBytes) : Loc.Get("n_a");
        Downloads = package.TotalDownloads;
        DownloadsText = Downloads.ToString("N0", CultureInfo.CurrentCulture);
        UpdatedText = ModArt.Ago(package.Updated != default ? package.Updated : latest?.Uploaded ?? default);
        VersionCount = package.Versions?.Count ?? 0;
        Categories = package.Categories ?? Array.Empty<string>();
        CategoriesText = Categories.Count == 0 ? Loc.Get("n_a") : string.Join(", ", Categories);
        var deps = latest?.Dependencies ?? Array.Empty<string>();
        DependenciesText = deps.Count == 0 ? Loc.Get("mods_deps_none") : string.Join("\n", deps);
        IconSource = ModArt.LoadRemote(package.IconUrl);
        Initials = ModArt.Initials(Name);
    }

    public ModPackage Package { get; }
    public string FullName { get; }
    public string Name { get; }
    public string Owner { get; }
    public string Description { get; }
    public string LatestVersion { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }
    public long Downloads { get; }
    public string DownloadsText { get; }
    public string UpdatedText { get; }
    public int VersionCount { get; }
    public IReadOnlyList<string> Categories { get; }
    public string CategoriesText { get; }
    public string DependenciesText { get; }
    public ImageSource? IconSource { get; }
    public bool HasIcon => IconSource is not null;
    public string Initials { get; }

    public string OwnerLine => Loc.Format("mods_by", Owner);

    public string MetaLine =>
        SizeText + "    " + (Downloads == 1 ? Loc.Get("mods_downloads_one_label") : Loc.Format("mods_downloads_n_label", DownloadsText)) + "    " + UpdatedText;

    public string LatestLine => Loc.Format("mods_latest_line", LatestVersion, VersionCount);

    /// <summary>Installed copy's version, or null when the package is not installed.</summary>
    public string? InstalledVersion => _installedVersion;

    public bool IsInstalled => _installedVersion is not null;

    public bool IsCurrent => IsInstalled && !ModVersion.IsNewer(LatestVersion, _installedVersion);

    public bool HasUpdate => IsInstalled && !IsCurrent;

    public bool ShowState => IsInstalled;

    public string StateLabel =>
        IsCurrent ? Loc.Get("mods_browse_installed")
        : Loc.Format("mods_state_update", _installedVersion ?? string.Empty, LatestVersion);

    public string InstalledText =>
        _installedVersion is null ? Loc.Get("mods_not_installed") : "v" + _installedVersion;

    public void SetInstalledVersion(string? version)
    {
        if (string.Equals(_installedVersion, version, StringComparison.Ordinal))
            return;
        _installedVersion = version;
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ShowState));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(InstalledText));
        RaiseCta();
    }

    /// <summary>Queue state text ("Queued", "Installing 42%"); empty when the package is not queued.</summary>
    public string QueueState
    {
        get => _queueState;
        set
        {
            if (Set(ref _queueState, value ?? string.Empty))
                RaiseCta();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string CtaCaption =>
        _queueState.Length > 0 ? _queueState
        : IsCurrent ? Loc.Get("mods_browse_installed")
        : HasUpdate ? Loc.Format("mods_cta_update", LatestVersion, SizeText)
        : Loc.Format("mods_cta_install", SizeText);

    public bool CtaEnabled => _queueState.Length == 0 && !IsCurrent;

    public bool CtaIsUpdate => HasUpdate && _queueState.Length == 0;

    public string CtaNote =>
        IsCurrent ? Loc.Get("mods_note_current")
        : SizeBytes > 0 ? Loc.Format("mods_note_space", MainWindow.FormatBytes(ModSpace.Needed(SizeBytes)))
        : string.Empty;

    void RaiseCta()
    {
        OnPropertyChanged(nameof(CtaCaption));
        OnPropertyChanged(nameof(CtaEnabled));
        OnPropertyChanged(nameof(CtaIsUpdate));
        OnPropertyChanged(nameof(CtaNote));
    }
}

static class ModSpace
{
    // The zip, its unpacked copy and the replaced copy share the drive until the swap finishes.
    internal static long Needed(long downloadBytes) => (long)(downloadBytes * 2.6);
}

public enum ModQueueState
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
}

public sealed class ModQueueItem : ModViewModelBase
{
    private ModQueueState _state = ModQueueState.Queued;
    private int _step;
    private double _percent;
    private string _bytesLine = string.Empty;
    private string _rateText = string.Empty;
    private string _etaText = string.Empty;
    private string _detailLine = string.Empty;
    private string _errorText = string.Empty;
    private string _doneText = string.Empty;

    public ModQueueItem(ModInstallStep step, string kind, string reason, ImageSource? icon)
    {
        Step = step;
        Pin = step.Pin;
        FullName = step.Package.FullName;
        Name = string.IsNullOrWhiteSpace(step.Package.Name) ? step.Package.FullName : step.Package.Name.Replace('_', ' ');
        Version = step.Version.VersionNumber;
        SizeBytes = Math.Max(0, step.Version.FileSize);
        Kind = kind;
        Reason = reason;
        IconSource = icon;
        Initials = ModArt.Initials(Name);
    }

    public ModInstallStep Step { get; }
    public string Root { get; init; } = string.Empty;
    public string Pin { get; }
    public string FullName { get; }
    public string Name { get; }
    public string Version { get; }
    public long SizeBytes { get; }
    public string Kind { get; }
    public string Reason { get; }
    public ImageSource? IconSource { get; }
    public bool HasIcon => IconSource is not null;
    public string Initials { get; }

    public string Subtitle =>
        string.IsNullOrEmpty(Reason) ? Version + ", " + Kind : Version + ", " + Kind + ", " + Reason;

    public CancellationTokenSource? Cts { get; set; }
    public TaskCompletionSource<bool> Completion { get; private set; } = NewCompletion();

    // Download speed, smoothed so the ETA does not jump on every report.
    public long DownloadedBytes { get; set; }
    public long LastBytes { get; set; }
    public DateTime LastSample { get; set; }
    public double RateEma { get; set; }
    public DateTime Started { get; set; }

    public void ResetForRetry()
    {
        Completion = NewCompletion();
        DownloadedBytes = 0;
        LastBytes = 0;
        RateEma = 0;
        Phase = 0;
        Percent = 0;
        BytesLine = string.Empty;
        RateText = string.Empty;
        EtaText = string.Empty;
        DetailLine = string.Empty;
        ErrorText = string.Empty;
        DoneText = string.Empty;
        State = ModQueueState.Queued;
    }

    static TaskCompletionSource<bool> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ModQueueState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value))
                return;
            OnPropertyChanged(nameof(IsQueued));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsCancelled));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(StatusLine));
        }
    }

    public bool IsQueued => _state == ModQueueState.Queued;
    public bool IsRunning => _state == ModQueueState.Running;
    public bool IsDone => _state == ModQueueState.Done;
    public bool IsFailed => _state == ModQueueState.Failed;
    public bool IsCancelled => _state == ModQueueState.Cancelled;
    public bool IsActive => _state is ModQueueState.Queued or ModQueueState.Running;
    public bool CanRetry => _state is ModQueueState.Failed or ModQueueState.Cancelled;

    /// <summary>0 download, 1 unpack, 2 check, 3 install.</summary>
    public int Phase
    {
        get => _step;
        set
        {
            if (!Set(ref _step, value))
                return;
            OnPropertyChanged(nameof(StepDownload));
            OnPropertyChanged(nameof(StepUnpack));
            OnPropertyChanged(nameof(StepCheck));
            OnPropertyChanged(nameof(StepInstall));
        }
    }

    public bool StepDownload => _step == 0;
    public bool StepUnpack => _step == 1;
    public bool StepCheck => _step == 2;
    public bool StepInstall => _step == 3;

    public double Percent
    {
        get => _percent;
        set
        {
            if (Set(ref _percent, value))
                OnPropertyChanged(nameof(PercentText));
        }
    }

    public string PercentText => $"{Math.Floor(_percent):0}%";

    public string BytesLine
    {
        get => _bytesLine;
        set => Set(ref _bytesLine, value);
    }

    public string RateText
    {
        get => _rateText;
        set => Set(ref _rateText, value);
    }

    public string EtaText
    {
        get => _etaText;
        set => Set(ref _etaText, value);
    }

    public string DetailLine
    {
        get => _detailLine;
        set => Set(ref _detailLine, value);
    }

    public string ErrorText
    {
        get => _errorText;
        set
        {
            if (Set(ref _errorText, value))
                OnPropertyChanged(nameof(StatusLine));
        }
    }

    public string DoneText
    {
        get => _doneText;
        set
        {
            if (Set(ref _doneText, value))
                OnPropertyChanged(nameof(StatusLine));
        }
    }

    public string StatusLine => _state switch
    {
        ModQueueState.Queued => Loc.Format("mods_q_queued", SizeBytes > 0 ? MainWindow.FormatBytes(SizeBytes) : Loc.Get("n_a")),
        ModQueueState.Done => _doneText,
        ModQueueState.Failed => _errorText,
        ModQueueState.Cancelled => Loc.Get("mods_q_cancelled"),
        _ => string.Empty,
    };
}

public sealed class ModPlanRow
{
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;

    /// <summary>"new", "update", "on", "off" or "remove": picks the tag color.</summary>
    public string TagKind { get; init; } = string.Empty;

    public string SizeText { get; init; } = string.Empty;
    public ImageSource? IconSource { get; init; }
    public bool HasIcon => IconSource is not null;
    public string Initials => ModArt.Initials(Name);
}

public sealed class DediModPolicyRowViewModel : ModViewModelBase
{
    private int _policyIndex;

    public DediModPolicyRowViewModel(InstalledMod mod, int policyIndex)
    {
        Mod = mod;
        Id = mod.Id;
        Name = string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name;
        _policyIndex = policyIndex is >= 0 and <= 2 ? policyIndex : 0;
    }

    public InstalledMod Mod { get; }
    public string Id { get; }
    public string Name { get; }

    public int PolicyIndex
    {
        get => _policyIndex;
        set => Set(ref _policyIndex, value is >= 0 and <= 2 ? value : 0);
    }
}
