using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    const int MaxModPolicyEntries = 64;
    const string SessionModsBackupName = "mods.vdf.launcher-bak";
    const string ModPolicyFileName = "mod_policy.txt";
    const int MaxBrowseCategoryChips = 8;
    static readonly TimeSpan CatalogMaxAge = TimeSpan.FromMinutes(10);

    // Older than any install this or another launcher could still be writing.
    static readonly TimeSpan StagingSweepAge = TimeSpan.FromMinutes(30);

    enum ModsSection
    {
        Installed,
        Browse,
        Downloads,
    }

    readonly List<ModRowViewModel> _modRows = new();
    readonly List<BrowseModRowViewModel> _browseRows = new();
    readonly List<DediModPolicyRowViewModel> _dediModRows = new();
    readonly List<ModFilterChip> _browseChips = new();
    readonly ObservableCollection<ModQueueItem> _modQueue = new();
    IReadOnlyList<ModPackage> _tsPackages = Array.Empty<ModPackage>();
    DateTime _catalogLoadedUtc = DateTime.MinValue;
    Task? _catalogLoad;

    // Which enabled map mods the mode rail was built from.
    string _catalogModsKey = string.Empty;

    static IReadOnlyList<ModPlaylistSource> ModPlaylistSources(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<ModPlaylistSource>();

        string modsDir;
        try
        {
            modsDir = ModsStore.ModsDirectory(root);
        }
        catch
        {
            return Array.Empty<ModPlaylistSource>();
        }

        return ModsStore.Discover(root)
            .Where(m => m.Enabled && m.Maps.Count > 0)
            .Select(m => new ModPlaylistSource
            {
                Id = m.Id,
                Folder = Path.Combine(modsDir, m.FolderName),
                Maps = m.Maps,
            })
            .ToList();
    }

    // Covers the patch files too: a mod update that renames a playlist keeps its id and maps.
    static string ModPlaylistSourcesKey(IReadOnlyList<ModPlaylistSource> mods) =>
        string.Join("|", mods.Select(m => m.Id + ":" + string.Join(",", m.Maps) + ":" + PlaylistPatchStamp(m.Folder)));

    static string PlaylistPatchStamp(string folder)
    {
        foreach (var name in new[] { "playlists_r5_patch.txt", "playlist_r5_patch.txt" })
        {
            try
            {
                var info = new FileInfo(Path.Combine(folder, name));
                if (info.Exists)
                    return info.Length.ToString(CultureInfo.InvariantCulture) + "@" +
                           info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return string.Empty;
    }

    void ReloadCatalogIfModsChanged()
    {
        if (ModPlaylistSourcesKey(ModPlaylistSources(ModsInstallRoot())) != _catalogModsKey)
            ReloadPlaylistsAndMaps(selectSaved: true);
    }

    ThunderstoreClient? _thunderstore;
    ModInstaller? _modInstaller;
    ModsSection _modsSection = ModsSection.Installed;
    string _installedFilter = "all";
    string _browseCategory = "all";
    string _browseSort = "popular";
    BrowseModRowViewModel? _browseSelected;
    bool _modQueueRunning;
    bool _modsBusy;
    bool _suppressModEvents;
    bool _suppressDediModEvents;
    int _dediModPolicy;
    bool _dediModPolicyLoaded;
    string _modsStatus = string.Empty;

    bool _sessionModsPending;
    bool _sessionModsClientSeen;
    bool _sessionModsForceRelaunch;
    string _sessionModsInstall = string.Empty;
    string _sessionModsWritten = string.Empty;
    string _sessionModsBackup = string.Empty;

    ThunderstoreClient ModsThunderstore() =>
        _thunderstore ??= new ThunderstoreClient();

    ModInstaller ModsInstaller() =>
        _modInstaller ??= new ModInstaller(ModsThunderstore());

    static bool ThunderstoreLive => ProductConstants.ThunderstoreEnabled;

    void DisposeModsServices()
    {
        foreach (var item in _modQueue)
        {
            try { item.Cts?.Cancel(); }
            catch { /* already disposed */ }
        }

        _modInstaller = null;
        _thunderstore?.Dispose();
        _thunderstore = null;
    }

    void SetModsStatus(string text)
    {
        _modsStatus = text ?? string.Empty;
        if (TxtModsStatus is not null)
            TxtModsStatus.Text = _modsStatus;
    }

    void SetModsBusy(bool busy)
    {
        _modsBusy = busy;
        if (BtnModsRefresh is not null)
            BtnModsRefresh.IsEnabled = !busy;
        if (BtnModsInstallFile is not null)
            BtnModsInstallFile.IsEnabled = !busy;
        if (BtnModsImport is not null)
            BtnModsImport.IsEnabled = !busy;
        if (BtnModsExport is not null)
            BtnModsExport.IsEnabled = !busy;
    }

    string ModsInstallRoot() => TxtInstallRoot?.Text?.Trim() ?? string.Empty;

    static bool ModsRootUsable(string root) => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);

    static bool GameRunning(string root) =>
        !string.IsNullOrWhiteSpace(root)
        && (ProcessSpawner.IsRoleAlive(LaunchRole.Client, root) || ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root));

    static long FreeBytes(string root)
    {
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(root));
            return string.IsNullOrEmpty(drive) ? -1 : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    // ------------------------------------------------------------------ sections

    void OnModsSectionInstalled(object sender, RoutedEventArgs e)
    {
        _modsSection = ModsSection.Installed;
        ApplyModsSection();
    }

    void OnModsSectionBrowse(object sender, RoutedEventArgs e)
    {
        if (!ThunderstoreLive)
            return;
        _modsSection = ModsSection.Browse;
        ApplyModsSection();
        _ = EnsureCatalogAsync(force: false, reportErrors: true);
    }

    void OnModsSectionDownloads(object sender, RoutedEventArgs e)
    {
        _modsSection = ModsSection.Downloads;
        ApplyModsSection();
    }

    void ApplyModsSection()
    {
        if (!ThunderstoreLive && _modsSection == ModsSection.Browse)
            _modsSection = ModsSection.Installed;
        if (_modQueue.Count == 0 && _modsSection == ModsSection.Downloads)
            _modsSection = ModsSection.Installed;

        var catalog = ThunderstoreLive ? Visibility.Visible : Visibility.Collapsed;
        if (BtnModsSectionBrowse is not null)
            BtnModsSectionBrowse.Visibility = catalog;
        if (BtnModsShare is not null)
            BtnModsShare.Visibility = catalog;
        if (BtnModsSectionDownloads is not null)
            BtnModsSectionDownloads.Visibility = _modQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ListModsQueue is not null && ListModsQueue.ItemsSource is null)
            ListModsQueue.ItemsSource = _modQueue;

        StyleTab(BtnModsSectionInstalled, _modsSection == ModsSection.Installed);
        StyleTab(BtnModsSectionBrowse, _modsSection == ModsSection.Browse);
        StyleTab(BtnModsSectionDownloads, _modsSection == ModsSection.Downloads);
        if (PanelModsInstalled is not null)
            PanelModsInstalled.Visibility = _modsSection == ModsSection.Installed ? Visibility.Visible : Visibility.Collapsed;
        if (PanelModsBrowse is not null)
            PanelModsBrowse.Visibility = _modsSection == ModsSection.Browse ? Visibility.Visible : Visibility.Collapsed;
        if (PanelModsDownloads is not null)
            PanelModsDownloads.Visibility = _modsSection == ModsSection.Downloads ? Visibility.Visible : Visibility.Collapsed;
        UpdateModsSectionLabels();
    }

    void UpdateModsSectionLabels()
    {
        var count = _modRows.Count;
        var updates = _modRows.Count(r => r.HasUpdate);
        if (BtnModsSectionInstalled is not null)
        {
            BtnModsSectionInstalled.Content = updates > 0
                ? Loc.Format("mods_installed_n_upd", count, updates)
                : Loc.Format("mods_installed_n", count);
        }

        if (BtnModsSectionBrowse is not null)
        {
            BtnModsSectionBrowse.Content = _browseRows.Count > 0
                ? Loc.Format("mods_browse_n", _browseRows.Count)
                : Loc.Get("mods_browse_tab");
        }

        if (BtnModsSectionDownloads is not null)
        {
            var active = _modQueue.Count(i => i.IsActive);
            BtnModsSectionDownloads.Content = active > 0
                ? Loc.Format("mods_downloads_n", active)
                : Loc.Get("mods_downloads_tab");
        }

        if (BtnModsFilterAll is not null)
        {
            BtnModsFilterAll.Content = Loc.Format("mods_filter_all", count);
            BtnModsFilterOn.Content = Loc.Format("mods_filter_on", _modRows.Count(r => r.Enabled));
            BtnModsFilterOff.Content = Loc.Format("mods_filter_off", _modRows.Count(r => !r.Enabled));
            BtnModsFilterUpd.Content = Loc.Format("mods_filter_upd", updates);
        }

        if (BtnModsUpdateAll is not null)
        {
            var show = ThunderstoreLive && updates > 0;
            BtnModsUpdateAll.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                var bytes = _modRows.Where(r => r.HasUpdate).Sum(r => r.UpdateBytes);
                BtnModsUpdateAll.Content = Loc.Format("mods_update_all", updates, FormatBytes(bytes));
            }
        }
    }

    static void StyleFilter(Button? btn, bool on)
    {
        if (btn is null)
            return;
        if (btn.TryFindResource(on ? "FilterTabOn" : "FilterTab") is Style style)
            btn.Style = style;
    }

    void OnModsSearchChanged(object sender, TextChangedEventArgs e)
    {
        BindInstalledMods();
        BindBrowseMods();
    }

    void OnModsRefresh(object sender, RoutedEventArgs e)
    {
        if (_modsSection == ModsSection.Browse)
            _ = EnsureCatalogAsync(force: true, reportErrors: true);
        else
            _ = RefreshInstalledModsAsync();
    }

    async Task RefreshModsPanelAsync()
    {
        ApplyModsSection();
        SweepModStaging();
        await RefreshInstalledModsAsync().ConfigureAwait(true);
        RefreshDediModPolicyUi();
        UpdateGameRunningBar();
        UpdateQueueUi();
        _ = EnsureCatalogAsync(force: false, reportErrors: _modsSection == ModsSection.Browse);
    }

    void SweepModStaging()
    {
        if (_modQueueRunning)
            return;
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
            return;
        _ = Task.Run(() =>
        {
            try
            {
                var n = ModsStore.SweepStaging(root, StagingSweepAge);
                if (n > 0)
                    Dispatcher.BeginInvoke(() => Log("Mods: removed " + n + " leftover install folder(s)."));
            }
            catch
            {
                // Best effort; the next tab open retries.
            }
        });
    }

    void UpdateGameRunningBar()
    {
        if (BarModsGameRunning is null)
            return;
        BarModsGameRunning.Visibility = GameRunning(ModsInstallRoot()) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ installed

    async Task RefreshInstalledModsAsync()
    {
        if (_modsBusy)
            return;
        SetModsBusy(true);
        try
        {
            await LoadInstalledModsCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            SetModsBusy(false);
        }
    }

    async Task LoadInstalledModsCoreAsync()
    {
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
        {
            _modRows.Clear();
            BindInstalledMods();
            UpdateBrowseInstalledState();
            UpdateModsSectionLabels();
            SetModsStatus(Loc.Get("mods_no_install"));
            return;
        }

        try
        {
            var skipped = new List<string>();
            var found = await Task.Run(() => ModsStore.Discover(root, skipped)).ConfigureAwait(true);

            _modRows.Clear();
            foreach (var mod in found)
                _modRows.Add(new ModRowViewModel(mod));
            ApplyCatalogToInstalled();

            BindInstalledMods();
            UpdateBrowseInstalledState();
            UpdateModsSectionLabels();
            if (skipped.Count > 0)
                SetModsStatus(Loc.Format("mods_status_skipped", skipped.Count));
            else if (_modRows.Count == 0)
                SetModsStatus(Loc.Get("mods_empty"));
            else if (_modsSection == ModsSection.Installed)
                SetModsStatus(string.Empty);

            ReloadCatalogIfModsChanged();
            _ = FillModSizesAsync(root, _modRows.ToList());
        }
        catch (Exception ex)
        {
            SetModsStatus(ex.Message);
        }
    }

    async Task FillModSizesAsync(string root, IReadOnlyList<ModRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            var size = await Task.Run(() => ModsStore.FolderSizeBytes(root, row.FolderName)).ConfigureAwait(true);
            row.SizeText = size > 0 ? FormatBytes(size) : string.Empty;
        }
    }

    // An installed mod maps to a catalog package by its Owner-Name folder (manifest-backed
    // installs) or by its Owner.Name id; never by display name.
    void ApplyCatalogToInstalled()
    {
        var byFull = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in _tsPackages)
        {
            if (string.IsNullOrEmpty(pkg.FullName) || pkg.Versions.Count == 0)
                continue;
            byFull.TryAdd(pkg.FullName, pkg);
            byId.TryAdd(ModInstaller.ExpectedCatalogId(pkg), pkg);
        }

        foreach (var row in _modRows)
        {
            ModPackage? pkg = null;
            if (row.IsCatalog)
                byFull.TryGetValue(row.FolderName, out pkg);
            if (pkg is null)
                byId.TryGetValue(row.Id, out pkg);
            row.SetCatalog(pkg);
        }
    }

    // A catalog package is installed when a mod folder carries its <Owner>.<Name> id or its
    // Owner-Name folder; the manifest version is the one Thunderstore compares against.
    void UpdateBrowseInstalledState()
    {
        foreach (var row in _browseRows)
        {
            var id = ModInstaller.ExpectedCatalogId(row.Package);
            var mod = _modRows.FirstOrDefault(r =>
                string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.FolderName, row.FullName, StringComparison.OrdinalIgnoreCase));
            row.SetInstalledVersion(mod?.InstalledVersion);
        }
    }

    void BindInstalledMods()
    {
        if (ListModsInstalled is null)
            return;

        var q = TxtModsSearch?.Text?.Trim() ?? string.Empty;
        IEnumerable<ModRowViewModel> rows = _modRows;
        if (q.Length > 0)
        {
            rows = rows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        rows = _installedFilter switch
        {
            "on" => rows.Where(r => r.Enabled),
            "off" => rows.Where(r => !r.Enabled),
            "upd" => rows.Where(r => r.HasUpdate),
            _ => rows,
        };

        _suppressModEvents = true;
        ListModsInstalled.ItemsSource = null;
        ListModsInstalled.ItemsSource = rows.ToList();
        _suppressModEvents = false;
    }

    void OnModsFilter(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;
        _installedFilter = key;
        StyleFilter(BtnModsFilterAll, key == "all");
        StyleFilter(BtnModsFilterOn, key == "on");
        StyleFilter(BtnModsFilterOff, key == "off");
        StyleFilter(BtnModsFilterUpd, key == "upd");
        BindInstalledMods();
    }

    void OnModEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressModEvents)
            return;
        if (sender is not CheckBox { Tag: ModRowViewModel row } box)
            return;

        var wanted = box.IsChecked == true;
        var root = ModsInstallRoot();
        try
        {
            ModsStore.SetEnabled(root, row.Id, wanted);
            _suppressModEvents = true;
            row.Enabled = wanted;
            _suppressModEvents = false;
            UpdateModsSectionLabels();
            UpdateGameRunningBar();
            if (_installedFilter is "on" or "off")
                BindInstalledMods();
            ReloadCatalogIfModsChanged();
        }
        catch (Exception ex)
        {
            _suppressModEvents = true;
            row.Enabled = !wanted;
            box.IsChecked = !wanted;
            _suppressModEvents = false;
            SetModsStatus(Loc.Format("mods_enable_failed", ex.Message));
        }
    }

    void OnModMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ModRowViewModel row })
            MoveMod(row, -1);
    }

    void OnModMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ModRowViewModel row })
            MoveMod(row, 1);
    }

    void MoveMod(ModRowViewModel row, int delta)
    {
        var i = _modRows.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _modRows.Count)
            return;

        var root = ModsInstallRoot();
        (_modRows[i], _modRows[j]) = (_modRows[j], _modRows[i]);
        try
        {
            ModsStore.Reorder(root, _modRows.Select(r => r.Id).ToList());
            BindInstalledMods();
        }
        catch (Exception ex)
        {
            (_modRows[i], _modRows[j]) = (_modRows[j], _modRows[i]);
            SetModsStatus(Loc.Format("mods_reorder_failed", ex.Message));
        }
    }

    void OnModRowUpdate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModRowViewModel { Package: { } pkg } })
            return;
        _ = QueueCatalogInstallAsync(new[] { (pkg, (string?)null) }, string.Empty, askAlways: false);
    }

    void OnModsUpdateAll(object sender, RoutedEventArgs e)
    {
        var roots = _modRows
            .Where(r => r.HasUpdate && r.Package is not null)
            .Select(r => (r.Package!, (string?)null))
            .ToList();
        if (roots.Count == 0)
            return;
        _ = QueueCatalogInstallAsync(roots, string.Empty, askAlways: roots.Count > 1);
    }

    void OnModMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRowViewModel row } btn)
            return;

        var itemStyle = (Style)FindResource("DarkMenuItem");
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            PlacementTarget = btn,
            Placement = PlacementMode.Bottom,
        };

        MenuItem Item(string key, Action act)
        {
            var mi = new MenuItem { Header = Loc.Get(key), Style = itemStyle };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
            return mi;
        }

        Item("mods_menu_open_folder", () => OpenModFolder(row));
        if (row.Package is { } pkg)
        {
            Item("mods_view_page", () => OpenPackagePage(pkg));
            if (row.IsCatalog)
            {
                Item("mods_menu_reinstall", () =>
                {
                    var have = pkg.Versions.Any(v => string.Equals(v.VersionNumber, row.InstalledVersion, StringComparison.OrdinalIgnoreCase))
                        ? row.InstalledVersion
                        : null;
                    _ = QueueCatalogInstallAsync(new[] { (pkg, have) }, string.Empty, askAlways: false);
                });
            }
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparator") });
        Item("mods_menu_uninstall", () => _ = UninstallModAsync(row));
        menu.IsOpen = true;
    }

    void OpenModFolder(ModRowViewModel row)
    {
        try
        {
            var modsDir = ModsStore.ModsDirectory(ModsInstallRoot());
            if (SafePath.TryJoin(modsDir, row.FolderName, out var dir) && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            SetModsStatus(ex.Message);
        }
    }

    void OnModsOpenFolder(object sender, RoutedEventArgs e)
    {
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
        {
            SetModsStatus(Loc.Get("mods_no_install"));
            return;
        }

        try
        {
            var modsDir = ModsStore.ModsDirectory(root);
            Directory.CreateDirectory(modsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + modsDir + "\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            SetModsStatus(ex.Message);
        }
    }

    // Only Thunderstore pages open; the listing URL is remote data.
    void OpenPackagePage(ModPackage pkg)
    {
        var url = pkg.PackageUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = "https://thunderstore.io/c/" + ThunderstoreClient.Community + "/p/" + pkg.Owner + "/" + pkg.Name + "/";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !ModArt.IsThunderstoreHost(uri))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetModsStatus(ex.Message);
        }
    }

    void OnModUninstall(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ModRowViewModel row })
            _ = UninstallModAsync(row);
    }

    async Task UninstallModAsync(ModRowViewModel row)
    {
        if (_modsBusy || row.Busy)
            return;

        var root = ModsInstallRoot();
        if (GameRunning(root))
        {
            SetModsStatus(Loc.Get("mods_uninstall_game"));
            return;
        }

        if (_modQueue.Any(q => q.IsActive && string.Equals(q.FullName, row.FolderName, StringComparison.OrdinalIgnoreCase)))
        {
            SetModsStatus(Loc.Format("mods_uninstall_queued", row.Name));
            return;
        }

        var dependents = _modRows
            .Where(r => r != row && r.Mod.Dependencies.Any(d =>
                ModInstallPlanner.TrySplitPin(d, out var full, out _)
                && string.Equals(full, row.FolderName, StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Name)
            .ToList();

        var body = Loc.Format("mods_uninstall_confirm", row.Name);
        if (dependents.Count > 0)
            body += "\n\n" + Loc.Format("mods_uninstall_needed_by", string.Join(", ", dependents));

        var ok = ModPlanWindow.Ask(this, new ModPlanSpec
        {
            Headline = Loc.Format("mods_uninstall_headline", row.Name),
            Body = body,
            Rows = new[]
            {
                new ModPlanRow
                {
                    Name = row.Name,
                    Detail = row.Detail,
                    Tag = Loc.Get("mods_tag_remove"),
                    TagKind = "remove",
                    SizeText = row.SizeText,
                    IconSource = row.IconSource,
                },
            },
            PrimaryText = Loc.Get("mods_uninstall"),
            Danger = true,
        });
        if (!ok)
            return;

        row.Busy = true;
        SetModsBusy(true);
        try
        {
            await Task.Run(() => ModsStore.Uninstall(root, row.FolderName)).ConfigureAwait(true);
            SetModsStatus(Loc.Format("mods_uninstall_ok", row.Name));
            await LoadInstalledModsCoreAsync().ConfigureAwait(true);
            RefreshDediModPolicyUi();
        }
        catch (Exception ex)
        {
            SetModsStatus(Loc.Format("mods_uninstall_failed", ex.Message));
        }
        finally
        {
            row.Busy = false;
            SetModsBusy(false);
        }
    }

    void OnModsInstallFile(object sender, RoutedEventArgs e) => _ = InstallModFromFileAsync();

    async Task InstallModFromFileAsync()
    {
        if (_modsBusy)
            return;

        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
        {
            SetModsStatus(Loc.Get("mods_no_install"));
            return;
        }

        var dlg = new OpenFileDialog
        {
            Filter = Loc.Get("mods_zip_filter"),
            Title = Loc.Get("mods_file_title"),
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true)
            return;

        SetModsBusy(true);
        SetModsStatus(Loc.Get("mods_installing"));
        try
        {
            var progress = new Progress<ContentInstallProgress>(p => SetModsStatus(FileInstallProgressText(p)));
            await ModsInstaller().InstallFromZipAsync(root, dlg.FileName, folderName: null, progress)
                .ConfigureAwait(true);
            SetModsStatus(Loc.Get("mods_install_ok"));
            await LoadInstalledModsCoreAsync().ConfigureAwait(true);
            RefreshDediModPolicyUi();
        }
        catch (Exception ex)
        {
            SetModsStatus(Loc.Format("mods_install_failed", ex.Message));
        }
        finally
        {
            SetModsBusy(false);
        }
    }

    static string FileInstallProgressText(ContentInstallProgress p) => p.Phase switch
    {
        "extract" when p.Total > 0 => Loc.Format(
            "mods_file_unpacking", $"{p.Current * 100.0 / p.Total:0}", p.JobCurrent, p.JobTotal),
        "check" => Loc.Get("mods_step_check"),
        "commit" => Loc.Get("mods_step_install"),
        _ => Loc.Get("mods_installing"),
    };

    // ------------------------------------------------------------------ catalog

    async Task EnsureCatalogAsync(bool force, bool reportErrors)
    {
        if (!ThunderstoreLive)
            return;
        if (_catalogLoad is { IsCompleted: false })
        {
            await _catalogLoad.ConfigureAwait(true);
            return;
        }

        if (!force && _tsPackages.Count > 0 && DateTime.UtcNow - _catalogLoadedUtc < CatalogMaxAge)
            return;

        _catalogLoad = LoadCatalogCoreAsync(reportErrors);
        await _catalogLoad.ConfigureAwait(true);
    }

    async Task LoadCatalogCoreAsync(bool reportErrors)
    {
        if (reportErrors)
            SetModsStatus(Loc.Get("mods_catalog_loading"));
        try
        {
            var packages = await ModsThunderstore().ListPackagesAsync().ConfigureAwait(true);
            _tsPackages = packages;
            _catalogLoadedUtc = DateTime.UtcNow;

            var selected = _browseSelected?.FullName;
            _browseRows.Clear();
            foreach (var pkg in packages)
            {
                if (pkg.IsDeprecated || pkg.IsNsfw || pkg.Versions.Count == 0)
                    continue;
                _browseRows.Add(new BrowseModRowViewModel(pkg));
            }

            _browseSelected = null;
            RebuildBrowseChips();
            UpdateBrowseInstalledState();
            SyncBrowseQueueStates();
            ApplyCatalogToInstalled();
            BindInstalledMods();
            BindBrowseMods(selected);
            UpdateModsSectionLabels();
            if (reportErrors)
                SetModsStatus(_browseRows.Count == 0 ? Loc.Get("mods_browse_empty") : string.Empty);
        }
        catch (Exception ex)
        {
            if (reportErrors)
                SetModsStatus(Loc.Format("mods_browse_failed", ex.Message));
            else
                Log("Mods: catalog load failed: " + ex.Message);
        }
    }

    void RebuildBrowseChips()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _browseRows)
        {
            foreach (var cat in row.Categories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(cat) || cat.Length > 40)
                    continue;
                counts[cat] = counts.TryGetValue(cat, out var n) ? n + 1 : 1;
            }
        }

        _browseChips.Clear();
        _browseChips.Add(new ModFilterChip("all", Loc.Format("mods_filter_all", _browseRows.Count)));
        foreach (var (cat, n) in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                     .Take(MaxBrowseCategoryChips))
            _browseChips.Add(new ModFilterChip(cat, cat + "  " + n));

        if (!_browseChips.Any(c => string.Equals(c.Key, _browseCategory, StringComparison.OrdinalIgnoreCase)))
            _browseCategory = "all";
        foreach (var chip in _browseChips)
            chip.IsOn = string.Equals(chip.Key, _browseCategory, StringComparison.OrdinalIgnoreCase);
        if (ListModsCategories is not null)
        {
            ListModsCategories.ItemsSource = null;
            ListModsCategories.ItemsSource = _browseChips;
        }
    }

    void OnBrowseCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModFilterChip chip })
            return;
        _browseCategory = chip.Key;
        foreach (var c in _browseChips)
            c.IsOn = ReferenceEquals(c, chip);
        BindBrowseMods(_browseSelected?.FullName);
    }

    void OnBrowseSort(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;
        _browseSort = key;
        StyleFilter(BtnModsSortPopular, key == "popular");
        StyleFilter(BtnModsSortUpdated, key == "updated");
        StyleFilter(BtnModsSortName, key == "name");
        if (BtnModsSortName is not null)
            BtnModsSortName.Margin = new Thickness(0);
        BindBrowseMods(_browseSelected?.FullName);
    }

    void BindBrowseMods(string? keepSelected = null)
    {
        if (ListModsBrowse is null)
            return;

        var q = TxtModsSearch?.Text?.Trim() ?? string.Empty;
        IEnumerable<BrowseModRowViewModel> rows = _browseRows;
        if (_browseCategory != "all")
            rows = rows.Where(r => r.Categories.Contains(_browseCategory, StringComparer.OrdinalIgnoreCase));
        if (q.Length > 0)
        {
            rows = rows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Owner.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.FullName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.CategoriesText.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        rows = _browseSort switch
        {
            "updated" => rows.OrderByDescending(r => r.Package.Updated).ThenBy(r => r.Name),
            "name" => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderByDescending(r => r.Package.IsPinned).ThenByDescending(r => r.Downloads).ThenBy(r => r.Name),
        };

        var list = rows.ToList();
        ListModsBrowse.ItemsSource = list;

        var keep = keepSelected ?? _browseSelected?.FullName;
        var pick = list.FirstOrDefault(r => string.Equals(r.FullName, keep, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault();
        SelectBrowseRow(pick);
    }

    void SelectBrowseRow(BrowseModRowViewModel? row)
    {
        if (_browseSelected is not null && !ReferenceEquals(_browseSelected, row))
            _browseSelected.IsSelected = false;
        _browseSelected = row;
        if (row is not null)
            row.IsSelected = true;
        if (PanelModDetail is null)
            return;
        PanelModDetail.DataContext = row;
        PanelModDetail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnBrowseCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BrowseModRowViewModel row })
            SelectBrowseRow(row);
    }

    void OnBrowseCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BrowseModRowViewModel row })
            return;
        SelectBrowseRow(row);
        if (row.CtaEnabled)
            _ = QueueCatalogInstallAsync(new[] { (row.Package, (string?)null) }, string.Empty, askAlways: false);
    }

    void OnModDetailOpenPage(object sender, RoutedEventArgs e)
    {
        if (_browseSelected is { } row)
            OpenPackagePage(row.Package);
    }

    void OnBrowseModInstall(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BrowseModRowViewModel row } || !row.CtaEnabled)
            return;
        _ = QueueCatalogInstallAsync(new[] { (row.Package, (string?)null) }, string.Empty, askAlways: false);
    }

    void SyncBrowseQueueStates()
    {
        foreach (var row in _browseRows)
        {
            var item = _modQueue.LastOrDefault(q =>
                q.IsActive && string.Equals(q.FullName, row.FullName, StringComparison.OrdinalIgnoreCase));
            row.QueueState = item is null ? string.Empty
                : item.IsQueued ? Loc.Get("mods_q_state_queued")
                : Loc.Format("mods_q_state_running", item.PercentText);
        }
    }

    // ------------------------------------------------------------------ queue

    ModPlanRow PlanRow(ModInstallStep step)
    {
        var installed = _modRows.FirstOrDefault(r =>
            string.Equals(r.FolderName, step.Package.FullName, StringComparison.OrdinalIgnoreCase));
        var (tag, kind) = installed is null ? (Loc.Get("mods_tag_new"), "new")
            : string.Equals(installed.InstalledVersion, step.Version.VersionNumber, StringComparison.OrdinalIgnoreCase)
                ? (Loc.Get("mods_tag_reinstall"), "reinstall")
                : (Loc.Get("mods_tag_update"), "update");
        var detail = step.Package.Owner + "  ·  " + (installed is null
            ? step.Version.VersionNumber
            : installed.InstalledVersion + " > " + step.Version.VersionNumber);
        return new ModPlanRow
        {
            Name = string.IsNullOrWhiteSpace(step.Package.Name) ? step.Package.FullName : step.Package.Name.Replace('_', ' '),
            Detail = detail,
            Tag = tag,
            TagKind = kind,
            SizeText = step.Version.FileSize > 0 ? FormatBytes(step.Version.FileSize) : Loc.Get("n_a"),
            IconSource = IconFor(step.Package),
        };
    }

    System.Windows.Media.ImageSource? IconFor(ModPackage pkg) =>
        _browseRows.FirstOrDefault(r => string.Equals(r.FullName, pkg.FullName, StringComparison.OrdinalIgnoreCase))?.IconSource
        ?? ModArt.LoadRemote(pkg.IconUrl);

    string QueueKind(ModInstallStep step)
    {
        var installed = _modRows.FirstOrDefault(r =>
            string.Equals(r.FolderName, step.Package.FullName, StringComparison.OrdinalIgnoreCase));
        if (installed is null)
            return Loc.Get("mods_kind_new");
        return string.Equals(installed.InstalledVersion, step.Version.VersionNumber, StringComparison.OrdinalIgnoreCase)
            ? Loc.Get("mods_kind_reinstall")
            : Loc.Format("mods_kind_update", installed.InstalledVersion);
    }

    /// <summary>
    /// Plans the packages (dependencies first), confirms when there is more than one or the
    /// drive is short, and queues them. Returns the queued items, or null when nothing was queued.
    /// </summary>
    async Task<IReadOnlyList<ModQueueItem>?> QueueCatalogInstallAsync(
        IReadOnlyList<(ModPackage Package, string? Version)> roots,
        string reason,
        bool askAlways)
    {
        if (!ThunderstoreLive)
            return null;
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
        {
            SetModsStatus(Loc.Get("mods_no_install"));
            return null;
        }

        var unresolved = new List<string>();
        IReadOnlyList<ModInstallStep> plan;
        try
        {
            var installed = await Task.Run(() => ModsStore.Discover(root)).ConfigureAwait(true);
            plan = ModInstallPlanner.Plan(_tsPackages, roots, installed, unresolved);
        }
        catch (Exception ex)
        {
            SetModsStatus(Loc.Format("mods_install_failed", ex.Message));
            return null;
        }

        if (unresolved.Count > 0)
        {
            SetModsStatus(Loc.Format("mods_install_failed",
                Loc.Format("mods_deps_unresolved", string.Join(", ", unresolved))));
            return null;
        }

        plan = plan.Where(s => !_modQueue.Any(q =>
            q.IsActive && string.Equals(q.FullName, s.Package.FullName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (plan.Count == 0)
        {
            SetModsStatus(Loc.Get("mods_q_nothing"));
            return null;
        }

        var download = plan.Sum(s => Math.Max(0, s.Version.FileSize));
        var need = ModSpace.Needed(download);
        var free = FreeBytes(root);
        if (askAlways || plan.Count > 1 || (free >= 0 && free < need))
        {
            var first = plan[^1];
            var ok = ModPlanWindow.Ask(this, new ModPlanSpec
            {
                Headline = plan.Count == 1
                    ? Loc.Format("mods_plan_one", PlanRow(first).Name)
                    : Loc.Format("mods_plan_many", plan.Count),
                Body = plan.Count > 1 && !askAlways
                    ? Loc.Format("mods_plan_deps", PlanRow(first).Name)
                    : Loc.Get("mods_plan_trust"),
                Rows = plan.Select(PlanRow).ToList(),
                PrimaryText = Loc.Format("mods_plan_primary", FormatBytes(download)),
                DownloadBytes = download,
                NeededBytes = need,
                FreeBytes = free,
            });
            if (!ok)
                return null;
        }

        return EnqueueSteps(root, plan, reason);
    }

    IReadOnlyList<ModQueueItem> EnqueueSteps(string root, IReadOnlyList<ModInstallStep> plan, string reason)
    {
        var items = new List<ModQueueItem>(plan.Count);
        foreach (var step in plan)
        {
            var existing = _modQueue.FirstOrDefault(q =>
                q.IsActive && string.Equals(q.FullName, step.Package.FullName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                items.Add(existing);
                continue;
            }

            var item = new ModQueueItem(step, QueueKind(step), reason, IconFor(step.Package)) { Root = root };
            _modQueue.Add(item);
            items.Add(item);
        }

        SyncBrowseQueueStates();
        ApplyModsSection();
        UpdateQueueUi();
        SetModsStatus(Loc.Format("mods_q_added", items.Count));
        if (!_modQueueRunning)
            _ = RunModQueueAsync();
        return items;
    }

    async Task RunModQueueAsync()
    {
        if (_modQueueRunning)
            return;
        _modQueueRunning = true;
        try
        {
            while (_modQueue.FirstOrDefault(i => i.IsQueued) is { } next)
                await RunQueueItemAsync(next).ConfigureAwait(true);
        }
        finally
        {
            _modQueueRunning = false;
            UpdateQueueUi();
        }
    }

    async Task RunQueueItemAsync(ModQueueItem item)
    {
        var root = item.Root;
        var updating = false;
        try
        {
            updating = SafePath.TryJoin(ModsStore.ModsDirectory(root), item.FullName, out var dir) && Directory.Exists(dir);
        }
        catch
        {
            // Install reports the bad root.
        }

        if (updating && GameRunning(root))
        {
            item.ErrorText = Loc.Format("mods_q_game_running", item.Name);
            item.State = ModQueueState.Failed;
            item.Completion.TrySetResult(false);
            UpdateQueueUi();
            return;
        }

        using var cts = new CancellationTokenSource();
        item.Cts = cts;
        item.Started = item.LastSample = DateTime.UtcNow;
        item.Phase = 0;
        item.State = ModQueueState.Running;
        SyncBrowseQueueStates();
        UpdateQueueUi();

        var progress = new Progress<ContentInstallProgress>(p => OnQueueProgress(item, p));
        try
        {
            await ModsInstaller().InstallAsync(root, item.Step.Package, item.Version, progress, cts.Token)
                .ConfigureAwait(true);
            item.DownloadedBytes = item.SizeBytes;
            item.DoneText = Loc.Format(
                "mods_q_done",
                FormatBytes(item.SizeBytes),
                FormatEta(Math.Max(1, (DateTime.UtcNow - item.Started).TotalSeconds)));
            item.State = ModQueueState.Done;
            item.Completion.TrySetResult(true);
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            item.State = ModQueueState.Cancelled;
            item.Completion.TrySetResult(false);
        }
        catch (Exception ex)
        {
            Log("Mods: install of " + item.Pin + " failed: " + ex.Message);
            item.ErrorText = Loc.Format("mods_q_failed", ex.Message);
            item.State = ModQueueState.Failed;
            item.Completion.TrySetResult(false);
        }
        finally
        {
            item.Cts = null;
            SyncBrowseQueueStates();
            UpdateQueueUi();
        }

        await LoadInstalledModsCoreAsync().ConfigureAwait(true);
        RefreshDediModPolicyUi();
    }

    void OnQueueProgress(ModQueueItem item, ContentInstallProgress p)
    {
        if (!item.IsRunning)
            return;
        switch (p.Phase)
        {
            case "download":
                item.Phase = 0;
                if (p.Unit == ProgressUnit.Bytes && p.Current >= 0)
                {
                    var total = p.Total > 0 ? p.Total : item.SizeBytes;
                    var cur = p.Current;
                    item.DownloadedBytes = cur;
                    var now = DateTime.UtcNow;
                    var dt = (now - item.LastSample).TotalSeconds;
                    if (dt >= 0.25)
                    {
                        var inst = (cur - item.LastBytes) / dt;
                        item.RateEma = item.RateEma <= 0 ? inst : 0.3 * inst + 0.7 * item.RateEma;
                        item.LastBytes = cur;
                        item.LastSample = now;
                    }

                    item.Percent = total > 0 ? Math.Min(100, cur * 100.0 / total) : 0;
                    item.BytesLine = total > 0
                        ? Loc.Format("mods_q_of", FormatBytes(cur), FormatBytes(total))
                        : FormatBytes(cur);
                    item.RateText = item.RateEma > 0 ? FormatRate(item.RateEma) : string.Empty;
                    item.EtaText = item.RateEma > 0 && total > cur
                        ? Loc.Format("mods_q_left", FormatEta((total - cur) / item.RateEma))
                        : string.Empty;
                    item.DetailLine = "thunderstore.io";
                }

                break;
            case "extract":
                item.Phase = 1;
                item.DownloadedBytes = item.SizeBytes;
                item.Percent = p.Total > 0 ? Math.Min(100, p.Current * 100.0 / p.Total) : 0;
                item.BytesLine = p.JobTotal > 0
                    ? Loc.Format("mods_q_files", p.JobCurrent, p.JobTotal)
                    : string.Empty;
                item.RateText = string.Empty;
                item.EtaText = string.Empty;
                item.DetailLine = p.Message ?? string.Empty;
                break;
            case "check":
                item.Phase = 2;
                item.Percent = 100;
                item.BytesLine = string.Empty;
                item.DetailLine = string.Empty;
                break;
            case "commit":
                item.Phase = 3;
                item.Percent = 100;
                break;
        }

        SyncBrowseQueueStates();
        UpdateQueueUi();
    }

    void UpdateQueueUi()
    {
        var counted = _modQueue.Where(i => !i.IsCancelled).ToList();
        var active = _modQueue.Where(i => i.IsActive).ToList();
        var running = _modQueue.FirstOrDefault(i => i.IsRunning);
        var total = counted.Sum(i => i.SizeBytes);
        var done = counted.Sum(i => i.IsDone ? i.SizeBytes : Math.Min(i.DownloadedBytes, i.SizeBytes));
        var pct = total > 0 ? done * 100.0 / total : 0;
        var rate = running?.RateEma ?? 0;
        var remaining = Math.Max(0, active.Sum(i => i.SizeBytes - Math.Min(i.DownloadedBytes, i.SizeBytes)));
        var rateText = rate > 0 && running?.Phase == 0 ? FormatRate(rate) : "-";
        var etaText = rate > 0 && remaining > 0 ? FormatEta(remaining / rate) : "-";

        if (TxtQPercent is not null)
        {
            TxtQPercent.Text = $"{Math.Floor(pct):0}%";
            TxtQBytes.Text = Loc.Format("mods_q_of", FormatBytes(done), FormatBytes(total));
            TxtQRate.Text = rateText;
            TxtQEta.Text = etaText;
            var root = running?.Root ?? ModsInstallRoot();
            var free = ModsRootUsable(root) ? FreeBytes(root) : -1;
            TxtQDisk.Text = free >= 0
                ? Loc.Format("mods_q_drive", FormatBytes(free), FormatBytes(ModSpace.Needed(remaining)))
                : "-";
            BarQOverall.Value = pct;
            TxtQSummary.Text = Loc.Format("mods_q_summary", counted.Count(i => i.IsDone), counted.Count);
            BtnModsQueueCancelAll.IsEnabled = active.Count > 0;
            BtnModsQueueClear.IsEnabled = _modQueue.Any(i => !i.IsActive);
        }

        if (BarModQueueStrip is not null)
        {
            BarModQueueStrip.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var lead = running ?? active.FirstOrDefault();
            TxtQStripName.Text = lead is null ? string.Empty
                : Loc.Format("mods_q_strip_name", lead.Name, counted.Count(i => i.IsDone), counted.Count);
            TxtQStripStats.Text = FormatBytes(done) + " / " + FormatBytes(total) + "  ·  " + rateText + "  ·  " + etaText;
            BarQStrip.Value = pct;
        }

        UpdateModsSectionLabels();
    }

    void OnModQueueCancel(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ModQueueItem item })
            CancelQueueItem(item);
        UpdateQueueUi();
    }

    void CancelQueueItem(ModQueueItem item)
    {
        if (item.IsQueued)
        {
            item.State = ModQueueState.Cancelled;
            item.Completion.TrySetResult(false);
            SyncBrowseQueueStates();
        }
        else if (item.IsRunning)
        {
            try { item.Cts?.Cancel(); }
            catch { /* finished meanwhile */ }
        }
    }

    void OnModQueueCancelAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in _modQueue.ToList())
            CancelQueueItem(item);
        UpdateQueueUi();
    }

    void OnModQueueRetry(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModQueueItem item } || !item.CanRetry)
            return;
        item.ResetForRetry();
        SyncBrowseQueueStates();
        UpdateQueueUi();
        if (!_modQueueRunning)
            _ = RunModQueueAsync();
    }

    void OnModQueueCopy(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModQueueItem item })
            return;
        try
        {
            Clipboard.SetText(item.Pin + "\n" + item.ErrorText);
            SetModsStatus(Loc.Get("mods_q_copied"));
        }
        catch (Exception ex)
        {
            SetModsStatus(ex.Message);
        }
    }

    void OnModQueueClear(object sender, RoutedEventArgs e)
    {
        foreach (var item in _modQueue.Where(i => !i.IsActive).ToList())
            _modQueue.Remove(item);
        ApplyModsSection();
        UpdateQueueUi();
    }

    void OnModQueueStripDetails(object sender, RoutedEventArgs e)
    {
        _modsSection = ModsSection.Downloads;
        ApplySimpleTab(SimpleTab.Mods);
        _ = RefreshModsPanelAsync();
    }

    // ------------------------------------------------------------------ profiles

    void OnModsShare(object sender, RoutedEventArgs e)
    {
        if (PopModsShare is not null)
            PopModsShare.IsOpen = !PopModsShare.IsOpen;
    }

    void OnModsExport(object sender, RoutedEventArgs e) => _ = ExportModsProfileAsync();

    async Task ExportModsProfileAsync()
    {
        if (!ThunderstoreLive || _modsBusy)
            return;

        var pins = new List<string>();
        foreach (var row in _modRows)
        {
            if (!row.Enabled || !row.IsCatalog)
                continue;
            var ver = row.InstalledVersion;
            if (string.IsNullOrWhiteSpace(ver))
                continue;
            pins.Add(row.Mod.ThunderstoreFullName.Trim() + "-" + ver.Trim());
        }

        if (pins.Count == 0)
        {
            SetModsStatus(Loc.Get("mods_profile_empty"));
            return;
        }

        SetModsBusy(true);
        try
        {
            var code = await ModsThunderstore()
                .CreateProfileAsync(new ModProfile { Packages = pins })
                .ConfigureAwait(true);
            if (TxtModsProfile is not null)
                TxtModsProfile.Text = code;
            try
            {
                Clipboard.SetText(code);
            }
            catch (Exception ex)
            {
                SetModsStatus(Loc.Format("mods_export_failed", ex.Message));
                return;
            }

            SetModsStatus(Loc.Get("mods_profile_copied"));
        }
        catch (Exception ex)
        {
            SetModsStatus(Loc.Format("mods_export_failed", ex.Message));
        }
        finally
        {
            SetModsBusy(false);
        }
    }

    void OnModsImport(object sender, RoutedEventArgs e) => _ = ImportModsProfileAsync();

    async Task ImportModsProfileAsync()
    {
        if (!ThunderstoreLive || _modsBusy)
            return;

        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
        {
            SetModsStatus(Loc.Get("mods_no_install"));
            return;
        }

        var code = TxtModsProfile?.Text?.Trim() ?? string.Empty;
        if (code.Length == 0)
        {
            SetModsStatus(Loc.Get("mods_profile_invalid"));
            return;
        }

        if (PopModsShare is not null)
            PopModsShare.IsOpen = false;
        SetModsBusy(true);
        List<(ModPackage, string?)> roots;
        var unresolved = new List<string>();
        try
        {
            var profile = await ModsThunderstore().GetProfileAsync(code).ConfigureAwait(true);
            await EnsureCatalogAsync(force: false, reportErrors: false).ConfigureAwait(true);
            roots = new List<(ModPackage, string?)>();
            foreach (var pin in profile.Packages)
            {
                if (TryResolvePin(pin, out var package, out var version))
                    roots.Add((package, version));
                else
                    unresolved.Add(pin);
            }
        }
        catch (Exception ex)
        {
            SetModsStatus(Loc.Format("mods_import_failed", ex.Message));
            return;
        }
        finally
        {
            SetModsBusy(false);
        }

        if (roots.Count == 0)
        {
            SetModsStatus(Loc.Format("mods_import_partial", 0, string.Join(", ", unresolved)));
            return;
        }

        var items = await QueueCatalogInstallAsync(roots, Loc.Get("mods_reason_profile"), askAlways: true)
            .ConfigureAwait(true);
        if (items is null)
            return;
        SetModsStatus(unresolved.Count > 0
            ? Loc.Format("mods_import_partial", items.Count, string.Join(", ", unresolved))
            : Loc.Format("mods_import_ok", items.Count));
    }

    bool TryResolvePin(string pin, out ModPackage package, out string? version)
    {
        package = null!;
        version = null;
        if (string.IsNullOrWhiteSpace(pin))
            return false;

        if (!ModInstallPlanner.TrySplitPin(pin, out var full, out var ver))
            return false;
        var raw = pin.Trim();

        foreach (var pkg in _tsPackages)
        {
            if (string.Equals(pkg.FullName, full, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pkg.FullName, raw, StringComparison.OrdinalIgnoreCase))
            {
                package = pkg;
                version = ver;
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ join

    /// <summary>
    /// Brings the enabled mod set in line with a server before joining: one confirmation lists
    /// what gets downloaded, what turns on and what goes off for this match. Installs run through
    /// the download queue; the session filter is written after them so its restore keeps them.
    /// </summary>
    async Task<bool> EnsureJoinModsAsync(ServerListing listing)
    {
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
            return true;

        IReadOnlyList<InstalledMod> discovered;
        try
        {
            discovered = await Task.Run(() => ModsStore.Discover(root)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetBrowserStatus(ex.Message);
            return false;
        }

        var required = DistinctIds(listing.RequiredMods);
        var allowed = DistinctIds(listing.AllowedMods);
        var enabledNow = new HashSet<string>(
            discovered.Where(m => m.Enabled).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

        // Mods the player turned off come back only with consent, and only for this session.
        var turnedOff = required
            .Where(id => !enabledNow.Contains(id)
                         && discovered.Any(m => !m.Enabled && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var have = new HashSet<string>(enabledNow, StringComparer.OrdinalIgnoreCase);
        have.UnionWith(turnedOff);
        var missing = required.Where(id => !have.Contains(id)).ToList();

        IReadOnlyList<ModInstallStep> plan = Array.Empty<ModInstallStep>();
        if (missing.Count > 0)
        {
            var names = string.Join("\n", missing);
            if (!ThunderstoreLive)
            {
                MessageBox.Show(
                    this,
                    Loc.Format("mods_join_no_catalog", names),
                    Loc.Get("mods_join_unresolved_title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            var unresolved = new List<string>();
            try
            {
                SetBrowserStatus(Loc.Get("mods_join_profile"));
                await EnsureCatalogAsync(force: false, reportErrors: false).ConfigureAwait(true);
                plan = await PlanJoinInstallAsync(root, listing, missing, unresolved).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                SetBrowserStatus(Loc.Format("mods_install_failed", ex.Message));
                MessageBox.Show(
                    this,
                    Loc.Format("mods_install_failed", ex.Message),
                    Loc.Get("mods_join_missing_title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (unresolved.Count > 0 || plan.Count == 0)
            {
                if (unresolved.Count == 0)
                    unresolved.AddRange(missing);
                MessageBox.Show(
                    this,
                    Loc.Format("mods_join_unresolved", string.Join("\n", unresolved)),
                    Loc.Get("mods_join_unresolved_title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        var extras = new List<string>();
        if (allowed.Count > 0)
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            allowedSet.UnionWith(required);
            extras = have.Where(id => !allowedSet.Contains(id)).ToList();
        }

        if (turnedOff.Count == 0 && plan.Count == 0 && extras.Count == 0)
            return true;

        string NameOf(string id) =>
            discovered.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) is { } m
            && !string.IsNullOrWhiteSpace(m.Name) ? m.Name : id;

        var rows = new List<ModPlanRow>();
        rows.AddRange(plan.Select(PlanRow));
        rows.AddRange(turnedOff.Select(id => new ModPlanRow
        {
            Name = NameOf(id),
            Detail = Loc.Get("mods_join_row_off"),
            Tag = Loc.Get("mods_tag_turn_on"),
            TagKind = "on",
            SizeText = "-",
        }));
        rows.AddRange(extras.Select(id => new ModPlanRow
        {
            Name = NameOf(id),
            Detail = Loc.Get("mods_join_row_extra"),
            Tag = Loc.Get("mods_tag_off_match"),
            TagKind = "off",
            SizeText = "-",
        }));

        var download = plan.Sum(s => Math.Max(0, s.Version.FileSize));
        var server = string.IsNullOrWhiteSpace(listing.Name) ? Loc.Get("join_server") : listing.Name;
        var ok = ModPlanWindow.Ask(this, new ModPlanSpec
        {
            Kicker = Loc.Get("mods_join_kicker"),
            Headline = Loc.Format("mods_join_headline", server),
            Body = plan.Count > 0 ? Loc.Get("mods_plan_trust") : Loc.Get("mods_join_session_body"),
            Rows = rows,
            PrimaryText = plan.Count > 0
                ? Loc.Format("mods_join_primary_dl", FormatBytes(download))
                : Loc.Get("mods_join_primary"),
            DownloadBytes = plan.Count > 0 ? download : -1,
            NeededBytes = ModSpace.Needed(download),
            FreeBytes = FreeBytes(root),
        });
        if (!ok)
        {
            SetBrowserStatus(Loc.Get("mods_join_cancelled"));
            return false;
        }

        if (plan.Count > 0)
        {
            var items = EnqueueSteps(root, plan, Loc.Get("mods_reason_server"));
            SetBrowserStatus(Loc.Format("mods_join_waiting", items.Count));
            var results = await Task.WhenAll(items.Select(i => i.Completion.Task)).ConfigureAwait(true);
            if (results.Any(r => !r))
            {
                SetBrowserStatus(Loc.Get("mods_join_install_failed"));
                MessageBox.Show(
                    this,
                    Loc.Get("mods_join_install_failed"),
                    Loc.Get("mods_join_missing_title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            var nowEnabled = new HashSet<string>(
                ModsStore.Discover(root).Where(m => m.Enabled).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            nowEnabled.UnionWith(turnedOff);
            var stillMissing = required.Where(id => !nowEnabled.Contains(id)).ToList();
            if (stillMissing.Count > 0)
            {
                MessageBox.Show(
                    this,
                    Loc.Format("mods_join_unresolved", string.Join("\n", stillMissing)),
                    Loc.Get("mods_join_unresolved_title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            _sessionModsForceRelaunch = true;
        }

        if (turnedOff.Count > 0 || extras.Count > 0)
        {
            var keep = new HashSet<string>(
                ModsStore.Discover(root).Where(m => m.Enabled).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            keep.UnionWith(turnedOff);
            keep.ExceptWith(extras);
            if (!TryBeginSessionModsFilter(root, keep))
                return false;
            _sessionModsForceRelaunch = true;
        }

        return true;
    }

    /// <summary>
    /// Resolves a server's missing mod ids (or its pinned profile) to exact catalog
    /// packages. Nothing is downloaded here; the player confirms the plan first.
    /// </summary>
    async Task<IReadOnlyList<ModInstallStep>> PlanJoinInstallAsync(
        string root,
        ServerListing listing,
        List<string> missing,
        List<string> unresolved)
    {
        if (_tsPackages.Count == 0)
            _tsPackages = await ModsThunderstore().ListPackagesAsync().ConfigureAwait(true);

        var roots = new List<(ModPackage, string?)>();
        if (!string.IsNullOrWhiteSpace(listing.ModsProfile))
        {
            var profile = await ModsThunderstore().GetProfileAsync(listing.ModsProfile.Trim())
                .ConfigureAwait(true);
            // The profile only chooses versions for mods the server requires; any
            // other pin it lists is not the server's to install.
            var wanted = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
            foreach (var pin in profile.Packages)
            {
                if (!TryResolvePin(pin, out var package, out var version))
                    continue;
                if (wanted.Contains(ModInstaller.ExpectedCatalogId(package)))
                    roots.Add((package, version));
                else
                    Log("Join: ignoring profile pin not required by the server: " + pin);
            }

            var covered = new HashSet<string>(
                roots.Select(r => ModInstaller.ExpectedCatalogId(r.Item1)), StringComparer.OrdinalIgnoreCase);
            foreach (var id in missing)
            {
                if (!covered.Contains(id))
                    unresolved.Add(id);
            }
        }
        else
        {
            foreach (var id in missing)
            {
                var package = MatchPackageById(id);
                if (package is null)
                    unresolved.Add(id);
                else
                    roots.Add((package, null));
            }
        }

        return ModInstallPlanner.Plan(
            _tsPackages, roots, ModsStore.Discover(root), unresolved, keepInstalledRoots: true);
    }

    /// <summary>A catalog mod id is <c>Owner.Name</c>; only that exact package satisfies it.</summary>
    ModPackage? MatchPackageById(string id)
    {
        foreach (var pkg in _tsPackages)
        {
            if (pkg.IsDeprecated)
                continue;
            if (string.Equals(ModInstaller.ExpectedCatalogId(pkg), id, StringComparison.OrdinalIgnoreCase))
                return pkg;
        }

        return null;
    }

    // Engine MAX_MODS_TO_LOAD.
    const int MaxModsToLoad = 1024;

    static List<string> DistinctIds(IReadOnlyList<string>? ids)
    {
        var result = new List<string>();
        if (ids is null)
            return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ids)
        {
            // Server-supplied; the game loads at most MaxModsToLoad mods anyway.
            if (result.Count >= MaxModsToLoad)
                break;
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id) || !ModId.IsValid(id) || !seen.Add(id))
                continue;
            result.Add(id);
        }

        return result;
    }

    bool TryBeginSessionModsFilter(string root, HashSet<string> keepEnabled)
    {
        RestoreSessionMods(force: true);

        string modsDir;
        try
        {
            modsDir = ModsStore.ModsDirectory(root);
        }
        catch (Exception ex)
        {
            SetBrowserStatus(ex.Message);
            return false;
        }

        if (!SafePath.TryJoin(modsDir, ModsStore.ModListFileName, out var vdfPath))
        {
            SetBrowserStatus(Loc.Get("mods_session_path"));
            return false;
        }

        if (!SafePath.TryJoin(modsDir, SessionModsBackupName, out var bakPath))
        {
            SetBrowserStatus(Loc.Get("mods_session_path"));
            return false;
        }

        try
        {
            Directory.CreateDirectory(modsDir);
            var original = File.Exists(vdfPath) ? File.ReadAllText(vdfPath) : string.Empty;
            File.WriteAllText(bakPath, original);

            var current = ModsStore.Discover(root);
            var next = new List<(string Id, bool Enabled)>(current.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in current)
            {
                if (!seen.Add(mod.Id))
                    continue;
                next.Add((mod.Id, keepEnabled.Contains(mod.Id)));
            }

            foreach (var id in keepEnabled)
            {
                if (seen.Add(id) && ModId.IsValid(id))
                    next.Add((id, true));
            }

            ModsStore.SetModList(root, next);

            _sessionModsWritten = File.Exists(vdfPath) ? File.ReadAllText(vdfPath) : string.Empty;
            _sessionModsBackup = bakPath;
            _sessionModsInstall = root;
            _sessionModsPending = true;
            _sessionModsClientSeen = false;
            return true;
        }
        catch (Exception ex)
        {
            SetBrowserStatus(ex.Message);
            try
            {
                if (File.Exists(bakPath) && SafePath.TryJoin(modsDir, ModsStore.ModListFileName, out var restoreTo))
                    File.Copy(bakPath, restoreTo, overwrite: true);
            }
            catch { /* best-effort */ }
            return false;
        }
    }

    void WatchSessionModsClient()
    {
        var root = string.IsNullOrEmpty(_sessionModsInstall)
            ? ModsInstallRoot()
            : _sessionModsInstall;
        var alive = !string.IsNullOrWhiteSpace(root)
                    && ProcessSpawner.IsRoleAlive(LaunchRole.Client, root);
        WatchSessionModsClient(alive);
    }

    void WatchSessionModsClient(bool clientAliveHint)
    {
        if (!_sessionModsPending || _joinBusy)
            return;

        var root = string.IsNullOrEmpty(_sessionModsInstall)
            ? ModsInstallRoot()
            : _sessionModsInstall;
        var box = ReadInstallPathBox();
        var alive = string.Equals(root, box, StringComparison.OrdinalIgnoreCase)
            ? clientAliveHint
            : !string.IsNullOrWhiteSpace(root)
                && ProcessSpawner.IsRoleAlive(LaunchRole.Client, root);
        if (alive)
        {
            _sessionModsClientSeen = true;
            return;
        }

        if (_sessionModsClientSeen)
            RestoreSessionMods(force: false);
    }

    void RestoreSessionMods(bool force)
    {
        if (!_sessionModsPending && !force)
            return;
        if (!_sessionModsPending)
            return;

        var root = _sessionModsInstall;
        var bak = _sessionModsBackup;
        var written = _sessionModsWritten;
        _sessionModsPending = false;
        _sessionModsClientSeen = false;
        _sessionModsInstall = string.Empty;
        _sessionModsWritten = string.Empty;
        _sessionModsBackup = string.Empty;

        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(bak))
            return;

        try
        {
            var modsDir = ModsStore.ModsDirectory(root);
            if (!SafePath.TryJoin(modsDir, ModsStore.ModListFileName, out var vdfPath))
                return;
            if (!File.Exists(bak))
                return;

            if (File.Exists(vdfPath))
            {
                var current = File.ReadAllText(vdfPath);
                if (!string.Equals(current, written, StringComparison.Ordinal))
                {
                    Log("Join mods: leaving mods.vdf in place; it changed after the session filter.");
                    TryDeleteFile(bak);
                    return;
                }
            }

            File.Copy(bak, vdfPath, overwrite: true);
            TryDeleteFile(bak);
            Log("Join mods: restored mods.vdf after the session.");
        }
        catch (Exception ex)
        {
            Log("Join mods restore failed: " + ex.Message);
        }
    }

    static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* best-effort */ }
    }

    void RefreshDediModPolicyUi()
    {
        if (ListDediModPolicy is null)
            return;

        var root = ModsInstallRoot();
        _suppressDediModEvents = true;
        try
        {
            _dediModRows.Clear();
            _dediModPolicyLoaded = false;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                ListDediModPolicy.ItemsSource = null;
                if (CmbModPolicy is not null)
                    CmbModPolicy.SelectedIndex = 0;
                _dediModPolicy = 0;
                return;
            }

            IReadOnlyList<InstalledMod> found;
            IReadOnlyList<(string Id, bool Enabled)> required;
            IReadOnlyList<(string Id, bool Enabled)> allowed;
            try
            {
                found = ModsStore.Discover(root);
                required = ModsStore.ReadRequiredMods(root);
                allowed = ModsStore.ReadAllowedMods(root);
            }
            catch
            {
                ListDediModPolicy.ItemsSource = null;
                return;
            }

            var reqSet = new HashSet<string>(
                required.Where(r => r.Enabled).Select(r => r.Id),
                StringComparer.OrdinalIgnoreCase);
            var allowSet = new HashSet<string>(
                allowed.Where(r => r.Enabled).Select(r => r.Id),
                StringComparer.OrdinalIgnoreCase);

            foreach (var mod in found)
            {
                var index = 0;
                if (reqSet.Contains(mod.Id))
                    index = 1;
                else if (allowSet.Contains(mod.Id))
                    index = 2;
                _dediModRows.Add(new DediModPolicyRowViewModel(mod, index));
            }

            ListDediModPolicy.ItemsSource = null;
            ListDediModPolicy.ItemsSource = _dediModRows;
            _dediModPolicyLoaded = true;

            var stored = ReadStoredModPolicy(root);
            if (stored is int explicitPolicy)
                _dediModPolicy = explicitPolicy;
            else if (allowSet.Count > 0)
                _dediModPolicy = 2;
            else if (reqSet.Count > 0)
                _dediModPolicy = 1;
            else
                _dediModPolicy = 0;

            if (CmbModPolicy is not null
                && CmbModPolicy.SelectedIndex != _dediModPolicy
                && _dediModPolicy is >= 0 and <= 2)
                CmbModPolicy.SelectedIndex = _dediModPolicy;
        }
        finally
        {
            _suppressDediModEvents = false;
        }
    }

    void OnDediModPolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressDediModEvents)
            return;
        if (CmbModPolicy is null)
            return;
        var idx = CmbModPolicy.SelectedIndex;
        _dediModPolicy = idx is >= 0 and <= 2 ? idx : 0;
        PersistDediModPolicy();
        RefreshArgPreviews();
    }

    void OnDediModRowPolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressDediModEvents)
            return;
        if (sender is not ComboBox { Tag: DediModPolicyRowViewModel row })
            return;
        row.PolicyIndex = ((ComboBox)sender).SelectedIndex;
        PersistDediModPolicy();
        RefreshArgPreviews();
    }

    void PersistDediModPolicy()
    {
        if (!_dediModPolicyLoaded)
            return;
        var root = ModsInstallRoot();
        if (string.IsNullOrWhiteSpace(root))
            return;

        var required = new List<(string Id, bool Enabled)>();
        var allowed = new List<(string Id, bool Enabled)>();
        foreach (var row in _dediModRows)
        {
            if (!ModId.IsValid(row.Id))
                continue;
            if (row.PolicyIndex == 1)
            {
                required.Add((row.Id, true));
                allowed.Add((row.Id, true));
            }
            else if (row.PolicyIndex == 2)
            {
                allowed.Add((row.Id, true));
            }
        }

        required = CapPolicyList(required);
        allowed = CapPolicyList(allowed);

        try
        {
            ModsStore.WriteRequiredMods(root, required);
            ModsStore.WriteAllowedMods(root, allowed);
            WriteStoredModPolicy(root, _dediModPolicy);
        }
        catch (Exception ex)
        {
            Log("Mod policy save failed: " + ex.Message);
        }
    }

    static List<(string Id, bool Enabled)> CapPolicyList(List<(string Id, bool Enabled)> rows)
    {
        var result = new List<(string Id, bool Enabled)>(Math.Min(rows.Count, MaxModPolicyEntries));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (result.Count >= MaxModPolicyEntries)
                break;
            if (!ModId.IsValid(row.Id) || !seen.Add(row.Id))
                continue;
            result.Add(row);
        }

        return result;
    }

    int? ReadStoredModPolicy(string installPath)
    {
        try
        {
            var modsDir = ModsStore.ModsDirectory(installPath);
            if (!SafePath.TryJoin(modsDir, ModPolicyFileName, out var path) || !File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            if (int.TryParse(text, out var n) && n is >= 0 and <= 2)
                return n;
        }
        catch { /* infer from lists */ }
        return null;
    }

    void WriteStoredModPolicy(string installPath, int policy)
    {
        if (policy is < 0 or > 2)
            policy = 0;
        try
        {
            var modsDir = ModsStore.ModsDirectory(installPath);
            Directory.CreateDirectory(modsDir);
            if (!SafePath.TryJoin(modsDir, ModPolicyFileName, out var path))
                return;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, policy.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log("Mod policy file write failed: " + ex.Message);
        }
    }

    IReadOnlyList<string> BuildDediModPolicyTokens()
    {
        var required = new List<string>();
        var allowed = new List<string>();
        if (_dediModRows.Count == 0)
            RefreshDediModPolicyUi();

        foreach (var row in _dediModRows)
        {
            if (!ModId.IsValid(row.Id))
                continue;
            if (row.PolicyIndex == 1)
            {
                required.Add(row.Id);
                allowed.Add(row.Id);
            }
            else if (row.PolicyIndex == 2)
            {
                allowed.Add(row.Id);
            }
        }

        required = SanitizeModCsvIds(required);
        allowed = SanitizeModCsvIds(allowed);
        var policy = _dediModPolicy is >= 0 and <= 2 ? _dediModPolicy : 0;

        var tokens = new List<string> { "+sv_modPolicy", policy.ToString() };
        if (required.Count > 0)
        {
            tokens.Add("+sv_requiredMods");
            tokens.Add(string.Join(",", required));
        }

        if (allowed.Count > 0)
        {
            tokens.Add("+sv_allowedMods");
            tokens.Add(string.Join(",", allowed));
        }

        return tokens;
    }

    static List<string> SanitizeModCsvIds(IEnumerable<string> ids)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ids)
        {
            if (result.Count >= MaxModPolicyEntries)
                break;
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id) || !ModId.IsValid(id) || !seen.Add(id))
                continue;
            result.Add(id);
        }

        return result;
    }
}
