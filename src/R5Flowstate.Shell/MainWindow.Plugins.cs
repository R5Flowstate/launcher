using R5Flowstate.Content;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public sealed class PluginRowViewModel
{
    public CuratedPlugin? Plugin { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Meta { get; init; } = string.Empty;
    public string Caps { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string NativeNote { get; init; } = string.Empty;
    public bool ShowNativeNote { get; init; }
    public bool ShowInstall { get; init; }
    public bool ShowEnable { get; init; }
    public bool ShowDisable { get; init; }
    public bool ShowRemove { get; init; }
    public bool Removed { get; init; }
}

public partial class MainWindow
{
    bool _pluginsBusy;
    bool _sessionPluginsForceRelaunch;

    void OnPluginsRefresh(object sender, System.Windows.RoutedEventArgs e) => _ = RefreshPluginsAsync();

    void OnPluginInstall(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: PluginRowViewModel row })
            _ = InstallPluginRowAsync(row);
    }

    void OnPluginEnable(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: PluginRowViewModel row })
            _ = SetPluginEnabledAsync(row, enabled: true);
    }

    void OnPluginDisable(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: PluginRowViewModel row })
            _ = SetPluginEnabledAsync(row, enabled: false);
    }

    void OnPluginRemove(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: PluginRowViewModel row })
            _ = RemovePluginRowAsync(row);
    }

    async Task<bool> RefreshPluginsForPlayAsync(string root)
    {
        if (!ModsRootUsable(root))
            return true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await CuratedCatalog.LoadAsync(root, refresh: true, cts.Token,
                log: line => Log("Plugins: " + line)).ConfigureAwait(true);
            return true;
        }
        catch (CuratedListException ex) when (ex.Code == "plugin_list_copy")
        {
            Log("Plugins: " + ex.Message);
            var text = Loc.Format("plugins_status_failed", ex.Message);
            SetPluginsStatus(text);
            if (_settings.SimpleMode)
                SetSimpleStatus(text);
            else
            {
                SetError(text);
                System.Windows.MessageBox.Show(this, text, Loc.Get("tab_plugins"),
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }

            return false;
        }
        catch (Exception ex)
        {
            Log("Plugins: list refresh failed: " + ex.Message);
            return true;
        }
    }

    async Task RefreshPluginsAsync()
    {
        if (ListPlugins is null || _pluginsBusy)
            return;
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
        {
            SetPluginsStatus(Loc.Get("status_install_first"));
            return;
        }

        _pluginsBusy = true;
        try
        {
            var list = await CuratedCatalog.LoadAsync(root, refresh: true, CancellationToken.None,
                log: line => Log("Plugins: " + line)).ConfigureAwait(true);
            var enabled = new HashSet<string>(PluginEnablement.Read(root), StringComparer.Ordinal);
            var rows = new List<PluginRowViewModel>();
            var shown = new HashSet<string>(StringComparer.Ordinal);
            foreach (var plugin in list.Plugins.OrderBy(p => p.Id, StringComparer.Ordinal)
                         .ThenByDescending(p => p.Version, Comparer<string>.Create(ModVersion.Compare)))
            {
                var pin = plugin.Id + "@" + plugin.Version;
                shown.Add(pin);
                var removed = list.IsPluginRevoked(plugin.Id, plugin.Version);
                var installed = PluginInstaller.IsInstalled(root, plugin);
                var on = installed && enabled.Contains(pin) && !removed;
                rows.Add(PluginRow(plugin, installed, on, removed));
            }

            foreach (var file in PluginInstaller.Scan(root, line => Log("Plugins: " + line)))
            {
                var pin = file.Id + "@" + file.Version;
                if (!shown.Add(pin))
                    continue;
                rows.Add(new PluginRowViewModel
                {
                    Id = file.Id,
                    Version = file.Version,
                    Title = file.Id,
                    Status = Loc.Get("plugins_removed"),
                    Meta = file.Version,
                    Removed = true,
                    ShowRemove = true,
                });
            }

            ListPlugins.ItemsSource = rows;
            if (TxtPluginsEmpty is not null)
                TxtPluginsEmpty.Visibility = rows.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            SetPluginsStatus(Loc.Format("plugins_status_n", rows.Count));
        }
        catch (Exception ex)
        {
            Log("Plugins: " + ex.Message);
            ListPlugins.ItemsSource = null;
            if (TxtPluginsEmpty is not null)
                TxtPluginsEmpty.Visibility = System.Windows.Visibility.Visible;
            SetPluginsStatus(Loc.Format("plugins_status_failed", ex.Message));
        }
        finally
        {
            _pluginsBusy = false;
        }
    }

    async Task InstallPluginRowAsync(PluginRowViewModel row)
    {
        if (_pluginsBusy || row.Plugin is null || row.Removed)
            return;
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
            return;
        _pluginsBusy = true;
        try
        {
            SetPluginsStatus(Loc.Get("plugins_install") + " " + row.Id);
            await PluginInstaller.InstallAsync(root, row.Plugin, handler: null, CancellationToken.None,
                line => Log("Plugins: " + line)).ConfigureAwait(true);
            SetPluginsStatus(Loc.Format("plugins_install_ok", row.Id));
        }
        catch (Exception ex)
        {
            Log("Plugins: " + ex.Message);
            SetPluginsStatus(Loc.Format("plugins_install_failed", row.Id, ex.Message));
        }
        finally
        {
            _pluginsBusy = false;
        }

        await RefreshPluginsAsync().ConfigureAwait(true);
    }

    async Task SetPluginEnabledAsync(PluginRowViewModel row, bool enabled)
    {
        if (_pluginsBusy || row.Removed)
            return;
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
            return;
        _pluginsBusy = true;
        try
        {
            if (!enabled)
            {
                PluginEnablement.Disable(root, row.Id);
            }
            else if (row.Plugin is not null)
            {
                var list = await CuratedCatalog.LoadAsync(root, refresh: false, CancellationToken.None,
                    log: line => Log("Plugins: " + line)).ConfigureAwait(true);
                var err = PluginEnablement.TryEnable(root, row.Plugin, list);
                if (err is not null)
                {
                    SetPluginsStatus(Loc.Format("plugins_enable_failed", row.Id));
                    Log("Plugins: enable " + row.Id + " " + err);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Plugins: " + ex.Message);
            SetPluginsStatus(ex.Message);
        }
        finally
        {
            _pluginsBusy = false;
        }

        await RefreshPluginsAsync().ConfigureAwait(true);
    }

    async Task RemovePluginRowAsync(PluginRowViewModel row)
    {
        if (_pluginsBusy || string.IsNullOrEmpty(row.Id))
            return;
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
            return;
        _pluginsBusy = true;
        try
        {
            var err = PluginInstaller.Uninstall(root, row.Id, line => Log("Plugins: " + line));
            if (err is not null)
                SetPluginsStatus(err);
        }
        catch (Exception ex)
        {
            Log("Plugins: " + ex.Message);
            SetPluginsStatus(ex.Message);
        }
        finally
        {
            _pluginsBusy = false;
        }

        await RefreshPluginsAsync().ConfigureAwait(true);
    }

    async Task<bool> EnsureJoinPluginsAsync(ServerListing listing)
    {
        if (listing.RequiredPlugins.Count == 0)
            return true;
        var root = ModsInstallRoot();
        if (!ModsRootUsable(root))
            return true;

        CuratedList list;
        try
        {
            SetBrowserStatus(Loc.Get("plugins_join_checking"));
            list = await CuratedCatalog.LoadAsync(root, refresh: true, CancellationToken.None,
                log: line => Log("Plugins: " + line)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log("Plugins: " + ex.Message);
            System.Windows.MessageBox.Show(this, Loc.Get("plugins_join_unavailable"), Loc.Get("tab_plugins"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return true;
        }

        var enabled = new HashSet<string>(PluginEnablement.Read(root), StringComparer.Ordinal);
        var rows = new List<ModPlanRow>();
        var toInstall = new List<CuratedPlugin>();
        var toEnable = new List<CuratedPlugin>();
        var seenRaw = new HashSet<string>(StringComparer.Ordinal);
        var seenPin = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var raw in listing.RequiredPlugins)
        {
            if (!seenRaw.Add(raw))
                continue;
            var plugin = PickClientPlugin(list, raw);
            if (plugin is not null && !seenPin.Add(plugin.Id + "@" + plugin.Version))
                continue;
            if (plugin is null)
            {
                rows.Add(new ModPlanRow
                {
                    Name = raw,
                    Detail = Loc.Get("plugins_join_skip"),
                    Tag = Loc.Get("plugins_join_skipped"),
                    TagKind = "off",
                    SizeText = "-",
                });
                Log("Plugins: not installing " + raw);
                continue;
            }

            var pin = plugin.Id + "@" + plugin.Version;
            if (PluginInstaller.IsInstalled(root, plugin))
            {
                if (!enabled.Contains(pin))
                    toEnable.Add(plugin);
                rows.Add(new ModPlanRow
                {
                    Name = plugin.Id,
                    Detail = plugin.Version,
                    Tag = Loc.Get("plugins_join_have"),
                    TagKind = "on",
                    SizeText = "-",
                });
                continue;
            }

            toInstall.Add(plugin);
            bytes += Math.Max(0, plugin.ArtifactSize);
            rows.Add(new ModPlanRow
            {
                Name = plugin.Id,
                Detail = PluginCapsText(plugin.Caps),
                Tag = Loc.Get("plugins_join_install"),
                TagKind = "new",
                SizeText = FormatBytes(plugin.ArtifactSize),
            });
        }

        if (toInstall.Count == 0 && toEnable.Count == 0)
            return true;

        var server = string.IsNullOrWhiteSpace(listing.Name) ? Loc.Get("join_server") : listing.Name;
        var ok = ModPlanWindow.Ask(this, new ModPlanSpec
        {
            Kicker = Loc.Get("plugins_join_kicker"),
            Headline = Loc.Format("plugins_join_headline", server),
            Body = Loc.Get("plugins_join_body"),
            Rows = rows,
            PrimaryText = Loc.Get("plugins_join_primary"),
            DownloadBytes = toInstall.Count > 0 ? bytes : -1,
            NeededBytes = bytes,
            FreeBytes = -1,
        });
        if (!ok)
        {
            SetBrowserStatus(Loc.Get("plugins_join_cancelled"));
            return false;
        }

        var changed = false;
        foreach (var plugin in toInstall)
        {
            try
            {
                await PluginInstaller.InstallAsync(root, plugin, handler: null, CancellationToken.None,
                    line => Log("Plugins: " + line)).ConfigureAwait(true);
                toEnable.Add(plugin);
                changed = true;
            }
            catch (Exception ex)
            {
                Log("Plugins: " + ex.Message);
                System.Windows.MessageBox.Show(this,
                    Loc.Format("plugins_install_failed", plugin.Id, ex.Message),
                    Loc.Get("tab_plugins"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                if (changed)
                    _sessionPluginsForceRelaunch = true;
                return false;
            }
        }

        foreach (var plugin in toEnable)
        {
            var err = PluginEnablement.TryEnable(root, plugin, list);
            if (err is null)
            {
                changed = true;
                continue;
            }

            Log("Plugins: enable " + plugin.Id + " " + err);
            SetBrowserStatus(Loc.Format("plugins_enable_failed", plugin.Id));
            if (changed)
                _sessionPluginsForceRelaunch = true;
            return false;
        }

        if (changed)
            _sessionPluginsForceRelaunch = true;
        return true;
    }

    static CuratedPlugin? PickClientPlugin(CuratedList list, string raw)
    {
        var at = raw.IndexOf('@');
        var id = at < 0 ? raw : raw[..at];
        var version = at < 0 ? null : raw[(at + 1)..];
        if (version is not null)
        {
            var exact = list.FindPlugin(id, version);
            return exact is not null && exact.ClientInstallable ? exact : null;
        }

        CuratedPlugin? best = null;
        foreach (var plugin in list.Plugins)
        {
            if (plugin.Id != id || list.IsPluginRevoked(plugin.Id, plugin.Version) || !plugin.ClientInstallable)
                continue;
            if (best is null || ModVersion.Compare(plugin.Version, best.Version) > 0)
                best = plugin;
        }

        return best;
    }

    static PluginRowViewModel PluginRow(CuratedPlugin plugin, bool installed, bool enabled, bool removed)
    {
        var tier = plugin.Tier == "native" ? Loc.Get("plugins_tier_native") : Loc.Get("plugins_tier_wasm");
        var realm = plugin.Realm switch
        {
            "client" => Loc.Get("plugins_realm_client"),
            "both" => Loc.Get("plugins_realm_both"),
            _ => Loc.Get("plugins_realm_server"),
        };
        var native = plugin.Tier == "native" && plugin.FirstParty;
        return new PluginRowViewModel
        {
            Plugin = plugin,
            Id = plugin.Id,
            Version = plugin.Version,
            Title = plugin.Id,
            Meta = tier + " | " + realm,
            Caps = PluginCapsText(plugin.Caps),
            Status = removed ? Loc.Get("plugins_removed") : enabled ? Loc.Get("plugins_on") : string.Empty,
            NativeNote = native ? Loc.Get("plugins_native_first_party") : string.Empty,
            ShowNativeNote = native,
            Removed = removed,
            ShowInstall = !removed && !installed,
            ShowEnable = !removed && installed && !enabled,
            ShowDisable = !removed && enabled,
            ShowRemove = installed || removed,
        };
    }

    static string PluginCapsText(IReadOnlyList<string> caps)
    {
        var parts = new List<string>();
        foreach (var cap in caps)
        {
            var sentence = cap switch
            {
                "log" => Loc.Get("plugins_cap_log"),
                "convars" => Loc.Get("plugins_cap_convars"),
                "script" => Loc.Get("plugins_cap_script"),
                "events" => Loc.Get("plugins_cap_events"),
                "usercmd" => Loc.Get("plugins_cap_usercmd"),
                "entities" => Loc.Get("plugins_cap_entities"),
                "net" => Loc.Get("plugins_cap_net"),
                "storage" => Loc.Get("plugins_cap_storage"),
                _ => string.Empty,
            };
            if (sentence.Length > 0)
                parts.Add(sentence);
        }

        return string.Join("; ", parts);
    }

    void SetPluginsStatus(string text)
    {
        if (TxtPluginsStatus is not null)
            TxtPluginsStatus.Text = text;
    }
}
