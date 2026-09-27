using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    readonly List<ModRowViewModel> _modRows = new();
    readonly List<BrowseModRowViewModel> _browseRows = new();
    readonly List<DediModPolicyRowViewModel> _dediModRows = new();
    IReadOnlyList<ModPackage> _tsPackages = Array.Empty<ModPackage>();

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

    static string ModPlaylistSourcesKey(IReadOnlyList<ModPlaylistSource> mods) =>
        string.Join("|", mods.Select(m => m.Id + ":" + string.Join(",", m.Maps)));

    void ReloadCatalogIfModsChanged()
    {
        if (ModPlaylistSourcesKey(ModPlaylistSources(ModsInstallRoot())) != _catalogModsKey)
            ReloadPlaylistsAndMaps(selectSaved: true);
    }
    ThunderstoreClient? _thunderstore;
    ModInstaller? _modInstaller;
    bool _modsSectionBrowse;
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

    void OnModsSectionInstalled(object sender, RoutedEventArgs e)
    {
        _modsSectionBrowse = false;
        ApplyModsSection();
    }

    void OnModsSectionBrowse(object sender, RoutedEventArgs e)
    {
        if (!ThunderstoreLive)
            return;
        _modsSectionBrowse = true;
        ApplyModsSection();
        if (_browseRows.Count == 0 && !_modsBusy)
            _ = RefreshBrowseModsAsync();
    }

    void ApplyModsSection()
    {
        if (!ThunderstoreLive)
            _modsSectionBrowse = false;

        var catalog = ThunderstoreLive ? Visibility.Visible : Visibility.Collapsed;
        if (PanelModsSections is not null)
            PanelModsSections.Visibility = catalog;
        if (PanelModsProfile is not null)
            PanelModsProfile.Visibility = catalog;

        StyleTab(BtnModsSectionInstalled, !_modsSectionBrowse);
        StyleTab(BtnModsSectionBrowse, _modsSectionBrowse);
        if (PanelModsInstalled is not null)
            PanelModsInstalled.Visibility = _modsSectionBrowse ? Visibility.Collapsed : Visibility.Visible;
        if (PanelModsBrowse is not null)
            PanelModsBrowse.Visibility = _modsSectionBrowse ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnModsSearchChanged(object sender, TextChangedEventArgs e) => BindInstalledMods();

    void OnModsBrowseSearchChanged(object sender, TextChangedEventArgs e) => BindBrowseMods();

    void OnModsRefresh(object sender, RoutedEventArgs e)
    {
        if (_modsSectionBrowse)
            _ = RefreshBrowseModsAsync();
        else
            _ = RefreshInstalledModsAsync();
    }

    async Task RefreshModsPanelAsync()
    {
        ApplyModsSection();
        await RefreshInstalledModsAsync().ConfigureAwait(true);
        RefreshDediModPolicyUi();
    }

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
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _modRows.Clear();
            BindInstalledMods();
            SetModsStatus(Loc.Get("mods_no_install"));
            return;
        }

        try
        {
            var skipped = new List<string>();
            var found = await Task.Run(() => ModsStore.Discover(root, skipped)).ConfigureAwait(true);
            var latestByFull = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var latestByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pkg in _tsPackages)
            {
                var ver = pkg.Versions is { Count: > 0 } ? pkg.Versions[0].VersionNumber : string.Empty;
                if (string.IsNullOrEmpty(ver))
                    continue;
                if (!string.IsNullOrEmpty(pkg.FullName))
                    latestByFull[pkg.FullName] = ver;
                if (!string.IsNullOrEmpty(pkg.Name) && !latestByName.ContainsKey(pkg.Name))
                    latestByName[pkg.Name] = ver;
            }

            _modRows.Clear();
            foreach (var mod in found)
            {
                string? latest = null;
                if (!string.IsNullOrEmpty(mod.ThunderstoreFullName)
                    && latestByFull.TryGetValue(mod.ThunderstoreFullName, out var byFull))
                    latest = byFull;
                else if (latestByName.TryGetValue(mod.Id, out var byId))
                    latest = byId;
                else if (latestByName.TryGetValue(mod.Name, out var byName))
                    latest = byName;
                _modRows.Add(new ModRowViewModel(mod, latest));
            }

            BindInstalledMods();
            if (skipped.Count > 0)
                SetModsStatus(Loc.Format("mods_status_skipped", skipped.Count));
            else if (_modRows.Count == 0)
                SetModsStatus(Loc.Get("mods_empty"));
            else
                SetModsStatus(string.Empty);

            ReloadCatalogIfModsChanged();
        }
        catch (Exception ex)
        {
            SetModsStatus(ex.Message);
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
            rows = _modRows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = rows.ToList();
        _suppressModEvents = true;
        ListModsInstalled.ItemsSource = null;
        ListModsInstalled.ItemsSource = list;
        _suppressModEvents = false;
    }

    async Task RefreshBrowseModsAsync()
    {
        if (!ThunderstoreLive || _modsBusy)
            return;

        SetModsBusy(true);
        SetModsStatus(Loc.Get("lb_refreshing"));
        try
        {
            var packages = await ModsThunderstore().ListPackagesAsync().ConfigureAwait(true);
            _tsPackages = packages;
            _browseRows.Clear();
            foreach (var pkg in packages)
            {
                if (pkg.IsDeprecated || pkg.IsNsfw)
                    continue;
                _browseRows.Add(new BrowseModRowViewModel(pkg));
            }

            BindBrowseMods();
            if (_browseRows.Count == 0)
                SetModsStatus(Loc.Get("mods_browse_empty"));
            else
                SetModsStatus(string.Empty);

            await LoadInstalledModsCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetModsStatus(Loc.Format("mods_browse_failed", ex.Message));
        }
        finally
        {
            SetModsBusy(false);
        }
    }

    void BindBrowseMods()
    {
        if (ListModsBrowse is null)
            return;

        var q = TxtModsBrowseSearch?.Text?.Trim() ?? string.Empty;
        IEnumerable<BrowseModRowViewModel> rows = _browseRows;
        if (q.Length > 0)
        {
            rows = _browseRows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Owner.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.FullName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        ListModsBrowse.ItemsSource = null;
        ListModsBrowse.ItemsSource = rows.ToList();
    }

    void OnModEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressModEvents)
            return;
        if (sender is not CheckBox { Tag: ModRowViewModel row })
            return;

        var wanted = row.Enabled;
        if (sender is CheckBox box)
            wanted = box.IsChecked == true;

        var root = ModsInstallRoot();
        try
        {
            ModsStore.SetEnabled(root, row.Id, wanted);
            _suppressModEvents = true;
            row.Enabled = wanted;
            _suppressModEvents = false;
            ReloadCatalogIfModsChanged();
        }
        catch (Exception ex)
        {
            _suppressModEvents = true;
            row.Enabled = !wanted;
            if (sender is CheckBox cb)
                cb.IsChecked = !wanted;
            _suppressModEvents = false;
            SetModsStatus(Loc.Format("mods_enable_failed", ex.Message));
        }
    }

    void OnModMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModRowViewModel row })
            return;
        MoveMod(row, -1);
    }

    void OnModMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModRowViewModel row })
            return;
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

    void OnModUninstall(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModRowViewModel row })
            return;
        _ = UninstallModAsync(row);
    }

    async Task UninstallModAsync(ModRowViewModel row)
    {
        if (_modsBusy || row.Busy)
            return;

        var confirm = MessageBox.Show(
            this,
            Loc.Format("mods_uninstall_confirm", row.Name),
            Loc.Get("mods_uninstall_title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        var root = ModsInstallRoot();
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
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
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
            var progress = NewModsProgress(null);
            var zip = dlg.FileName;
            await ModsInstaller().InstallFromZipAsync(root, zip, folderName: null, progress)
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

    void OnBrowseModInstall(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BrowseModRowViewModel row })
            return;
        _ = InstallBrowseModAsync(row);
    }

    async Task InstallBrowseModAsync(BrowseModRowViewModel row)
    {
        if (!ThunderstoreLive || _modsBusy || row.Busy)
            return;

        var root = ModsInstallRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            SetModsStatus(Loc.Get("mods_no_install"));
            return;
        }

        var unresolved = new List<string>();
        IReadOnlyList<ModInstallStep> plan;
        try
        {
            plan = ModInstallPlanner.Plan(
                _tsPackages, new[] { (row.Package, (string?)null) }, ModsStore.Discover(root), unresolved);
        }
        catch (Exception ex)
        {
            SetModsStatus(Loc.Format("mods_install_failed", ex.Message));
            return;
        }

        if (unresolved.Count > 0)
        {
            SetModsStatus(Loc.Format("mods_install_failed",
                Loc.Format("mods_deps_unresolved", string.Join(", ", unresolved))));
            return;
        }

        if (plan.Count > 1)
        {
            var ask = MessageBox.Show(
                this,
                Loc.Format("mods_install_plan", row.Name, PlanLines(plan)),
                Loc.Get("mods_install_plan_title"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (ask != MessageBoxResult.OK)
                return;
        }

        row.Busy = true;
        SetModsBusy(true);
        SetModsStatus(Loc.Format("mods_join_installing", row.Name));
        try
        {
            var progress = NewModsProgress(row);
            await InstallPlanAsync(root, plan, progress, SetModsStatus).ConfigureAwait(true);
            row.ProgressText = string.Empty;
            await LoadInstalledModsCoreAsync().ConfigureAwait(true);
            RefreshDediModPolicyUi();

            var installedId = ModInstaller.ExpectedCatalogId(row.Package);
            var replaces = _modRows
                .Where(r => string.Equals(r.Id, installedId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.Mod.Replaces)
                .ToList();
            SetModsStatus(replaces.Count == 0
                ? Loc.Format("mods_install_ok_named", row.Name)
                : Loc.Format("mods_install_ok_replaces", row.Name, string.Join(", ", replaces)));
        }
        catch (Exception ex)
        {
            row.ProgressText = string.Empty;
            SetModsStatus(Loc.Format("mods_install_failed", ex.Message));
        }
        finally
        {
            row.Busy = false;
            SetModsBusy(false);
        }
    }

    async Task InstallPlanAsync(
        string root,
        IReadOnlyList<ModInstallStep> plan,
        IProgress<ContentInstallProgress>? progress,
        Action<string> status)
    {
        foreach (var step in plan)
        {
            status(Loc.Format("mods_join_installing", step.Pin));
            await ModsInstaller().InstallAsync(root, step.Package, step.Version.VersionNumber, progress)
                .ConfigureAwait(true);
        }
    }

    static string PlanLines(IReadOnlyList<ModInstallStep> plan) =>
        string.Join("\n", plan.Select(p => p.Pin));

    IProgress<ContentInstallProgress> NewModsProgress(BrowseModRowViewModel? row) =>
        new Progress<ContentInstallProgress>(p =>
        {
            var msg = string.IsNullOrWhiteSpace(p.Message) ? p.Phase : p.Message;
            if (row is not null)
                row.ProgressText = msg;
            if (!string.IsNullOrEmpty(msg))
                SetModsStatus(msg);
        });

    void OnModsExport(object sender, RoutedEventArgs e) => _ = ExportModsProfileAsync();

    async Task ExportModsProfileAsync()
    {
        if (!ThunderstoreLive || _modsBusy)
            return;

        var pins = new List<string>();
        foreach (var row in _modRows)
        {
            if (!row.Enabled)
                continue;
            var full = row.Mod.ThunderstoreFullName;
            var ver = string.IsNullOrWhiteSpace(row.Mod.ThunderstoreVersion)
                ? row.Mod.Version
                : row.Mod.ThunderstoreVersion;
            if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(ver))
                continue;
            pins.Add(full.Trim() + "-" + ver.Trim());
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
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
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

        SetModsBusy(true);
        try
        {
            var profile = await ModsThunderstore().GetProfileAsync(code).ConfigureAwait(true);
            if (_tsPackages.Count == 0)
            {
                try
                {
                    _tsPackages = await ModsThunderstore().ListPackagesAsync().ConfigureAwait(true);
                }
                catch
                {
                    // Match pins against whatever listing we already have.
                }
            }

            var unresolved = new List<string>();
            var roots = new List<(ModPackage, string?)>();
            foreach (var pin in profile.Packages)
            {
                if (!TryResolvePin(pin, out var package, out var version))
                {
                    unresolved.Add(pin);
                    continue;
                }

                roots.Add((package, version));
            }

            var plan = ModInstallPlanner.Plan(_tsPackages, roots, ModsStore.Discover(root), unresolved);
            await InstallPlanAsync(root, plan, progress: null, SetModsStatus).ConfigureAwait(true);
            var installed = plan.Count;

            await LoadInstalledModsCoreAsync().ConfigureAwait(true);
            RefreshDediModPolicyUi();
            if (unresolved.Count > 0)
            {
                SetModsStatus(Loc.Format(
                    "mods_import_partial",
                    installed,
                    string.Join(", ", unresolved)));
            }
            else
                SetModsStatus(Loc.Format("mods_import_ok", installed));
        }
        catch (Exception ex)
        {
            SetModsStatus(Loc.Format("mods_import_failed", ex.Message));
        }
        finally
        {
            SetModsBusy(false);
        }
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

    async Task<bool> EnsureJoinModsAsync(ServerListing listing)
    {
        var root = ModsInstallRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
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

        // Mods the player turned off come back only with consent, and only for this session.
        var turnedOff = required
            .Where(id => discovered.Any(m => !m.Enabled && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (turnedOff.Count > 0)
        {
            var enableAsk = MessageBox.Show(
                this,
                Loc.Format("mods_join_enable_disabled", string.Join("\n", turnedOff)),
                Loc.Get("mods_join_missing_title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (enableAsk != MessageBoxResult.Yes)
                return false;

            var keep = new HashSet<string>(
                discovered.Where(m => m.Enabled).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            keep.UnionWith(turnedOff);
            if (!TryBeginSessionModsFilter(root, keep))
                return false;
            _sessionModsForceRelaunch = true;
        }

        IReadOnlyList<string> enabled;
        try
        {
            enabled = ModsStore.EnabledIds(root);
        }
        catch (Exception ex)
        {
            SetBrowserStatus(ex.Message);
            return false;
        }

        var enabledSet = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(id => !enabledSet.Contains(id)).ToList();

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

            List<string> unresolved = new();
            IReadOnlyList<ModInstallStep> plan;
            try
            {
                SetBrowserStatus(Loc.Get("mods_join_profile"));
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

            var ask = MessageBox.Show(
                this,
                Loc.Format("mods_join_plan", names, PlanLines(plan)),
                Loc.Get("mods_join_missing_title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (ask != MessageBoxResult.Yes)
                return false;

            try
            {
                await InstallPlanAsync(root, plan, progress: null, SetBrowserStatus).ConfigureAwait(true);
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

            var nowEnabled = new HashSet<string>(ModsStore.EnabledIds(root), StringComparer.OrdinalIgnoreCase);
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

        if (allowed.Count == 0)
            return true;

        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        foreach (var id in required)
            allowedSet.Add(id);

        try
        {
            enabled = ModsStore.EnabledIds(root);
        }
        catch (Exception ex)
        {
            SetBrowserStatus(ex.Message);
            return false;
        }

        var extras = enabled.Where(id => !allowedSet.Contains(id)).ToList();
        if (extras.Count == 0)
            return true;

        var filterAsk = MessageBox.Show(
            this,
            Loc.Format("mods_join_filter", string.Join("\n", extras)),
            Loc.Get("mods_join_filter_title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (filterAsk != MessageBoxResult.Yes)
            return false;

        if (!TryBeginSessionModsFilter(root, allowedSet))
            return false;

        _sessionModsForceRelaunch = true;
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
