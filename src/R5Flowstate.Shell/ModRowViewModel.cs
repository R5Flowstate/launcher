using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public sealed class ModRowViewModel : INotifyPropertyChanged
{
    private bool _enabled;
    private bool _busy;
    private string _progressText = string.Empty;

    public ModRowViewModel(InstalledMod mod, string? latestThunderstoreVersion)
    {
        Mod = mod;
        FolderName = mod.FolderName;
        Id = mod.Id;
        Name = string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name;
        Author = string.IsNullOrWhiteSpace(mod.Author) ? Loc.Get("n_a") : mod.Author;
        Version = string.IsNullOrWhiteSpace(mod.Version) ? Loc.Get("n_a") : mod.Version;
        _enabled = mod.Enabled;
        IconSource = TryLoadIcon(mod.IconPath);
        HasIcon = IconSource is not null;
        LatestVersion = latestThunderstoreVersion ?? string.Empty;
        HasUpdate = IsNewer(LatestVersion, string.IsNullOrEmpty(mod.ThunderstoreVersion)
            ? mod.Version
            : mod.ThunderstoreVersion);
        RealmWarning = RealmIsUnknown(mod);
        ValidationFailed = !ModId.IsValid(mod.Id);
    }

    public InstalledMod Mod { get; }
    public string FolderName { get; }
    public string Id { get; }
    public string Name { get; }
    public string Author { get; }
    public string Version { get; }
    public ImageSource? IconSource { get; }
    public bool HasIcon { get; }
    public string LatestVersion { get; }
    public bool HasUpdate { get; }
    public bool RealmWarning { get; }
    public bool ValidationFailed { get; }
    public bool HasWarning => RealmWarning || ValidationFailed;

    public string WarningLabel =>
        ValidationFailed ? Loc.Get("mods_badge_invalid") : Loc.Get("mods_badge_realm");

    public string UpdateLabel => Loc.Get("mods_update");

    public string Detail =>
        Author + "  ·  " + Version + "  ·  " + Id
        + (IsServerOnly(Mod) ? "  ·  " + Loc.Get("mods_realm_server") : string.Empty)
        + (Mod.Replaces.Count == 0 ? string.Empty : "  ·  " + Loc.Format("mods_replaces", string.Join(", ", Mod.Replaces)));

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            OnPropertyChanged();
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value)
                return;
            _busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionsEnabled));
        }
    }

    public bool ActionsEnabled => !_busy;

    public string ProgressText
    {
        get => _progressText;
        set
        {
            var next = value ?? string.Empty;
            if (_progressText == next)
                return;
            _progressText = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProgress));
        }
    }

    public bool HasProgress => !string.IsNullOrEmpty(_progressText);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal static ImageSource? TryLoadIcon(string? path)
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
            bmp.DecodePixelWidth = 64;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

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

    internal static bool IsNewer(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(current))
            return false;
        return CompareVersions(candidate.Trim(), current.Trim()) > 0;
    }

    internal static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.', StringSplitOptions.None);
        var pb = b.Split('.', StringSplitOptions.None);
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            var na = TakeLeadingNumber(sa);
            var nb = TakeLeadingNumber(sb);
            var c = na.CompareTo(nb);
            if (c != 0)
                return c;
            c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (c != 0)
                return c;
        }

        return 0;
    }

    static int TakeLeadingNumber(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsDigit(s[i]))
            i++;
        if (i == 0)
            return 0;
        return int.TryParse(s.AsSpan(0, i), out var n) ? n : 0;
    }
}

public sealed class BrowseModRowViewModel : INotifyPropertyChanged
{
    private bool _busy;
    private string _progressText = string.Empty;

    public BrowseModRowViewModel(ModPackage package)
    {
        Package = package;
        FullName = package.FullName;
        Name = string.IsNullOrWhiteSpace(package.Name) ? package.FullName : package.Name;
        Owner = string.IsNullOrWhiteSpace(package.Owner) ? Loc.Get("n_a") : package.Owner;
        Description = package.Description ?? string.Empty;
        var latest = package.Versions is { Count: > 0 } ? package.Versions[0] : null;
        LatestVersion = latest?.VersionNumber ?? Loc.Get("n_a");
        IconSource = TryLoadRemoteIcon(package.IconUrl);
        HasIcon = IconSource is not null;
    }

    public ModPackage Package { get; }
    public string FullName { get; }
    public string Name { get; }
    public string Owner { get; }
    public string Description { get; }
    public string LatestVersion { get; }
    public ImageSource? IconSource { get; }
    public bool HasIcon { get; }

    /// <summary>Installed copy's version, or null when the package is not installed.</summary>
    public string? InstalledVersion { get; private set; }

    public bool IsInstalled => InstalledVersion is not null;

    public bool IsCurrent =>
        IsInstalled && !ModRowViewModel.IsNewer(LatestVersion, InstalledVersion!);

    public void SetInstalledVersion(string? version)
    {
        if (string.Equals(InstalledVersion, version, StringComparison.Ordinal))
            return;
        InstalledVersion = version;
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(InstallCaption));
        OnPropertyChanged(nameof(InstallEnabled));
    }

    // Listing icons come from the Thunderstore CDN; any other host or scheme is not fetched.
    static ImageSource? TryLoadRemoteIcon(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("thunderstore.io", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".thunderstore.io", StringComparison.OrdinalIgnoreCase)))
            return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = uri;
            bmp.DecodePixelWidth = 64;
            bmp.EndInit();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public string Detail
    {
        get
        {
            var desc = Description;
            if (desc.Length > 140)
                desc = desc[..140] + "...";
            return Owner + "  ·  " + LatestVersion
                   + (string.IsNullOrWhiteSpace(desc) ? string.Empty : "  ·  " + desc);
        }
    }

    public string InstallCaption =>
        _busy ? Loc.Get("mods_installing")
        : IsCurrent ? Loc.Get("mods_browse_installed")
        : IsInstalled ? Loc.Get("mods_browse_update")
        : Loc.Get("mods_install");

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value)
                return;
            _busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InstallEnabled));
            OnPropertyChanged(nameof(InstallCaption));
        }
    }

    public bool InstallEnabled => !_busy && !IsCurrent;

    public string ProgressText
    {
        get => _progressText;
        set
        {
            var next = value ?? string.Empty;
            if (_progressText == next)
                return;
            _progressText = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProgress));
        }
    }

    public bool HasProgress => !string.IsNullOrEmpty(_progressText);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DediModPolicyRowViewModel : INotifyPropertyChanged
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
        set
        {
            var next = value is >= 0 and <= 2 ? value : 0;
            if (_policyIndex == next)
                return;
            _policyIndex = next;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
